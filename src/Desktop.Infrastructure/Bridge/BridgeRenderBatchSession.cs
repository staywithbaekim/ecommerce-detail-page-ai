using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Desktop.Application.Files;
using Desktop.Application.Inputs;
using Desktop.Application.Psd;
using Desktop.Application.Rendering;
using Desktop.Domain.Inputs;
using Desktop.Infrastructure.Files;
using Desktop.Infrastructure.Psd;

namespace Desktop.Infrastructure.Bridge;

/// <summary>Production batch call site for the existing owner, resource lease, media validator and page committer.</summary>
public sealed class BridgeRenderBatchSessionFactory(IBatchRepository batches, BridgeSessionOwnerFactory owners,
    RenderBatchResourcePreparer resources, PsdInspectionWorkerOptions worker, IRenderPlanValidator plans,
    IRenderPageCommitStore commits, RenderStyleOutputPublisher? publicOutputs = null) : IRenderBatchSessionFactory, IPhotoshopAvailabilityPort
{
    private BridgeSessionOwner? _current;
    private BridgeLoopbackHost.TransportFault? _lastTransportFault;
    private BridgeExecutionBroker.BindingFault? _lastBindingFault;

    public async Task<PhotoshopAvailability> CheckAsync(CancellationToken cancellationToken)
    {
        var status = await GetStatusAsync(cancellationToken).ConfigureAwait(false);
        return new(status.Paired && status.Connected && status.Ready && status.Heartbeats >= 3,
            status.Ready ? "PHOTOSHOP_READY" : "PHOTOSHOP_WAITING_FOR_CONNECTION");
    }

    public Task<BridgeSessionSnapshot> GetStatusAsync(CancellationToken cancellationToken) =>
        Volatile.Read(ref _current) is { } owner ? owner.GetStatusAsync(cancellationToken)
            : Task.FromResult(new BridgeSessionSnapshot(false, false, false, false, 0,
                TransportFault: Volatile.Read(ref _lastTransportFault), BindingFault: Volatile.Read(ref _lastBindingFault)));

    public async Task<IRenderBatchSession> OpenAsync(RenderBatchJournal journal, CancellationToken cancellationToken)
    {
        var batch = await batches.GetAsync(journal.BatchId, cancellationToken).ConfigureAwait(false) ?? throw new InvalidOperationException("RENDER_BATCH_NOT_FOUND");
        // The output-intent persistence slice must not silently fall back to the legacy artifact layout.
        if (batch.OutputSelection is not null && publicOutputs is null)
            throw new InvalidOperationException("RENDER_OUTPUT_PUBLICATION_NOT_AVAILABLE");
        if (batch.ScanStatus != BatchScanStatus.ScanReady || !Path.IsPathFullyQualified(batch.OutputRoot))
            throw new InvalidOperationException("RENDER_BATCH_INPUT_INVALID");
        RenderStyleOutputPreparation? outputPins = null;
        if (batch.OutputSelection is not null)
        {
            try { outputPins = await publicOutputs!.PrepareForRenderAsync(journal, cancellationToken).ConfigureAwait(false); }
            catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException
                or InvalidOperationException or OperationCanceledException or System.Data.Common.DbException)
            { throw new InvalidOperationException("RENDER_OUTPUT_PREFLIGHT_REQUIRED", error); }
        }
        BridgeSessionOwner? owner = null;
        try
        {
            BridgeOutputReservation.ValidatePath(batch.OutputRoot);
            Directory.CreateDirectory(batch.OutputRoot);
            BridgeOutputReservation.ValidatePath(batch.OutputRoot);
            var paths = new LocalAppPathPolicy(batch.OutputRoot);
            var outputDirectory = "rendered/id-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(batch.BatchId)))[..32];
            var imageLayerContract = journal.Pages.Where(page => page.PlanJson is not null).Any(page =>
            {
                using var json = JsonDocument.Parse(page.PlanJson!);
                return json.RootElement.GetProperty("required_capabilities").EnumerateArray()
                    .Any(item => item.GetString() == RenderTemplateEvidenceValidator.RasterReplacementCapability);
            });
            var coveredStackContract = journal.Pages.Where(page => page.PlanJson is not null).Any(page =>
            {
                using var json = JsonDocument.Parse(page.PlanJson!);
                return json.RootElement.GetProperty("required_capabilities").EnumerateArray()
                    .Any(item => item.GetString() == RenderTemplateEvidenceValidator.CoveredRasterStackCapability);
            });
            owner = await owners.StartTaskOwnerAsync(batch.BatchId, cancellationToken, journal.Version == 4,
                imageLayerContract, coveredStackContract).ConfigureAwait(false);
            Volatile.Write(ref _lastTransportFault, null);
            Volatile.Write(ref _lastBindingFault, null);
            Volatile.Write(ref _current, owner);
            return new BridgeRenderBatchSession(batch, owner, resources, paths, outputDirectory,
                new PsdInspectionWorkerClient(paths, worker), plans, commits,
                () =>
                {
                    Volatile.Write(ref _lastTransportFault, owner.LastTransportFault);
                    Volatile.Write(ref _lastBindingFault, owner.LastBindingFault);
                    Interlocked.CompareExchange(ref _current, null, owner);
                    try { owners.RecordSessionFaults(batch.BatchId, owner.LastTransportFault, owner.LastBindingFault); }
                    finally { outputPins?.Dispose(); }
                }, page => outputPins?.VerifyBeforeDispatch(page));
        }
        catch
        {
            try { if (owner is not null) await owner.DisposeAsync().ConfigureAwait(false); }
            finally { outputPins?.Dispose(); }
            throw;
        }
    }
}

