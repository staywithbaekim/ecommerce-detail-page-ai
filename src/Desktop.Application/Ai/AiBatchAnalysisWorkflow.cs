using Desktop.Domain.Templates;
using Desktop.Application.Inputs;
using Desktop.Application.SizeTables;
using Desktop.Application.Templates;
using System.Collections.Concurrent;
using Desktop.Application.Rendering;

namespace Desktop.Application.Ai;

/// <summary>
/// Coordinates the already implemented INPUT-01 and AI-S3 producers into the
/// B-03 projection.  Results are always read from the durable result store;
/// an absent result is never interpreted as a successful analysis.
/// </summary>
public sealed class AiBatchAnalysisWorkflow : IAiBatchAnalysisWorkflow, ISizeTableRepairAdmission, IDisposable
{
    private readonly IBatchRepository _batches;
    private readonly ITemplateRepository _templates;
    private readonly IAiAnalysisStore _store;
    private readonly AiBatchProjectionService _projections;
    private readonly IAiAnalysisSchedulingStateStore _schedulingState;
    private readonly IAiAnalysisSchedulerFactory _schedulerFactory;
    private readonly IConsentPort _consent;
    private readonly AiRenderPreparationService? _renderPreparation;
    private readonly IAiAnalysisCheckpointPort? _checkpointPort;
    private readonly ConcurrentDictionary<string, AiBatchCommandService> _contexts = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _contextGate = new(1, 1);
    private readonly CancellationTokenSource _backgroundCancellation = new();
    private int _disposed;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _backgroundCancellation.Cancel();
        foreach (var context in _contexts.Values)
        {
            context.StopAdmission();
        }

        foreach (var context in _contexts.Values)
        {
            context.WaitForBackgroundCompletion();
        }

