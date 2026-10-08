using System.Text.Json;
using System.Security.Cryptography;
using Desktop.Application.Rendering;
using Desktop.Contracts.Bridge;

namespace Desktop.Infrastructure.Bridge;

/// <summary>One Photoshop channel. Durable delivery facts precede responses and survive lost acknowledgements.</summary>
public sealed partial class BridgeExecutionBroker(
    string taskId, BridgePlanSubmissionValidator submissions, BridgeWireValidator wire,
    IBridgeExecutionJournalStore journal, TimeProvider? timeProvider = null) : IBridgePlanSubmissionPort, IDisposable
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, BridgeExecutionRecord> _records = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Delivery> _deliveries = new(StringComparer.Ordinal);
    private bool _initialized;
    private bool _poisoned;
    private bool _disposed;
    private BridgeResourceLease? _pendingResources;
    private BridgeOutputReservation? _pendingOutput;
    // Local diagnostics contain only fixed stages and validated journal states, never request material.
    public sealed record BindingFault(string Operation, string Stage, string ExecutionState);
    public BindingFault? LastBindingFault { get; private set; }

    /// <summary>Transfers ownership after success. The desktop owner supplies only verified, opened resources.</summary>
    public async Task RegisterResourcesAsync(BridgeResourceLease resources, CancellationToken cancellationToken,
        BridgeOutputReservation? output = null)
    {
        ArgumentNullException.ThrowIfNull(resources);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureReady();
            if (_pendingResources is not null || _records.Values.Any(r => !IsTerminal(r.State)))
                throw new InvalidOperationException("BRIDGE_BUSY");
            _pendingResources = resources;
            _pendingOutput = output;
        }
        finally { _gate.Release(); }
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_initialized) return;
            var previous = await journal.LoadAsync(cancellationToken).ConfigureAwait(false);
            if (previous is not null)
            {
                ValidateJournal(previous);
                foreach (var record in previous.Executions)
                {
                    _records.Add(record.ExecutionId, IsTerminal(record.State) ? record : record with { State = "unknown" });
                }
                await Save(cancellationToken).ConfigureAwait(false);
            }
            _initialized = true;
        }
        finally { _gate.Release(); }
    }

    // The owner may release only the exact unpublished lease it supplied, never another page.
    internal async Task ReleasePendingResourcesAsync(BridgeResourceLease resources)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (ReferenceEquals(_pendingResources, resources))
            {
                _pendingResources = null;
                _pendingOutput = null;
                await resources.DisposeAsync().ConfigureAwait(false);
            }
        }
        finally { _gate.Release(); }
    }

    internal async Task<bool> HasIdempotencyKeyAsync(string key, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { EnsureReady(); return _records.Values.Any(record => record.TaskId == taskId && record.IdempotencyKey == key); }
        finally { _gate.Release(); }
    }

    public async Task<BridgePlanDeliveryResult> SubmitAsync(string requestJson, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureReady();
            var valid = submissions.Validate(requestJson);
            if (!valid.IsValid) return new(BridgePlanDelivery.NotSent);
            using var request = JsonDocument.Parse(requestJson);
            var body = request.RootElement.GetProperty("body");
            var payload = body.GetProperty("payload");
            var plan = payload.GetProperty("render_plan");
            if (plan.GetProperty("batch_id").GetString() != taskId) return Reply(requestJson, null, "BRIDGE_PLAN_INVALID");
            var key = body.GetProperty("idempotency_key").GetString()!;
            var digest = payload.GetProperty("plan_digest").GetString()!;
            var existing = _records.Values.SingleOrDefault(r => r.TaskId == taskId && r.IdempotencyKey == key);
            if (existing is not null)
                return Reply(requestJson, existing, string.Equals(existing.PlanDigest, digest, StringComparison.OrdinalIgnoreCase)
                    ? null : "BRIDGE_PLAN_INVALID");
            if (_records.Values.Any(r => !IsTerminal(r.State)) || _records.Count >= 10000)
                return Reply(requestJson, null, "BRIDGE_BUSY");
            var bindings = JsonSerializer.Deserialize<BridgeResourceBindingV1[]>(payload.GetProperty("resource_bindings"), BridgeProtocol.JsonOptions)!;
            if (_pendingResources is null || !await _pendingResources.MatchesAsync(body.GetProperty("session_id").GetString()!,
                digest, bindings, cancellationToken).ConfigureAwait(false))
                return Reply(requestJson, null, "BRIDGE_RESOURCE_BINDING_INVALID");
            if (_pendingOutput is not null)
            {
                _pendingOutput.EnsureMatches(digest, _pendingOutput.Binding);
                var outputs = payload.GetProperty("output_bindings");
                if (outputs.GetArrayLength() != 1
                    || outputs[0].GetProperty("output_root_handle").GetString() != _pendingOutput.Binding.OutputRootHandle
                    || outputs[0].GetProperty("artifact_set_id").GetString() != _pendingOutput.Binding.ArtifactSetId
                    || outputs[0].GetProperty("overwrite_policy").GetString() != "never"
                    || outputs[0].GetProperty("confirmed_target_ids").GetArrayLength() != 0)
                    return Reply(requestJson, null, "BRIDGE_RESOURCE_BINDING_INVALID");
            }
            var id = $"execution_{Guid.NewGuid():N}";
            var accepted = new BridgeEventV1($"event_{Guid.NewGuid():N}", 1, _time.GetUtcNow(),
                "execution_accepted", id, "accepted");
            var record = new BridgeExecutionRecord(taskId, id, key, digest,
                plan.GetProperty("document").GetProperty("page_id").GetString()!, "accepted", false, 1, [accepted], RenderPlanInstanceIdentity.Read(plan));
            _records.Add(id, record);
            await Save(cancellationToken).ConfigureAwait(false);
            _deliveries.Add(id, new Delivery(body.GetProperty("session_id").GetString()!, requestJson,
                $"claim_{Guid.NewGuid():N}", _pendingResources, _pendingOutput));
            _pendingResources = null;
            _pendingOutput = null;
            return Reply(requestJson, record);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            _poisoned = true;
            await ReleaseResources().ConfigureAwait(false);
            return new(BridgePlanDelivery.Uncertain);
        }
        finally { _gate.Release(); }
    }

    public async Task<BridgeResult<AdapterClaimResponse>> ClaimAsync(string sessionId, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureReady();
            var record = _records.Values.SingleOrDefault(r => r.TaskId == taskId && !IsTerminal(r.State));
            if (record is null) return BridgeResults.Success(new AdapterClaimResponse(false));
            if (!_deliveries.TryGetValue(record.ExecutionId, out var delivery) || delivery.SessionId != sessionId)
                return Failure<AdapterClaimResponse>("BRIDGE_EXECUTION_UNKNOWN");
            if (record.State == "accepted")
            {
                record = record with { State = "claimed" };
                _records[record.ExecutionId] = record;
                await Save(cancellationToken).ConfigureAwait(false);
            }
            return BridgeResults.Success(new AdapterClaimResponse(true, Claim(record, delivery)));
        }
        finally { _gate.Release(); }
    }

    public async Task<BridgeResult<AdapterClaimResponse>> AcknowledgeAsync(string sessionId,
        AdapterClaimAcknowledgement acknowledgement, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureReady();
            if (!Owns(sessionId, acknowledgement.ExecutionId, acknowledgement.ClaimId, out var record, out var delivery)
                || !string.Equals(record!.PlanDigest, acknowledgement.PlanDigest, StringComparison.OrdinalIgnoreCase))
                return Failure<AdapterClaimResponse>("BRIDGE_EXECUTION_NOT_FOUND");
            if (record.State == "claimed" && !record.CancelRequested)
            {
                record = record with { State = "authorized" };
                _records[record.ExecutionId] = record;
                await Save(cancellationToken).ConfigureAwait(false);
            }
            return BridgeResults.Success(new AdapterClaimResponse(true, Claim(record, delivery!)));
        }
        finally { _gate.Release(); }
    }

    public async Task<BridgeResult<AdapterEventReceipt>> ReportAsync(string sessionId, AdapterEventReport report,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureReady();
            if (!wire.Validate(report.Event.GetRawText(), BridgeEndpoint.Event).IsValid)
                return Failure<AdapterEventReceipt>("BRIDGE_PROTOCOL_MISMATCH");
            BridgeEventV1 item;
            try { item = JsonSerializer.Deserialize<BridgeWireEnvelope<BridgeEventV1>>(report.Event, BridgeProtocol.JsonOptions)!.Body; }
            catch (JsonException) { return Failure<AdapterEventReceipt>("BRIDGE_PROTOCOL_MISMATCH"); }
            if (!Owns(sessionId, item.ExecutionId, report.ClaimId, out var record, out var eventDelivery))
                return Failure<AdapterEventReceipt>("BRIDGE_EXECUTION_NOT_FOUND");
            // Byte receipts are not publication. The desktop commit/checkpoint gate is still closed.
            if (item.EventType == "execution_completed" && eventDelivery!.Output is not null)
                return Failure<AdapterEventReceipt>("BRIDGE_EXECUTION_UNKNOWN");
            var duplicate = _records.Values.SelectMany(r => r.Events).SingleOrDefault(e => e.EventId == item.EventId);
            if (duplicate is not null)
                return JsonSerializer.Serialize(duplicate, BridgeProtocol.JsonOptions) == JsonSerializer.Serialize(item, BridgeProtocol.JsonOptions)
                    ? BridgeResults.Success(new AdapterEventReceipt(item.ExecutionId, item.EventSeq))
                    : Failure<AdapterEventReceipt>("BRIDGE_PROTOCOL_MISMATCH");
            if (_committingOutputs.Contains(item.ExecutionId)) return Failure<AdapterEventReceipt>("BRIDGE_EXECUTION_UNKNOWN");
            if (record!.Events.Count >= 256 || item.EventSeq != record.EventSeq + 1
                || (item.PageId is not null && item.PageId != record.PageId) || !Transition(record, item))
                return Failure<AdapterEventReceipt>("BRIDGE_PROTOCOL_MISMATCH");
            // page_completed is not an execution terminal; output publication remains the desktop's job.
            var state = item.EventType == "page_completed" ? "running" : item.Status;
            _records[record.ExecutionId] = record with { State = state, EventSeq = item.EventSeq, Events = [.. record.Events, item] };
            await Save(cancellationToken).ConfigureAwait(false);
            if (IsTerminal(state) || state == "unknown")
            {
                await _deliveries[record.ExecutionId].Resources.DisposeAsync().ConfigureAwait(false);
                _deliveries[record.ExecutionId].Output?.Dispose();
            }
            return BridgeResults.Success(new AdapterEventReceipt(item.ExecutionId, item.EventSeq));
        }
        finally { _gate.Release(); }
    }

    public async Task<BridgeResult<AdapterResourceChunk>> ReadResourceAsync(string sessionId,
        AdapterResourceRead read, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureReady();
            if (!Owns(sessionId, read.ExecutionId, read.ClaimId, out var record, out var delivery)
                || record!.State is not ("claimed" or "authorized" or "running")
                || !string.Equals(read.PlanDigest, record.PlanDigest, StringComparison.OrdinalIgnoreCase)
                || read.Count is < 1 or > BridgeAdapterProtocol.ResourceChunkByteLimit)
            {
                LastBindingFault = new("resource_read", "ownership_or_state", record?.State ?? "missing");
                return Failure<AdapterResourceChunk>("BRIDGE_RESOURCE_BINDING_INVALID");
            }
            if (record.CancelRequested)
            {
                // The read lease is already revoked. Check immutable delivery metadata, never
                // reopen it or disclose cancellation to a different resource/claim/session.
                using var submitted = JsonDocument.Parse(delivery!.RequestJson);
                var bindings = submitted.RootElement.GetProperty("body").GetProperty("payload").GetProperty("resource_bindings");
                var binding = bindings.EnumerateArray().SingleOrDefault(item => item.GetProperty("session_handle").GetString() == read.SessionHandle);
                if (binding.ValueKind != JsonValueKind.Undefined && read.Offset >= 0
                    && read.Offset < binding.GetProperty("byte_length").GetInt64()
                    && read.Count <= binding.GetProperty("byte_length").GetInt64() - read.Offset)
                    return Failure<AdapterResourceChunk>("BRIDGE_CANCEL_PENDING");
                LastBindingFault = new("resource_read", "binding_or_range", record.State);
                return Failure<AdapterResourceChunk>("BRIDGE_RESOURCE_BINDING_INVALID");
            }
            var bytes = await delivery!.Resources.ReadChunkAsync(sessionId, read.PlanDigest, read.SessionHandle,
                read.Offset, read.Count, cancellationToken).ConfigureAwait(false);
            return BridgeResults.Success(new AdapterResourceChunk(read.ExecutionId, record.PlanDigest, read.SessionHandle,
                read.Offset, bytes.Length, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
                BridgeAdapterCryptography.Encode(bytes)));
        }
        catch (InvalidDataException)
        {
            LastBindingFault = new("resource_read", "lease", "unavailable");
            return Failure<AdapterResourceChunk>("BRIDGE_RESOURCE_BINDING_INVALID");
        }
        finally { _gate.Release(); }
    }

    public async Task<BridgeExecutionRecord?> QueryAsync(string executionId, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureReady();
            return _records.TryGetValue(executionId, out var record) && record.TaskId == taskId
                ? record with { Events = record.Events.ToArray() } : null;
        }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<BridgeExecutionRecord>> ListAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureReady();
            return _records.Values.Where(record => record.TaskId == taskId)
                .Select(record => record with { Events = record.Events.ToArray() }).ToArray();
        }
        finally { _gate.Release(); }
    }

    internal async Task ReleaseTerminalResourcesAsync(string executionId, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureReady();
            if (!_records.TryGetValue(executionId, out var record) || record.TaskId != taskId || !IsTerminal(record.State)
                || _committingOutputs.Contains(executionId) || _validatingOutputs.Contains(executionId))
                throw new InvalidDataException("RENDER_OUTPUT_NOT_READY");
            // A recovered owner has no file ownership objects. It must not sweep historical paths.
            if (_deliveries.TryGetValue(executionId, out var delivery))
            {
                await delivery.Resources.DisposeAsync().ConfigureAwait(false);
                delivery.Output?.ReleaseAfterTerminal();
            }
        }
        finally { _gate.Release(); }
    }

    public async Task CancelAsync(string executionId, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureReady();
            // Once local commit starts, finish/query that page before applying a
            // user cancellation to subsequent pages. Never dispose commit leases.
            if (_committingOutputs.Contains(executionId)) return;
            if (_records.TryGetValue(executionId, out var record) && record.TaskId == taskId && !IsTerminal(record.State))
            {
                _records[executionId] = record with { CancelRequested = true };
                await Save(cancellationToken).ConfigureAwait(false);
                if (_deliveries.TryGetValue(executionId, out var delivery))
                {
                    await delivery.Resources.DisposeAsync().ConfigureAwait(false);
                    delivery.Output?.Dispose();
                }
            }
        }
        finally { _gate.Release(); }
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            await ReleaseResources().ConfigureAwait(false);
            EnsureReady();
            foreach (var record in _records.Values.Where(r => r.TaskId == taskId && !IsTerminal(r.State)).ToArray())
                _records[record.ExecutionId] = record with { State = "unknown" };
            foreach (var key in _deliveries.Keys.Where(key => !_committingOutputs.Contains(key)).ToArray())
                _deliveries.Remove(key);
            await Save(cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    private bool Owns(string session, string execution, string claim, out BridgeExecutionRecord? record, out Delivery? delivery)
    {
        _records.TryGetValue(execution, out record);
        _deliveries.TryGetValue(execution, out delivery);
        return record?.TaskId == taskId && delivery?.SessionId == session && delivery.ClaimId == claim;
    }

    private static AdapterClaim Claim(BridgeExecutionRecord record, Delivery delivery)
    {
        using var plan = record.State == "claimed" ? JsonDocument.Parse(delivery.RequestJson) : null;
        return new(delivery.ClaimId, record.ExecutionId, record.PlanDigest, record.State, record.CancelRequested,
            plan?.RootElement.Clone());
    }

    private BridgePlanDeliveryResult Reply(string requestJson, BridgeExecutionRecord? record, string? error = null)
    {
        using var document = JsonDocument.Parse(requestJson);
        var body = document.RootElement.GetProperty("body");
        var response = new BridgeResponseBodyV1(error is null, body.GetProperty("request_id").GetString()!,
            body.GetProperty("correlation_id").GetString()!, error is null ? "accepted" : "rejected", _time.GetUtcNow(),
            error is null ? record!.ExecutionId : null, error is null ? WireState(record!.State) : null,
            error is null ? record!.EventSeq : null, Error: error is null ? null : BridgeWireErrors.Create(error));
        return new(BridgePlanDelivery.ResponseReceived, JsonSerializer.Serialize(new BridgeWireEnvelope<BridgeResponseBodyV1>(
            BridgeProtocol.Version, "response", response), BridgeProtocol.JsonOptions));
    }

    public static string WireState(string state) => state is "claimed" or "authorized" ? "accepted" : state;
    private static bool IsTerminal(string state) => state is "completed" or "failed" or "cancelled";

    private static bool Transition(BridgeExecutionRecord record, BridgeEventV1 item) => item.EventType switch
    {
        "page_started" => record.State == "authorized" && !record.CancelRequested && item.Status == "running" && item.PageId == record.PageId,
        "page_completed" => record.State == "running" && item.Status == "running" && item.PageId == record.PageId,
        "execution_completed" => record.State == "running" && item.Status == "completed"
            && record.Events[^1].EventType == "page_completed",
        "page_failed" or "execution_failed" => record.State is "authorized" or "running" && item.Status == "failed" && item.Error is not null,
        "execution_cancelled" => record.CancelRequested && record.State is "claimed" or "authorized" or "running" && item.Status == "cancelled",
        "host_blocked" => record.State is "authorized" or "running" && item.Status == "unknown" && item.Error is not null,
        _ => false
    };

    private void ValidateJournal(BridgeExecutionJournal previous)
    {
        if (previous.Version is not (1 or 2) || previous.Executions is null || previous.Executions.Count > 10000
            || previous.Version == 2 && !previous.Executions.Any(record => record?.Instance is not null))
            throw new InvalidDataException("BRIDGE_JOURNAL_INVALID");
        var executions = new HashSet<string>(StringComparer.Ordinal);
        var keys = new HashSet<(string, string)>();
        var events = new HashSet<string>(StringComparer.Ordinal);
        foreach (var record in previous.Executions)
        {
            if (record is null || !ValidId(record.TaskId) || !ValidId(record.ExecutionId)
                || record.Instance is { } instance && (previous.Version != 2 || !RenderPlanInstanceIdentity.IsValid(instance)
                    || instance.BatchId != record.TaskId || instance.SourcePageId != record.PageId)
                || !ValidKey(record.IdempotencyKey) || !ValidId(record.PageId)
                || record.PlanDigest is not { Length: 71 } || !record.PlanDigest.StartsWith("sha256:", StringComparison.Ordinal)
                || record.PlanDigest.Skip(7).Any(c => !char.IsAsciiHexDigit(c))
                || record.State is not ("accepted" or "claimed" or "authorized" or "running" or "completed" or "failed" or "cancelled" or "unknown")
                || record.Events is null || record.EventSeq is < 1 or > 256 || record.Events.Count != record.EventSeq
                || !executions.Add(record.ExecutionId) || !keys.Add((record.TaskId, record.IdempotencyKey)))
                throw new InvalidDataException("BRIDGE_JOURNAL_INVALID");
            var replay = record with { State = "accepted", Events = [], EventSeq = 0 };
            foreach (var item in record.Events)
            {
                if (item is null || item.EventSeq != replay.EventSeq + 1 || item.ExecutionId != record.ExecutionId
                    || (item.PageId is not null && item.PageId != record.PageId) || !events.Add(item.EventId)
                    || !wire.Validate(JsonSerializer.Serialize(new BridgeWireEnvelope<BridgeEventV1>(
                        BridgeProtocol.Version, "event", item), BridgeProtocol.JsonOptions), BridgeEndpoint.Event).IsValid)
                    throw new InvalidDataException("BRIDGE_JOURNAL_INVALID");
                if (item.EventSeq == 1)
                {
                    if (item.EventType != "execution_accepted" || item.Status != "accepted" || item.PageId is not null || item.Error is not null)
                        throw new InvalidDataException("BRIDGE_JOURNAL_INVALID");
                }
                else
                {
                    // Claim/ACK and the cancel request are persisted state, not transport events.
                    // A later cancel must not invalidate the earlier page_started event during replay.
                    if (replay.State == "accepted") replay = replay with { State = "authorized" };
                    replay = replay with { CancelRequested = item.EventType == "execution_cancelled" && record.CancelRequested };
                    if (!Transition(replay, item)) throw new InvalidDataException("BRIDGE_JOURNAL_INVALID");
                }
                replay = replay with { State = item.Status, EventSeq = item.EventSeq, Events = [.. replay.Events, item] };
            }
            var consistent = replay.State == "accepted"
                ? record.State is "accepted" or "claimed" or "authorized" or "unknown"
                : replay.State == record.State || (!IsTerminal(replay.State) && record.State == "unknown");
            if (!consistent) throw new InvalidDataException("BRIDGE_JOURNAL_INVALID");
        }
    }

    private static bool ValidId(string? value) => value is { Length: > 0 and <= 128 }
        && char.IsAsciiLetterOrDigit(value[0])
        && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or ':' or '-');

    private static bool ValidKey(string? value) => value is { Length: >= 8 and <= 200 }
        && char.IsAsciiLetterOrDigit(value[0])
        && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or ':' or '/' or '-');

    private async Task Save(CancellationToken cancellationToken)
    {
        try { await journal.SaveAsync(new(_records.Values.Any(record => record.Instance is not null) ? 2 : 1, _records.Values.ToArray()), cancellationToken).ConfigureAwait(false); }
        catch { _poisoned = true; await ReleaseResources().ConfigureAwait(false); throw; }
    }

    private void EnsureReady()
    {
        if (_disposed || !_initialized || _poisoned) throw new InvalidOperationException("BRIDGE_EXECUTION_UNKNOWN");
    }

    private static BridgeResult<T> Failure<T>(string code)
    {
        var error = BridgeWireErrors.Create(code);
        return BridgeResults.Failure<T>(error.Code, error.Retryable, error.Scope, error.UserAction);
    }

    private async Task ReleaseResources()
    {
        if (_pendingResources is not null) await _pendingResources.DisposeAsync().ConfigureAwait(false);
        _pendingResources = null;
        _pendingOutput?.Dispose();
        _pendingOutput = null;
        foreach (var delivery in _deliveries.Where(pair => !_committingOutputs.Contains(pair.Key)).Select(pair => pair.Value))
        {
            await delivery.Resources.DisposeAsync().ConfigureAwait(false);
            delivery.Output?.Dispose();
        }
    }

    private sealed record Delivery(string SessionId, string RequestJson, string ClaimId, BridgeResourceLease Resources,
        BridgeOutputReservation? Output);

    public void Dispose()
    {
        _gate.Wait();
        try
        {
            if (_disposed) return;
            _disposed = true;
            _pendingResources?.Dispose();
            _pendingResources = null;
            _pendingOutput?.Dispose();
            _pendingOutput = null;
            foreach (var delivery in _deliveries.Values)
            {
                delivery.Resources.Dispose();
                delivery.Output?.Dispose();
            }
            _deliveries.Clear();
        }
        finally { _gate.Release(); }
    }
}