public sealed class BridgeRenderBatchSession(BatchDraft batch, BridgeSessionOwner owner,
    RenderBatchResourcePreparer resources, IPathPolicy paths, string outputDirectory,
    IPsdInspectionPort inspector, IRenderPlanValidator plans, IRenderPageCommitStore commits, Action? onDisposed = null,
    Action<RenderBatchPage>? beforeDispatch = null) : IRenderBatchSession
{
    private readonly Dictionary<string, PreparedRenderResources> _prepared = new(StringComparer.Ordinal);
    public async Task<bool> IsReadyAsync(CancellationToken cancellationToken)
    {
        var status = await owner.GetStatusAsync(cancellationToken).ConfigureAwait(false);
        return status.Paired && status.Connected && status.Ready && status.Heartbeats >= 3;
    }

    public async Task<RenderPageObservation> SubmitAsync(RenderBatchPage page, CancellationToken cancellationToken)
    {
        using var performance = RenderPerformance.Start("page.submit", page.PageId);
        BridgeOutputReservation? output = null;
        PreparedRenderResources? prepared = null;
        var submitting = false;
        try
        {
            beforeDispatch?.Invoke(page);
            if (!plans.Validate(page.PlanJson!).IsValid) throw new InvalidDataException("RENDER_PLAN_INVALID");
            prepared = await resources.PrepareAsync(batch, page, paths, cancellationToken).ConfigureAwait(false);
            output = BridgeOutputReservation.Create(page.PlanJson!, outputDirectory, paths, plans);
            output.TemporaryFiles = prepared.TemporaryFiles;
            var result = await RenderPerformance.MeasureAsync("bridge.submit", () => owner.SubmitPageAsync(
                page.PlanJson!, prepared.Sources, paths, output, prepared.Template,
                () => submitting = true, cancellationToken)).ConfigureAwait(false);
            if (!result.IsSuccess)
            {
                output.Dispose();
                if (!submitting) prepared.Release();
                return new(submitting || result.Error!.Code == "BRIDGE_EXECUTION_UNKNOWN"
                    ? RenderBatchPageStatus.Unknown : RenderBatchPageStatus.Failed, result.Error!.Code);
            }
            output = null; // ownership transferred
            _prepared.Add(result.Value!.ExecutionId!, prepared);
            return new(RenderBatchPageStatus.Running, "RENDER_EXECUTING", result.Value!.ExecutionId!);
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException or OperationCanceledException)
        {
            output?.Dispose();
            if (!submitting) prepared?.Release();
            return new(submitting ? RenderBatchPageStatus.Unknown : RenderBatchPageStatus.Failed,
                submitting ? "RENDER_RECOVERY_REQUIRED" : "RENDER_RESOURCE_PREPARATION_FAILED");
        }
    }

    public async Task<RenderPageObservation> ObserveAsync(RenderBatchPage page, bool recovering, CancellationToken cancellationToken)
    {
        if (page.PlanJson is null) return new(page.Status, page.ReasonCode);
        using var document = JsonDocument.Parse(page.PlanJson);
        var plan = document.RootElement;
        var executions = await owner.ListExecutionsAsync(cancellationToken).ConfigureAwait(false);
        var record = executions.SingleOrDefault(item => item.IdempotencyKey == plan.GetProperty("idempotency_key").GetString());
        if (record is null)
            return page.Status == RenderBatchPageStatus.Failed && page.ExecutionId is null
                ? new(page.Status, page.ReasonCode) : Unknown(page.ExecutionId);
        if (record.Instance != page.Instance || RenderPlanInstanceIdentity.Read(plan) != page.Instance
            || record.PlanDigest != plan.GetProperty("plan_digest").GetString() || record.PageId != page.PageId
            || record.TaskId != batch.BatchId || page.ExecutionId is not null && page.ExecutionId != record.ExecutionId) return Unknown(page.ExecutionId);
        if (record.State is "failed" or "cancelled")
        {
            await owner.ReleaseTerminalResourcesAsync(record.ExecutionId, cancellationToken).ConfigureAwait(false);
            ReleasePrepared(record.ExecutionId);
            await resources.ReleaseTerminalAsync(page.PlanJson, paths, record, cancellationToken).ConfigureAwait(false);
            return new(record.State == "failed" ? RenderBatchPageStatus.Failed : RenderBatchPageStatus.Cancelled,
                record.Events[^1].Error?.Code ?? "RENDER_CANCELLED", record.ExecutionId);
        }
        try
        {
            RenderPageCommitResult committed;
            if (recovering || record.State == "completed")
            {
                var final = Path.Combine(outputDirectory, plan.GetProperty("output").GetProperty("artifact_set_id").GetString()!, "manifest.json");
                var stage = Path.Combine(RenderPageCommitter.StagingDirectory(outputDirectory, batch.BatchId, page.StyleId,
                    page.PageId, record.PlanDigest), "page", "manifest.json");
                // Only these two exact plan-derived locations can supply recovery evidence.
                var manifest = File.Exists(paths.ResolveOwnedPath(final)) ? final : stage;
                committed = await owner.RecoverOutputAsync(record.ExecutionId, manifest, paths, commits, cancellationToken).ConfigureAwait(false);
            }
            else if (record.State == "running" && !record.CancelRequested && record.Events[^1].EventType == "page_completed")
            {
                await RenderPerformance.MeasureAsync("output.validate", () => owner.ValidateOutputAsync(
                    record.ExecutionId, inspector, cancellationToken)).ConfigureAwait(false);
                // Capture before commit: the plugin can acknowledge release as soon as
                // execution_completed is persisted, even before CommitOutputAsync returns.
                var baseline = await owner.GetStatusAsync(cancellationToken).ConfigureAwait(false);
                committed = await RenderPerformance.MeasureAsync("output.commit", () => owner.CommitOutputAsync(
                    record.ExecutionId, page.Attempt, commits, cancellationToken)).ConfigureAwait(false);
                using var readyPerformance = RenderPerformance.Start("output.wait_ready_ack");
                if (!await owner.WaitForReleaseAsync(record.EventSeq + 1, baseline.Heartbeats, cancellationToken).ConfigureAwait(false))
                    return Unknown(record.ExecutionId);
            }
            else
            {
                return record.State == "unknown" ? Unknown(record.ExecutionId)
                    : new(RenderBatchPageStatus.Running, "RENDER_EXECUTING", record.ExecutionId);
            }
            if (committed.Checkpoint.Page.Instance != page.Instance || committed.Checkpoint.Page.OutputVariantId != page.OutputVariantId || committed.Checkpoint.Page.Attempt != page.Attempt
                || page.Commit is not null && committed.Checkpoint != page.Commit) return Unknown(record.ExecutionId);
            using var cleanupPerformance = RenderPerformance.Start("output.release_resources");
            await owner.ReleaseTerminalResourcesAsync(record.ExecutionId, cancellationToken).ConfigureAwait(false);
            ReleasePrepared(record.ExecutionId);
            // Recovery may have advanced Unknown to Completed while reconciling the manifest/checkpoint.
            var terminal = (await owner.ListExecutionsAsync(cancellationToken).ConfigureAwait(false))
                .Single(item => item.ExecutionId == record.ExecutionId);
            await resources.ReleaseTerminalAsync(page.PlanJson, paths, terminal, cancellationToken).ConfigureAwait(false);
            return new(RenderBatchPageStatus.Committed, "RENDER_PAGE_COMMITTED", record.ExecutionId, committed.Checkpoint);
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException or OperationCanceledException)
        {
            return Unknown(record.ExecutionId);
        }
    }

    public Task CancelAsync(RenderBatchPage page, CancellationToken cancellationToken) => page.ExecutionId is null
        ? Task.CompletedTask : owner.CancelAsync(page.ExecutionId, cancellationToken);

    public async ValueTask DisposeAsync()
    {
        try { await owner.DisposeAsync().ConfigureAwait(false); }
        finally { onDisposed?.Invoke(); }
    }

    private static RenderPageObservation Unknown(string? execution) => new(RenderBatchPageStatus.Unknown, "RENDER_RECOVERY_REQUIRED", execution);

    private void ReleasePrepared(string execution)
    {
        if (!_prepared.TryGetValue(execution, out var prepared)) return;
        prepared.Release();
        _prepared.Remove(execution);
    }
}