        foreach (var context in _contexts.Values)
        {
            context.Dispose();
        }
        _backgroundCancellation.Dispose();
        _contextGate.Dispose();
        GC.SuppressFinalize(this);
    }

    public AiBatchAnalysisWorkflow(
        IBatchRepository batches,
        ITemplateRepository templates,
        IAiAnalysisStore store,
        IAiAnalysisResultStore results,
        IAiAnalysisSchedulingStateStore schedulingState,
        IAiAnalysisSchedulerFactory schedulerFactory,
        IConsentPort consent,
        ITemplateResourceRepository? templateResources = null,
        IAiRenderPlanFactory? renderPlans = null,
        ISizeTableRepository? sizeTables = null,
        ISizeTableRasterizer? sizeTableRasterizer = null,
        IAiAnalysisCheckpointPort? checkpointPort = null)
    {
        _batches = batches ?? throw new ArgumentNullException(nameof(batches));
        _templates = templates ?? throw new ArgumentNullException(nameof(templates));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        var resultReader = new AiAnalysisResultReader(_store, results);
        _projections = new AiBatchProjectionService(resultReader);
        _schedulingState = schedulingState ?? throw new ArgumentNullException(nameof(schedulingState));
        _schedulerFactory = schedulerFactory ?? throw new ArgumentNullException(nameof(schedulerFactory));
        _consent = consent ?? throw new ArgumentNullException(nameof(consent));
        _renderPreparation = templateResources is not null && renderPlans is not null
            ? new AiRenderPreparationService(resultReader, templateResources, renderPlans, sizeTables, sizeTableRasterizer)
            : null;
        _checkpointPort = checkpointPort;
    }

    public Task<AiUploadConsentRecord?> GetUploadConsentAsync(CancellationToken cancellationToken) =>
        _consent.GetConsentAsync(AiConsentScopes.AnalysisCopy, cancellationToken);

    public Task<AiUploadConsentRecord> GrantUploadConsentAsync(CancellationToken cancellationToken) =>
        _consent.GrantAsync(AiConsentScopes.AnalysisCopy, AiConsentPolicy.Version, cancellationToken);

    public Task<AiUploadConsentRecord> RevokeUploadConsentAsync(CancellationToken cancellationToken) =>
        _consent.RevokeAsync(AiConsentScopes.AnalysisCopy, cancellationToken);

    public Task<AiBatchAnalysisProjection> StartAsync(string batchId, CancellationToken cancellationToken) =>
        ExecuteAsync(batchId, (commands, token) => commands.StartAsync(token), cancellationToken);

    public async Task<AiBatchAnalysisProjection?> RefreshAsync(string batchId, CancellationToken cancellationToken)
    {
        var context = await LoadContextAsync(batchId, cancellationToken, throwIfMissing: false).ConfigureAwait(false);
        if (context is null)
        {
            return null;
        }

        await _contextGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await context.RefreshAsync(cancellationToken).ConfigureAwait(false);
            return await BuildProjectionAsync(context, cancellationToken).ConfigureAwait(false);
        }
        finally { _contextGate.Release(); }
    }

    public Task<AiBatchAnalysisProjection> PauseAsync(string batchId, CancellationToken cancellationToken) =>
        ExecuteAsync(batchId, (commands, token) => commands.PauseAsync(token), cancellationToken);

    public Task<AiBatchAnalysisProjection> ResumeAsync(string batchId, CancellationToken cancellationToken) =>
        ExecuteAsync(batchId, (commands, token) => commands.ResumeAsync(token), cancellationToken);

    public Task<AiBatchAnalysisProjection> CancelAsync(string batchId, CancellationToken cancellationToken) =>
        ExecuteAsync(batchId, (commands, token) => commands.CancelAsync(token), cancellationToken);

    public Task<AiBatchAnalysisProjection> WaitForNetworkAsync(string batchId, CancellationToken cancellationToken) =>
        ExecuteAsync(batchId, (commands, token) => commands.WaitForNetworkAsync(token), cancellationToken);

    public Task<AiBatchAnalysisProjection> LockSelectionAsync(
        string batchId, string styleId, string requirementId, string assetId, CancellationToken cancellationToken) =>
        ExecuteAsync(batchId, (commands, token) =>
            commands.LockSelectionAsync(styleId, requirementId, assetId, token), cancellationToken);

    public Task<AiBatchAnalysisProjection> SelectSizeTableSourceAsync(
        string batchId, string styleId, string assetId, CancellationToken cancellationToken) =>
        ExecuteAsync(batchId, (commands, token) =>
            commands.SelectSizeTableSourceAsync(styleId, assetId, token), cancellationToken);

    public Task<AiBatchAnalysisProjection> AnalyzeSelectedAssetAsync(
        string batchId, string styleId, string assetId, bool includeSizeTableSource, CancellationToken cancellationToken) =>
        ExecuteAsync(batchId, (commands, token) =>
            commands.AnalyzeSelectedAssetAsync(styleId, assetId, includeSizeTableSource, token), cancellationToken);

    public async Task<AiRenderPlanPreparation> PrepareRenderingAsync(
        string batchId,
        CancellationToken cancellationToken)
    {
        if (_renderPreparation is null)
        {
            throw new InvalidOperationException("AI_RENDER_FACTORY_NOT_CONFIGURED");
        }

        var context = await LoadContextAsync(batchId, cancellationToken).ConfigureAwait(false);
        await _contextGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await context.RefreshForRenderingAsync(cancellationToken).ConfigureAwait(false);
            var snapshot = context.CaptureSnapshot();
            var projection = await _projections.BuildAsync(snapshot, cancellationToken, context.CaptureTerminalErrors).ConfigureAwait(false);
            return await _renderPreparation.PrepareAsync(
                new AiRenderPreparationSnapshot(
                    snapshot.Batch,
                    snapshot.TemplateRevision,
                    context.CaptureRequests(),
                    projection),
                cancellationToken).ConfigureAwait(false);
        }
        finally { _contextGate.Release(); }
    }

    private async Task<AiBatchAnalysisProjection> ExecuteAsync(
        string batchId,
        Func<AiBatchCommandService, CancellationToken, Task> command,
        CancellationToken cancellationToken)
    {
        var context = await LoadContextAsync(batchId, cancellationToken).ConfigureAwait(false);
        await _contextGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await command(context, cancellationToken).ConfigureAwait(false);
            return await BuildProjectionAsync(context, cancellationToken).ConfigureAwait(false);
        }
        finally { _contextGate.Release(); }
    }

    public async Task<IDisposable> AcquireAsync(string batchId, CancellationToken cancellationToken)
    {
        var context = await LoadContextAsync(batchId, cancellationToken).ConfigureAwait(false);
        if (!await _contextGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            throw new InvalidOperationException("SIZE_REPAIR_AI_NOT_SETTLED");
        try
        {
            await context.RequireRepairIdleAsync(cancellationToken).ConfigureAwait(false);
            return new RepairAdmissionLease(_contextGate);
        }
        catch { _contextGate.Release(); throw; }
    }

    private sealed class RepairAdmissionLease(SemaphoreSlim gate) : IDisposable
    {
        private SemaphoreSlim? _gate = gate;
        public void Dispose() => Interlocked.Exchange(ref _gate, null)?.Release();
    }

    private async Task<AiBatchCommandService> LoadContextAsync(
        string batchId,
        CancellationToken cancellationToken,
        bool throwIfMissing = true)
    {
        if (string.IsNullOrWhiteSpace(batchId))
        {
            throw new ArgumentException("批次标识不能为空。", nameof(batchId));
        }

        if (_contexts.TryGetValue(batchId, out var existing))
        {
            return existing;
        }

        await _contextGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_contexts.TryGetValue(batchId, out existing))
            {
                return existing;
            }

            var batch = await _batches.GetAsync(batchId, cancellationToken).ConfigureAwait(false);
            if (batch is null)
            {
                if (!throwIfMissing)
                {
                    return null!;
                }

                throw new InvalidOperationException("AI_BATCH_NOT_FOUND");
            }

            var revision = await _templates.GetRevisionAsync(
                batch.TemplateSnapshot.RevisionId,
                cancellationToken).ConfigureAwait(false);
            if (revision is null || revision.State != TemplateRevisionState.Committed)
            {
                throw new InvalidOperationException("AI_TEMPLATE_REVISION_NOT_FOUND");
            }

            var group = await _templates.GetGroupAsync(revision.GroupId, cancellationToken).ConfigureAwait(false);
            var context = new AiBatchCommandService(
                batch,
                revision,
                group?.Name ?? "当前已校准模板",
                _schedulerFactory.Create(batch.BatchId),
                _store,
                _schedulingState,
                _consent,
                _backgroundCancellation.Token,
                _checkpointPort);
            _contexts[batchId] = context;
            await context.RefreshRequestsAsync(cancellationToken).ConfigureAwait(false);
            return context;
        }
        finally
        {
            _contextGate.Release();
        }
    }

    private Task<AiBatchAnalysisProjection> BuildProjectionAsync(
        AiBatchCommandService context,
        CancellationToken cancellationToken) =>
        _projections.BuildAsync(context.CaptureSnapshot(), cancellationToken, context.CaptureTerminalErrors);
}
