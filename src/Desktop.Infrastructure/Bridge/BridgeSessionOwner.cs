using Desktop.Application.Rendering;
using Desktop.Contracts.Bridge;
using Desktop.Contracts.Validation;
using Desktop.Application.Files;
using Desktop.Application.Psd;
using System.Text.Json;

namespace Desktop.Infrastructure.Bridge;

public sealed record BridgeSessionSnapshot(bool Listening, bool Paired, bool Connected, bool Ready, long Heartbeats, int LastEventSeq = 0,
    BridgeLoopbackHost.TransportFault? TransportFault = null, BridgeExecutionBroker.BindingFault? BindingFault = null,
    string? HostState = null, string? ModalState = null, DateTimeOffset? LastHeartbeatAt = null);

/// <summary>Owns one task's existing listener, sessions and broker. Never exposes pairing material or tokens.</summary>
public sealed class BridgeSessionOwner : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly BridgeAdapterSessions _sessions;
    private readonly BridgeExecutionBroker _broker;
    private readonly BridgeLoopbackHost _host;
    private readonly BridgePlanSubmissionService _submissions;
    private readonly RenderPlanValidator _plans;
    private readonly TimeProvider _time;
    private readonly HashSet<string> _capabilities;
    private readonly IDisposable? _hostSelectionLease;
    private readonly HashSet<string> _submittedKeys = new(StringComparer.Ordinal);
    private readonly Dictionary<string, BridgeOutputReservation> _outputs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _templates = new(StringComparer.Ordinal);
    private bool _started;
    private bool _disposed;

    // Coarse diagnostics remain readable after disposal; this contains no session material.
    public BridgeLoopbackHost.TransportFault? LastTransportFault => _host.LastTransportFault;
    public BridgeExecutionBroker.BindingFault? LastBindingFault => _broker.LastBindingFault;

    /// <summary>The caller retains responsibility for clearing its supplied secret after construction.</summary>
    public BridgeSessionOwner(byte[] secret, BridgeAdapterSessionPolicy policy,
        IBridgeExecutionJournalStore journal, TimeProvider? timeProvider = null, IBridgePeerGuard? peerGuard = null,
        IDisposable? hostSelectionLease = null)
    {
        ArgumentNullException.ThrowIfNull(secret);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(journal);
        var schemas = new JsonSchemaContractValidator();
        var wire = new BridgeWireValidator(schemas);
        var adapter = new BridgeAdapterValidator(schemas, wire);
        _time = timeProvider ?? TimeProvider.System;
        _capabilities = policy.Capabilities.ToHashSet(StringComparer.Ordinal);
        _plans = new(schemas);
        _sessions = new(secret, policy, wire, adapter, timeProvider);
        var validator = new BridgePlanSubmissionValidator(wire, _plans);
        _broker = new(policy.TaskId, validator, wire, journal, timeProvider);
        _submissions = new(validator, wire, _broker);
        _host = new(_sessions, _broker, adapter, wire, timeProvider, peerGuard);
        _hostSelectionLease = hostSelectionLease;
    }

    /// <summary>Creates private transport bindings inside the owner; no token or source path is returned.</summary>
    public Task<BridgeResult<BridgeResponseBodyV1>> SubmitPageAsync(string planJson,
        IReadOnlyList<BridgeResourceSource> sources, IPathPolicy paths, BridgeOutputReservation output,
        PsdInspectionDocument templateEvidence,
        CancellationToken cancellationToken) =>
        SubmitPageAsync(planJson, sources, paths, output, templateEvidence, static () => { }, cancellationToken);

    // Local preflight rejection is distinguishable from a possibly delivered submission.
    // Keep the public bridge result and wire contracts unchanged.
    internal async Task<BridgeResult<BridgeResponseBodyV1>> SubmitPageAsync(string planJson,
        IReadOnlyList<BridgeResourceSource> sources, IPathPolicy paths, BridgeOutputReservation output,
        PsdInspectionDocument templateEvidence, Action onSubmission, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        BridgeResourceLease? resources = null;
        var registered = false;
        var transferred = false;
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_started) return Failure("BRIDGE_DISCONNECTED");
            if (!_plans.Validate(planJson).IsValid) return Failure("BRIDGE_PLAN_INVALID");
            if (!RenderTemplateEvidenceValidator.Matches(planJson, templateEvidence)) return Failure("BRIDGE_PLAN_INVALID");
            using var document = JsonDocument.Parse(planJson);
            var plan = document.RootElement;
            if (plan.GetProperty("required_capabilities").EnumerateArray().Any(item => !_capabilities.Contains(item.GetString()!)))
                return Failure("BRIDGE_PLAN_INVALID");
            if (plan.GetProperty("batch_id").GetString() != _sessions.TaskId) return Failure("BRIDGE_PLAN_INVALID");
            var session = await _sessions.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
            var health = await _sessions.GetHealthAsync(cancellationToken).ConfigureAwait(false);
            if (session is null || !health.Connected) return Failure("BRIDGE_DISCONNECTED");
            if (!health.Ready) return Failure("BRIDGE_HOST_BLOCKED");
            var key = plan.GetProperty("idempotency_key").GetString()!;
            if (_submittedKeys.Contains(key) || await _broker.HasIdempotencyKeyAsync(key, cancellationToken).ConfigureAwait(false))
                return Failure("BRIDGE_EXECUTION_UNKNOWN");
            var digest = plan.GetProperty("plan_digest").GetString()!;
            output.EnsureMatches(digest, output.Binding);
            resources = await BridgeResourceLease.CreateAsync(planJson, session.SessionId, sources, paths, _plans, cancellationToken)
                .ConfigureAwait(false);
            var body = new BridgeRequestBodyV1<BridgePlanPayloadV1>($"request_{Guid.NewGuid():N}",
                plan.GetProperty("correlation_id").GetString()!, session.SessionId, session.Token, _time.GetUtcNow(), key,
                "submit_plan", new("page", plan.Clone(), digest, resources.Bindings, [output.Binding]));
            var request = JsonSerializer.Serialize(new BridgeWireEnvelope<BridgeRequestBodyV1<BridgePlanPayloadV1>>(
                BridgeProtocol.Version, "request", body), BridgeProtocol.JsonOptions);
            await _broker.RegisterResourcesAsync(resources, cancellationToken, output).ConfigureAwait(false);
            registered = true;
            // From this point, failure/unknown must be queried. Never automatically resubmit.
            onSubmission();
            _submittedKeys.Add(key);
            var result = await _submissions.SubmitAsync(request, cancellationToken).ConfigureAwait(false);
            if (result.IsSuccess)
            {
                _outputs.Add(result.Value!.ExecutionId!, output);
                _templates.Add(result.Value.ExecutionId!, sources.Single(source => source.Purpose == "template_copy").OwnedRelativePath);
                transferred = true;
            }
            return result;
        }
        catch (OperationCanceledException) when (registered) { return Failure("BRIDGE_EXECUTION_UNKNOWN"); }
        catch (InvalidOperationException exception) when (exception.Message == "BRIDGE_BUSY") { return Failure("BRIDGE_BUSY"); }
        catch (InvalidDataException) { return Failure("BRIDGE_RESOURCE_BINDING_INVALID"); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        { return Failure(registered ? "BRIDGE_EXECUTION_UNKNOWN" : "BRIDGE_RESOURCE_BINDING_INVALID"); }
        finally
        {
            try
            {
                if (registered && resources is not null) await _broker.ReleasePendingResourcesAsync(resources).ConfigureAwait(false);
                if (!transferred && resources is not null) await resources.DisposeAsync().ConfigureAwait(false);
            }
            finally { _gate.Release(); }
        }
    }

    public async Task<BridgeExecutionRecord?> QueryAsync(string executionId, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { ObjectDisposedException.ThrowIf(_disposed, this); return await _broker.QueryAsync(executionId, cancellationToken).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<BridgeExecutionRecord>> ListExecutionsAsync(CancellationToken cancellationToken)
    {
        using var performance = RenderPerformance.Start("bridge.executions.list");
        return await _broker.ListAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<RenderMediaEvidence> ValidateOutputAsync(string executionId, IPsdInspectionPort inspector,
        CancellationToken cancellationToken)
    {
        string template;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_templates.TryGetValue(executionId, out template!)) throw new InvalidDataException("RENDER_OUTPUT_NOT_READY");
        }
        finally { _gate.Release(); }
        return await _broker.ValidateOutputAsync(executionId, template, inspector, cancellationToken).ConfigureAwait(false);
    }

    public async Task CancelAsync(string executionId, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { ObjectDisposedException.ThrowIf(_disposed, this); await _broker.CancelAsync(executionId, cancellationToken).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    internal async Task ReleaseTerminalResourcesAsync(string executionId, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            await _broker.ReleaseTerminalResourcesAsync(executionId, cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public async Task<RenderPageCommitResult> CommitOutputAsync(string executionId, int attempt,
        IRenderPageCommitStore store, CancellationToken cancellationToken)
    {
        // Keep owner disposal outside this local commit boundary. The broker's
        // separate transport gate remains available to heartbeats and claims.
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return await _broker.CommitOutputAsync(executionId, attempt, store, cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public async Task<RenderPageCommitResult> RecoverOutputAsync(string executionId, string manifestRelativePath,
        IPathPolicy paths, IRenderPageCommitStore store, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return await _broker.RecoverOutputAsync(executionId, manifestRelativePath, paths, store, cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    private static BridgeResult<BridgeResponseBodyV1> Failure(string code)
    {
        var error = BridgeWireErrors.Create(code);
        return BridgeResults.Failure<BridgeResponseBodyV1>(error.Code, error.Retryable, error.Scope, error.UserAction);
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        using var performance = RenderPerformance.Start("bridge.owner.start");
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_started) throw new InvalidOperationException("BRIDGE_HOST_ALREADY_STARTED");
            try
            {
                await _host.StartAsync(cancellationToken).ConfigureAwait(false);
                _started = true;
            }
            catch
            {
                await CloseAsync().ConfigureAwait(false);
                throw;
            }
        }
        finally { _gate.Release(); }
    }

    public async Task<BridgeSessionSnapshot> GetStatusAsync(CancellationToken cancellationToken)
    {
        using var performance = RenderPerformance.Start("bridge.status");
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_started || _disposed) return new(false, false, false, false, 0);
            var paired = await _sessions.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
            var health = await _sessions.GetHealthAsync(cancellationToken).ConfigureAwait(false);
            return new(true, paired is not null, health.Connected, health.Ready, health.Heartbeats, health.LastEventSeq,
                _host.LastTransportFault, _broker.LastBindingFault, health.HostState, health.ModalState, health.LastHeartbeatAt);
        }
        finally { _gate.Release(); }
    }

    // page_completed confirms uploaded bytes, not plugin resource cleanup. Retain
    // the authenticated release boundary before sending another page or disposing.
    internal async Task<bool> WaitForReleaseAsync(int completedEventSeq, long baselineHeartbeats, CancellationToken cancellationToken)
    {
        var deadline = _time.GetUtcNow().AddSeconds(20);
        while (true)
        {
            var status = await GetStatusAsync(cancellationToken).ConfigureAwait(false);
            if (!status.Connected) return false;
            if (status.Ready && status.Heartbeats > baselineHeartbeats && status.LastEventSeq == completedEventSeq) return true;
            if (_time.GetUtcNow() >= deadline) return false;
            await Task.Delay(TimeSpan.FromMilliseconds(100), _time, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Stopping cannot be cancelled midway: leases, listener and session keys must all be released.</summary>
    public async ValueTask DisposeAsync()
    {
        using var performance = RenderPerformance.Start("bridge.owner.dispose");
        await _gate.WaitAsync().ConfigureAwait(false);
        try { await CloseAsync().ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    private async Task CloseAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _started = false;
        try { await _host.DisposeAsync().ConfigureAwait(false); }
        finally
        {
            try { _broker.Dispose(); }
            finally
            {
                try { foreach (var output in _outputs.Values) output.Dispose(); }
                finally
                {
                    _outputs.Clear(); _templates.Clear();
                    try { _sessions.Dispose(); }
                    finally { _hostSelectionLease?.Dispose(); }
                }
            }
        }
    }
}
