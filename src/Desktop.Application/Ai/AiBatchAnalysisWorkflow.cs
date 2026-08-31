using Desktop.Contracts.Ai;
using Desktop.Domain.Ai;
using Desktop.Domain.Inputs;
using Desktop.Domain.Templates;
using Desktop.Application.Inputs;
using Desktop.Application.Templates;
using System.Collections.Concurrent;
using System.Text.Json;

namespace Desktop.Application.Ai;

/// <summary>
/// Application-facing orchestration for the B-02/B-03 vertical slice.  The
/// presentation layer consumes these projections and commands; it never
/// carries scheduler state, provider DTOs or product rules.
/// </summary>
public interface IAiBatchAnalysisWorkflow
{
    Task<AiBatchAnalysisProjection> StartAsync(
        string batchId,
        CancellationToken cancellationToken);

    Task<AiBatchAnalysisProjection?> RefreshAsync(
        string batchId,
        CancellationToken cancellationToken);

    Task<AiBatchAnalysisProjection> PauseAsync(
        string batchId,
        CancellationToken cancellationToken);

    Task<AiBatchAnalysisProjection> ResumeAsync(
        string batchId,
        CancellationToken cancellationToken);

    Task<AiBatchAnalysisProjection> CancelAsync(
        string batchId,
        CancellationToken cancellationToken);

    Task<AiBatchAnalysisProjection> WaitForNetworkAsync(
        string batchId,
        CancellationToken cancellationToken);

    Task<AiBatchAnalysisProjection> LockSelectionAsync(
        string batchId,
        string styleId,
        string requirementId,
        string assetId,
        CancellationToken cancellationToken);

    Task<AiBatchAnalysisProjection> AnalyzeSelectedAssetAsync(
        string batchId,
        string styleId,
        string assetId,
        bool includeSizeTableSource,
        CancellationToken cancellationToken);

    Task<AiRenderPlanPreparation> PrepareRenderingAsync(
        string batchId,
        CancellationToken cancellationToken);

    Task<AiUploadConsentRecord?> GetUploadConsentAsync(
        CancellationToken cancellationToken);

    Task<AiUploadConsentRecord> GrantUploadConsentAsync(
        CancellationToken cancellationToken);

    Task<AiUploadConsentRecord> RevokeUploadConsentAsync(
        CancellationToken cancellationToken);
}

public enum AiBatchUiStatus
{
    Waiting = 0,
    Analysing,
    ManualConfiguration,
    Ready,
    ReviewRequired,
    BudgetStopped,
    NotGeneratable,
    Cancelled,
    RecoveryRequired,
    Failed
}

public enum AiStyleUiStatus
{
    Waiting = 0,
    Analysing,
    Ready,
    ReviewRequired,
    BusinessError,
    Cancelled,
    SystemError
}

public enum AiPageUiStatus
{
    Waiting = 0,
    Analysing,
    Ready,
    ReviewRequired,
    BusinessError,
    Cancelled,
    SystemError
}

public enum AiRequirementSelectionUiStatus
{
    Waiting = 0,
    Analysing,
    AiSelected,
    ManualLocked,
    Missing,
    Unavailable,
    Cancelled
}

/// <summary>
/// Provider-neutral projection of the photo currently used by one template
/// frame.  Asset identifiers and preview paths are command/data bindings only;
/// presentation never has to expose source file names or local paths as text.
/// </summary>
public sealed record AiRequirementSelectionProjection(
    string BatchId,
    string StyleId,
    string PageId,
    string PageName,
    string RequirementId,
    string FrameDisplayName,
    string SelectionRequirement,
    AiRequirementSelectionUiStatus Status,
    string StatusText,
    string? SelectedAssetId,
    string? SelectedPreviewPath)
{
    public string StatusState => Status.ToString();

    public bool HasSelection => !string.IsNullOrWhiteSpace(SelectedAssetId)
        && !string.IsNullOrWhiteSpace(SelectedPreviewPath);

    public bool IsManualLocked => Status == AiRequirementSelectionUiStatus.ManualLocked;
}

/// <summary>
/// One source photo already registered by INPUT-01 for a style.  This is the
/// complete local replacement library, not a provider candidate DTO.
/// </summary>
public sealed record AiStyleAssetProjection(
    string BatchId,
    string StyleId,
    string AssetId,
    string? PreviewPath,
    int DisplayOrder);

public sealed record AiCandidateProjection(
    string BatchId,
    string StyleId,
    string RequirementId,
    string AssetId,
    string AssetName,
    int Rank,
    decimal Confidence,
    IReadOnlyList<string> EvidenceCodes,
    bool IsLocked,
    string FrameDisplayName = "图片框",
    string? PreviewPath = null,
    string SelectionRequirement = "",
    string PageName = "")
{
    public string RankText => $"Top-{Rank}";

    public string ConfidenceText => $"匹配度 {Confidence:P0}";

    public string EvidenceText => EvidenceCodes.Count == 0
        ? "暂无补充证据"
        : string.Join(" · ", EvidenceCodes.Select(ToEvidenceText).Distinct(StringComparer.Ordinal));

    public string LockText => IsLocked ? "已选用" : "选用这张";

    public bool CanLock => !IsLocked;

    private static string ToEvidenceText(string code) => code switch
    {
        "front_view" => "正面",
        "back_view" => "背面",
        "detail_view" => "细节",
        "clear_subject" => "主体清晰",
        "variant_match" => "变体一致",
        "SUBJECT_COMPLETE" => "主体完整",
        "NO_EXTRA_DISTRACTIONS" => "无多余干扰",
        "SHOWS_SHOULDER_STRAP" => "肩带完整",
        "SHOWS_RING_DETAIL" => "圆环细节清晰",
        "PURE_WHITE_BACKGROUND" => "纯白背景",
        "SUBJECT_CLEAR" => "主体清晰",
        "VARIANT_MATCH" => "款式一致",
        "FRONT_VIEW" => "正面视角",
        "BACK_VIEW" => "背面视角",
        "DETAIL_VIEW" => "细节视角",
        "color_mismatch_black_not_gray" => "颜色与模板要求不符",
        "missing_shoulder_strap" => "缺少模板要求的肩带",
        "subject_partial_no_other_distractors" => "主体可见，但仍需核对完整性",
        _ => "需要人工核对"
    };
}

public sealed record AiPageAnalysisProjection(
    string BatchId,
    string StyleId,
    string PageId,
    string PageName,
    AiPageUiStatus Status,
    string StatusText,
    string ActionText,
    IReadOnlyList<AiCandidateProjection> Candidates,
    IReadOnlyList<string> WarningTexts,
    bool RequiresManualConfiguration = false,
    IReadOnlyList<AiRequirementSelectionProjection>? Requirements = null)
{
    public string StatusState => Status.ToString();

    public bool HasWarnings => WarningTexts.Count > 0;

    public bool CanGenerate => Status == AiPageUiStatus.Ready;
}

public sealed record AiStyleAnalysisProjection(
    string BatchId,
    string StyleId,
    string StyleName,
    AiStyleUiStatus Status,
    string StatusText,
    IReadOnlyList<AiPageAnalysisProjection> Pages,
    int AssetCount,
    IReadOnlyList<AiStyleAssetProjection>? Assets = null)
{
    public string StatusState => Status.ToString();

    public bool HasReadyPage => Pages.Any(page => page.Status == AiPageUiStatus.Ready);

    public bool HasBusinessError => Pages.Any(page => page.Status == AiPageUiStatus.BusinessError);
}

public sealed record AiBatchAnalysisProjection(
    string BatchId,
    AiBatchUiStatus Status,
    string StatusText,
    string ActionText,
    int StyleCount,
    int CompletedStyleCount,
    int ReadyPageCount,
    int TotalPageCount,
    IReadOnlyList<AiStyleAnalysisProjection> Styles,
    AiAnalysisSchedulingControlState ControlState,
    string? ControlReason,
    bool IsBusy = false,
    string TemplateSummary = "当前已校准模板",
    string TemplateMeta = "模板快照已冻结",
    string BatchSummary = "当前输入批次",
    string BatchMeta = "素材清单已登记",
    string OutputSummary = "仅 JPG",
    string OutputMeta = "正式输出目录已确认",
    string PageSummary = "0 个页面",
    string PageMeta = "等待分析")
{
    public string StatusState => Status.ToString();

    public string ProgressText => $"已完成 {CompletedStyleCount}/{StyleCount} 款 · 可生成页面 {ReadyPageCount}/{TotalPageCount}";

    public int ExecutablePageCount => Styles.SelectMany(style => style.Pages)
        .Count(page => page.Status == AiPageUiStatus.Ready
            || page.Status == AiPageUiStatus.ReviewRequired
                && !page.RequiresManualConfiguration);

    public int IsolatedPageCount => Styles.SelectMany(style => style.Pages)
        .Count(page => page.Status == AiPageUiStatus.BusinessError);

    public bool CanEnterGeneration => (Status is AiBatchUiStatus.Ready or AiBatchUiStatus.ReviewRequired)
        && ExecutablePageCount > 0
        && Styles.SelectMany(style => style.Pages).All(page => page.Status is
            AiPageUiStatus.Ready or AiPageUiStatus.ReviewRequired
                or AiPageUiStatus.BusinessError or AiPageUiStatus.Cancelled);

    public bool CanPause => IsBusy is false
        && Status is not AiBatchUiStatus.BudgetStopped
        && !IsTerminal
        && ControlState is AiAnalysisSchedulingControlState.Running;

    public bool CanResume => IsBusy is false
        && (ControlState is AiAnalysisSchedulingControlState.Paused
            or AiAnalysisSchedulingControlState.WaitingForNetwork);

    public bool CanCancel => IsBusy is false
        && !IsTerminal
        && ControlState is not AiAnalysisSchedulingControlState.Cancelled;

    public bool IsTerminal => Status is AiBatchUiStatus.Ready
        or AiBatchUiStatus.NotGeneratable
        or AiBatchUiStatus.Cancelled
        or AiBatchUiStatus.RecoveryRequired
        or AiBatchUiStatus.Failed;
}

/// <summary>
/// Coordinates the already implemented INPUT-01 and AI-S3 producers into the
/// B-03 projection.  Results are always read from the durable result store;
/// an absent result is never interpreted as a successful analysis.
/// </summary>
public sealed class AiBatchAnalysisWorkflow : IAiBatchAnalysisWorkflow, IDisposable
{
    private const string ModelConfigVersion = "doubao-seed-2.1-pro-260628";
    private const string PromptContractVersion = "ai-prompt/v1";
    private const string CompressionProfileVersion = AiAnalysisCopyProfiles.WicBoundedV1;

    private readonly IBatchRepository _batches;
    private readonly ITemplateRepository _templates;
    private readonly IAiAnalysisStore _store;
    private readonly IAiAnalysisResultStore _results;
    private readonly IAiAnalysisSchedulingStateStore _schedulingState;
    private readonly IAiAnalysisSchedulerFactory _schedulerFactory;
    private readonly IConsentPort _consent;
    private readonly ITemplateResourceRepository? _templateResources;
    private readonly IAiRenderPlanFactory? _renderPlans;
    private readonly ConcurrentDictionary<string, BatchContext> _contexts = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _contextGate = new(1, 1);
    private readonly CancellationTokenSource _backgroundCancellation = new();

    public void Dispose()
    {
        _backgroundCancellation.Cancel();
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
        IAiRenderPlanFactory? renderPlans = null)
    {
        _batches = batches ?? throw new ArgumentNullException(nameof(batches));
        _templates = templates ?? throw new ArgumentNullException(nameof(templates));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _results = results ?? throw new ArgumentNullException(nameof(results));
        _schedulingState = schedulingState ?? throw new ArgumentNullException(nameof(schedulingState));
        _schedulerFactory = schedulerFactory ?? throw new ArgumentNullException(nameof(schedulerFactory));
        _consent = consent ?? throw new ArgumentNullException(nameof(consent));
        _templateResources = templateResources;
        _renderPlans = renderPlans;
    }

    public Task<AiUploadConsentRecord?> GetUploadConsentAsync(CancellationToken cancellationToken) =>
        _consent.GetConsentAsync(AiConsentScopes.AnalysisCopy, cancellationToken);

    public Task<AiUploadConsentRecord> GrantUploadConsentAsync(CancellationToken cancellationToken) =>
        _consent.GrantAsync(AiConsentScopes.AnalysisCopy, AiConsentPolicy.Version, cancellationToken);

    public Task<AiUploadConsentRecord> RevokeUploadConsentAsync(CancellationToken cancellationToken) =>
        _consent.RevokeAsync(AiConsentScopes.AnalysisCopy, cancellationToken);

    public async Task<AiBatchAnalysisProjection> StartAsync(
        string batchId,
        CancellationToken cancellationToken)
    {
        var context = await LoadContextAsync(batchId, cancellationToken).ConfigureAwait(false);
        await EnsureSchedulerRestoredAsync(context, cancellationToken).ConfigureAwait(false);
        if (context.Batch.ScanStatus != BatchScanStatus.ScanReady)
        {
            throw new InvalidOperationException("AI_INPUT_BATCH_NOT_READY");
        }

        var consent = await RequireUploadConsentAsync(cancellationToken).ConfigureAwait(false);

        var projectionOptions = ProjectionOptions(context.Batch.BatchId, consent.ConsentId);
        _ = await context.Scheduler.EnqueueBatchAsync(
            context.Batch,
            context.TemplateRevision,
            projectionOptions,
            cancellationToken).ConfigureAwait(false);
        context.Started = true;
        await RefreshRequestsAsync(context, cancellationToken).ConfigureAwait(false);
        var projection = await BuildProjectionAsync(context, cancellationToken).ConfigureAwait(false);
        StartBackgroundProcessing(context);
        return projection;
    }

    public async Task<AiBatchAnalysisProjection?> RefreshAsync(
        string batchId,
        CancellationToken cancellationToken)
    {
        var context = await LoadContextAsync(batchId, cancellationToken, throwIfMissing: false).ConfigureAwait(false);
        if (context is null)
        {
            return null;
        }

        await EnsureSchedulerRestoredAsync(context, cancellationToken).ConfigureAwait(false);
        await RefreshRequestsAsync(context, cancellationToken).ConfigureAwait(false);
        return await BuildProjectionAsync(context, cancellationToken).ConfigureAwait(false);
    }

    public Task<AiBatchAnalysisProjection> PauseAsync(string batchId, CancellationToken cancellationToken) =>
        ControlAsync(batchId, (scheduler, token) => scheduler.RequestPauseAsync("用户已暂停分析；可恢复继续。", token), cancellationToken);

    public Task<AiBatchAnalysisProjection> ResumeAsync(string batchId, CancellationToken cancellationToken) =>
        ControlAsync(batchId, (scheduler, token) => scheduler.ResumeAsync(token), cancellationToken);

    public Task<AiBatchAnalysisProjection> CancelAsync(string batchId, CancellationToken cancellationToken) =>
        ControlAsync(batchId, (scheduler, token) => scheduler.CancelAsync(batchId, token), cancellationToken);

    public Task<AiBatchAnalysisProjection> WaitForNetworkAsync(string batchId, CancellationToken cancellationToken) =>
        ControlAsync(batchId, (scheduler, token) => scheduler.WaitForNetworkAsync("网络暂不可用；恢复网络后可继续。", token), cancellationToken);

    public async Task<AiBatchAnalysisProjection> LockSelectionAsync(
        string batchId,
        string styleId,
        string requirementId,
        string assetId,
        CancellationToken cancellationToken)
    {
        var context = await LoadContextAsync(batchId, cancellationToken).ConfigureAwait(false);
        await EnsureSchedulerRestoredAsync(context, cancellationToken).ConfigureAwait(false);
        var request = GetFirstLayerRequest(context, styleId);
        _ = await context.Scheduler.LockSelectionAsync(
            request,
            requirementId,
            assetId,
            cancellationToken).ConfigureAwait(false);
        return await BuildProjectionAsync(context, cancellationToken).ConfigureAwait(false);
    }

    public async Task<AiBatchAnalysisProjection> AnalyzeSelectedAssetAsync(
        string batchId,
        string styleId,
        string assetId,
        bool includeSizeTableSource,
        CancellationToken cancellationToken)
    {
        var context = await LoadContextAsync(batchId, cancellationToken).ConfigureAwait(false);
        await EnsureSchedulerRestoredAsync(context, cancellationToken).ConfigureAwait(false);
        var request = GetFirstLayerRequest(context, styleId);
        var session = await _store.LoadSessionAsync(SessionId(request), cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("AI_SESSION_NOT_FOUND");
        var hasLockedAsset = session.LockedSelections.Values.Any(selection =>
            string.Equals(selection.AssetId, assetId, StringComparison.Ordinal));
        if ((context.Batch.SelectionMode == BatchSelectionMode.Manual && !hasLockedAsset)
            || (session.LockedSelections.Count > 0 && !hasLockedAsset))
        {
            throw new InvalidOperationException("AI_REFINEMENT_SELECTION_NOT_LOCKED");
        }
        _ = await context.Scheduler.EnqueueRefinementAsync(
            request,
            [assetId],
            includeSizeTableSource,
            cancellationToken).ConfigureAwait(false);
        await RefreshRequestsAsync(context, cancellationToken).ConfigureAwait(false);
        StartBackgroundProcessing(context);
        return await BuildProjectionAsync(context, cancellationToken).ConfigureAwait(false);
    }

    public async Task<AiRenderPlanPreparation> PrepareRenderingAsync(
        string batchId,
        CancellationToken cancellationToken)
    {
        if (_templateResources is null || _renderPlans is null)
        {
            throw new InvalidOperationException("AI_RENDER_FACTORY_NOT_CONFIGURED");
        }

        var context = await LoadContextAsync(batchId, cancellationToken).ConfigureAwait(false);
        await EnsureSchedulerRestoredAsync(context, cancellationToken).ConfigureAwait(false);
        await RefreshRequestsAsync(context, cancellationToken).ConfigureAwait(false);
        var projection = await BuildProjectionAsync(context, cancellationToken).ConfigureAwait(false);
        if (projection.Status is AiBatchUiStatus.RecoveryRequired
            or AiBatchUiStatus.Failed
            or AiBatchUiStatus.Cancelled
            or AiBatchUiStatus.BudgetStopped
            or AiBatchUiStatus.Analysing
            or AiBatchUiStatus.Waiting)
        {
            throw new InvalidOperationException("AI_RENDER_ANALYSIS_NOT_CLOSED");
        }

        var resources = new List<ImportedTemplateResource>();
        foreach (var resourceId in context.Batch.TemplateSnapshot.Pages
                     .Select(page => page.TemplateResourceId)
                     .Distinct(StringComparer.Ordinal))
        {
            var resource = await _templateResources.GetByResourceIdAsync(
                resourceId,
                cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("AI_RENDER_TEMPLATE_RESOURCE_NOT_FOUND");
            resources.Add(resource);
        }

        var styleInputs = new List<AiRenderStyleInput>(context.Batch.Styles.Length);
        var planCreatedAt = context.Batch.UpdatedAt;
        foreach (var style in context.Batch.Styles.OrderBy(style => style.Order))
        {
            var firstLayer = context.Requests.Values.FirstOrDefault(request =>
                string.Equals(request.StyleId, style.StyleId, StringComparison.Ordinal)
                && string.Equals(
                    request.AnalysisKind,
                    AiAnalysisKind.ProductJointUnderstanding,
                    StringComparison.Ordinal));
            var session = firstLayer is null
                ? null
                : await _store.LoadSessionAsync(SessionId(firstLayer), cancellationToken).ConfigureAwait(false);
            var results = new List<AiAnalysisResultV1>();
            foreach (var request in context.Requests.Values.Where(request =>
                         string.Equals(request.StyleId, style.StyleId, StringComparison.Ordinal)))
            {
                var ledger = await _store.FindRequestByFingerprintAsync(
                    AiAnalysisCacheKeyFactory.Create(request),
                    cancellationToken).ConfigureAwait(false);
                if (ledger?.UpdatedAt > planCreatedAt)
                {
                    planCreatedAt = ledger.UpdatedAt;
                }
                if (ledger?.ResultReference is null
                    || ledger.Status is not ("Validated" or "ManualReview" or "PlanClosed"))
                {
                    continue;
                }

                var result = await _results.LoadAsync(
                    ledger.ResultReference,
                    cancellationToken).ConfigureAwait(false);
                if (result is not null)
                {
                    results.Add(AiAnalysisResultReferenceNormalizer.Normalize(request, result));
                }
            }

            var geometryByAsset = results.SelectMany(result => result.SubjectGeometry)
                .GroupBy(geometry => geometry.AssetId, StringComparer.Ordinal)
                .ToDictionary(
                    group => group.Key,
                    group => group.OrderByDescending(geometry => geometry.Confidence).First(),
                    StringComparer.Ordinal);
            var selectedRequirements = projection.Styles
                .Single(item => string.Equals(item.StyleId, style.StyleId, StringComparison.Ordinal))
                .Pages
                .Where(page => page.Status == AiPageUiStatus.Ready
                    || page.Status == AiPageUiStatus.ReviewRequired
                        && !page.RequiresManualConfiguration)
                .SelectMany(page => page.Requirements ?? [])
                .Where(requirement => requirement.SelectedAssetId is not null)
                .GroupBy(requirement => requirement.RequirementId, StringComparer.Ordinal)
                .Select(group => group.First())
                .Select(requirement =>
                {
                    var assetId = requirement.SelectedAssetId!;
                    var revision = session?.LockedSelections.TryGetValue(
                        requirement.RequirementId,
                        out var locked) == true
                        ? locked.SelectionRevision
                        : 0;
                    return new AiConfirmedRenderSelection(
                        requirement.RequirementId,
                        assetId,
                        geometryByAsset.GetValueOrDefault(assetId),
                        revision);
                })
                .ToArray();
            var sizeCandidates = results.Where(result => result.SizeTableCandidate is not null)
                .Select(result => result.SizeTableCandidate!)
                .GroupBy(candidate => JsonSerializer.Serialize(new
                {
                    candidate.SourceAssetId,
                    candidate.Columns,
                    candidate.Rows
                }), StringComparer.Ordinal)
                .Select(group => group.OrderByDescending(candidate => candidate.Confidence).First())
                .ToArray();
            styleInputs.Add(new AiRenderStyleInput(
                style.StyleId,
                selectedRequirements,
                sizeCandidates.Length == 1 ? sizeCandidates[0] : null));
        }

        return await _renderPlans.PrepareAsync(
            new AiRenderPlanPreparationRequest(
                context.Batch,
                context.TemplateRevision,
                resources,
                styleInputs,
                planCreatedAt),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<AiBatchAnalysisProjection> ControlAsync(
        string batchId,
        Func<AiAnalysisScheduler, CancellationToken, Task> command,
        CancellationToken cancellationToken)
    {
        var context = await LoadContextAsync(batchId, cancellationToken).ConfigureAwait(false);
        await EnsureSchedulerRestoredAsync(context, cancellationToken).ConfigureAwait(false);
        await command(context.Scheduler, cancellationToken).ConfigureAwait(false);
        await RefreshRequestsAsync(context, cancellationToken).ConfigureAwait(false);
        if (context.Scheduler.ControlState == AiAnalysisSchedulingControlState.Running)
        {
            StartBackgroundProcessing(context);
        }
        return await BuildProjectionAsync(context, cancellationToken).ConfigureAwait(false);
    }

    private static async Task ProcessBatchAsync(
        BatchContext context,
        CancellationToken cancellationToken)
    {
        while (context.Scheduler.ReadyCount > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (context.Scheduler.ControlState is AiAnalysisSchedulingControlState.Paused
                or AiAnalysisSchedulingControlState.WaitingForNetwork
                or AiAnalysisSchedulingControlState.CancelRequested
                or AiAnalysisSchedulingControlState.Cancelled)
            {
                break;
            }

            var workerCount = Math.Min(
                AiAnalysisProductionPolicy.QueueV1.MaxConcurrentRequests,
                context.Scheduler.ReadyCount);
            var results = await Task.WhenAll(Enumerable.Range(0, workerCount)
                .Select(_ => context.Scheduler.ProcessNextAsync(cancellationToken))).ConfigureAwait(false);
            if (results.Any(result => result.Disposition is AiAnalysisWorkDisposition.BudgetStopped
                    or AiAnalysisWorkDisposition.Paused
                    or AiAnalysisWorkDisposition.WaitingForNetwork))
            {
                break;
            }
            if (context.Scheduler.ControlState is AiAnalysisSchedulingControlState.Paused
                or AiAnalysisSchedulingControlState.WaitingForNetwork
                or AiAnalysisSchedulingControlState.Cancelled)
            {
                break;
            }
        }
    }

    private void StartBackgroundProcessing(BatchContext context)
    {
        lock (context.BackgroundGate)
        {
            if (context.BackgroundTask is { IsCompleted: false })
            {
                return;
            }

            context.BackgroundTask = Task.Run(
                () => ProcessBatchInBackgroundAsync(context, _backgroundCancellation.Token),
                _backgroundCancellation.Token);
        }
    }

    private static async Task ProcessBatchInBackgroundAsync(BatchContext context, CancellationToken cancellationToken)
    {
        try
        {
            await ProcessBatchAsync(context, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Application shutdown cancels background work; durable scheduler
            // state remains the source of truth for the next restore.
        }
        catch (InvalidOperationException)
        {
            // The scheduler has already persisted a terminal/recovery state.
            // The next explicit refresh projects the actionable Chinese state.
        }
    }

    private async Task<BatchContext> LoadContextAsync(
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
            var context = new BatchContext(
                batch,
                revision,
                group?.Name ?? "当前已校准模板",
                _schedulerFactory.Create(batch.BatchId));
            _contexts[batchId] = context;
            await RefreshRequestsAsync(context, cancellationToken).ConfigureAwait(false);
            return context;
        }
        finally
        {
            _contextGate.Release();
        }
    }

    private async Task RefreshRequestsAsync(BatchContext context, CancellationToken cancellationToken)
    {
        var state = await _schedulingState.LoadSchedulingStateAsync(
            context.Scheduler.StateKey,
            cancellationToken).ConfigureAwait(false);
        if (state?.HistoricalRequests is { Count: > 0 } requests)
        {
            context.Started |= requests.Any(request =>
                string.Equals(request.BatchId, context.Batch.BatchId, StringComparison.Ordinal));
            foreach (var request in requests.Where(request =>
                         string.Equals(request.BatchId, context.Batch.BatchId, StringComparison.Ordinal)))
            {
                context.Requests[request.RequestId] = request;
            }
        }

        foreach (var style in context.Batch.Styles)
        {
            if (context.Requests.ContainsKey($"ai-{context.Batch.BatchId}-{style.StyleId}"))
            {
                continue;
            }

            try
            {
                var projected = AiAnalysisInputProjector.Project(
                    context.Batch,
                    context.TemplateRevision,
                    style.StyleId,
                    ProjectionOptions(context.Batch.BatchId));
                context.Requests[projected.RequestId] = projected;
            }
            catch (InvalidOperationException)
            {
                // BuildProjection will expose an actionable terminal state.
            }
        }
    }

    private async Task<AiBatchAnalysisProjection> BuildProjectionAsync(
        BatchContext context,
        CancellationToken cancellationToken)
    {
        var styles = new List<AiStyleAnalysisProjection>(context.Batch.Styles.Length);
        foreach (var style in context.Batch.Styles.OrderBy(style => style.Order))
        {
            var request = context.Requests.Values.FirstOrDefault(candidate =>
                candidate.StyleId == style.StyleId
                && candidate.AnalysisKind == AiAnalysisKind.ProductJointUnderstanding);
            styles.Add(await BuildStyleProjectionAsync(context, style, request, cancellationToken).ConfigureAwait(false));
        }

        var pages = styles.SelectMany(style => style.Pages).ToArray();
        var completedStyles = styles.Count(style => style.Status is AiStyleUiStatus.Ready
            or AiStyleUiStatus.ReviewRequired
            or AiStyleUiStatus.BusinessError
            or AiStyleUiStatus.SystemError
            or AiStyleUiStatus.Cancelled);
        var readyPages = pages.Count(page => page.Status == AiPageUiStatus.Ready);
        var controlState = context.Scheduler.ControlState;
        var status = context.SchedulerRestoreError is not null
            ? AiBatchUiStatus.RecoveryRequired
            : DetermineBatchStatus(
                styles,
                controlState,
                context.Batch.SelectionMode == BatchSelectionMode.Manual,
                context.Scheduler.Budget.HardStopped);
        var message = controlState switch
        {
            AiAnalysisSchedulingControlState.Paused when context.Scheduler.ControlReason is
                "AI_UPLOAD_CONSENT_REQUIRED" or "AI_UPLOAD_CONSENT_REVOKED"
                => "上传授权未完成或已撤回；请在设置中重新授权后恢复分析。",
            AiAnalysisSchedulingControlState.Paused => "分析已暂停；点击恢复后从安全检查点继续。",
            AiAnalysisSchedulingControlState.WaitingForNetwork => context.Scheduler.ControlReason
                ?? "等待网络恢复；不会自动重发未知结果。",
            _ => status switch
            {
                AiBatchUiStatus.Ready => "分析完成，所有已选页面均可生成。",
                AiBatchUiStatus.ManualConfiguration => "分析完成，请逐个图片框选择并锁定素材。",
                AiBatchUiStatus.ReviewRequired => "分析完成，部分页面需要人工复核后才能生成。",
                AiBatchUiStatus.BudgetStopped => "本轮 AI 预算已达到停止线；请降低范围或等待人工批准后继续。",
                AiBatchUiStatus.NotGeneratable => "没有可生成页面，请先处理标红页面。",
                AiBatchUiStatus.Cancelled => "批次已取消；源图片和模板均未修改。",
                AiBatchUiStatus.RecoveryRequired => context.SchedulerRestoreError is null
                    ? "部分分析结果未知或需要恢复。请查询状态后再继续，不会自动重发。"
                    : "上次分析未能安全恢复，请刷新状态或重新打开批次；不会自动重发。",
                AiBatchUiStatus.Failed => "分析失败。请按页面提示修复后重试。",
                AiBatchUiStatus.Analysing => "正在分析商品素材；完成后会逐页更新状态。",
                _ => "等待分析任务开始。"
            }
        };
        return new AiBatchAnalysisProjection(
            context.Batch.BatchId,
            status,
            message,
            status is AiBatchUiStatus.Ready ? "进入生成" : "继续处理",
            styles.Count,
            completedStyles,
            readyPages,
            pages.Length,
            styles,
            controlState,
            context.Scheduler.ControlReason,
            TemplateSummary: context.TemplateGroupName,
            TemplateMeta: $"{context.Batch.TemplateSnapshot.Pages.Length} 个页面 · 已校准",
            BatchSummary: context.Batch.UpdatedAt.ToLocalTime().ToString(
                "yyyy-MM-dd HH:mm 批次",
                System.Globalization.CultureInfo.InvariantCulture),
            BatchMeta: $"{context.Batch.Styles.Length} 款 · {ToInputTypeText(context.Batch.InputType)}",
            OutputSummary: context.Batch.IncludeEditablePsd ? "JPG + 可编辑 PSD" : "仅 JPG",
            OutputMeta: "正式输出目录已确认",
            PageSummary: $"{context.Batch.TemplateSnapshot.Pages.Length} 个页面",
            PageMeta: ToPageSummary(context.Batch.TemplateSnapshot.Pages));
    }

    private async Task<AiStyleAnalysisProjection> BuildStyleProjectionAsync(
        BatchContext context,
        StyleDraft style,
        AiAnalysisRequestV1? request,
        CancellationToken cancellationToken)
    {
        var pages = new List<AiPageAnalysisProjection>();
        foreach (var page in context.TemplateRevision.Pages.Where(page =>
                     page.Enabled && context.Batch.TemplateSnapshot.Pages.Any(snapshot => snapshot.PageId == page.PageId)))
        {
            pages.Add(await BuildPageProjectionAsync(context, style, page, request, cancellationToken).ConfigureAwait(false));
        }

        // A cancelled page is terminal for that page only. It must not make a
        // sibling ready page look like it is still waiting, otherwise a single
        // user cancellation would block the same style and the whole batch.
        var activePages = pages.Where(page => page.Status != AiPageUiStatus.Cancelled).ToArray();
        var status = pages.Count == 0
            ? AiStyleUiStatus.SystemError
            : activePages.Length == 0
                ? AiStyleUiStatus.Cancelled
                : activePages.Any(page => page.Status == AiPageUiStatus.SystemError)
                    ? AiStyleUiStatus.SystemError
                    : activePages.All(page => page.Status == AiPageUiStatus.Ready)
                        ? AiStyleUiStatus.Ready
                        : activePages.Any(page => page.Status == AiPageUiStatus.BusinessError)
                            ? activePages.Any(page => page.Status is AiPageUiStatus.Ready or AiPageUiStatus.ReviewRequired)
                                ? AiStyleUiStatus.ReviewRequired
                                : AiStyleUiStatus.BusinessError
                            : activePages.Any(page => page.Status == AiPageUiStatus.ReviewRequired)
                                ? AiStyleUiStatus.ReviewRequired
                            : activePages.Any(page => page.Status == AiPageUiStatus.Analysing)
                                ? AiStyleUiStatus.Analysing
                                : AiStyleUiStatus.Waiting;
        var statusText = status switch
        {
            AiStyleUiStatus.Ready => "可生成",
            AiStyleUiStatus.ReviewRequired => "待人工复核",
            AiStyleUiStatus.BusinessError => "需要修复",
            AiStyleUiStatus.SystemError => "需要恢复",
            AiStyleUiStatus.Cancelled => "已取消",
            AiStyleUiStatus.Analysing => "分析中",
            _ => "待分析"
        };
        return new AiStyleAnalysisProjection(
            context.Batch.BatchId,
            style.StyleId,
            string.IsNullOrWhiteSpace(style.DefaultStyleCode) ? $"款式 {style.Order + 1}" : style.DefaultStyleCode,
            status,
            statusText,
            pages,
            style.AssetIds.Length,
            BuildStyleAssetProjections(context, style));
    }

    private async Task<AiPageAnalysisProjection> BuildPageProjectionAsync(
        BatchContext context,
        StyleDraft style,
        TemplatePage page,
        AiAnalysisRequestV1? request,
        CancellationToken cancellationToken)
    {
        var pageBindings = context.TemplateRevision.FrameBindings
            .Where(binding => string.Equals(binding.PageId, page.PageId, StringComparison.Ordinal))
            .OrderBy(binding => binding.FrameId, StringComparer.Ordinal)
            .ToArray();
        var pageName = page.Name;
        if (request is null)
        {
            return new(context.Batch.BatchId, style.StyleId, page.PageId, pageName,
                AiPageUiStatus.Waiting, "待分析", "等待分析", [], [],
                Requirements: BuildRequirementSelections(
                    context, style, page, pageBindings, [], [],
                    AiRequirementSelectionUiStatus.Waiting, "等待分析"));
        }

        var ledger = await _store.FindRequestByFingerprintAsync(
            AiAnalysisCacheKeyFactory.Create(request), cancellationToken).ConfigureAwait(false);
        if (ledger is null || ledger.Status is "Queued" or "RetryableError")
        {
            var requirementStatus = context.Started
                ? AiRequirementSelectionUiStatus.Analysing
                : AiRequirementSelectionUiStatus.Waiting;
            return new(context.Batch.BatchId, style.StyleId, page.PageId, pageName,
                context.Started ? AiPageUiStatus.Analysing : AiPageUiStatus.Waiting,
                context.Started ? "分析中" : "待分析",
                context.Started ? "分析中" : "等待分析", [], [],
                Requirements: BuildRequirementSelections(
                    context, style, page, pageBindings, [], [], requirementStatus,
                    context.Started ? "分析中" : "等待分析"));
        }
        if (ledger.Status is "Uploading" or "Analysing")
        {
            return new(context.Batch.BatchId, style.StyleId, page.PageId, pageName,
                AiPageUiStatus.Analysing, "分析中", "分析中", [], [],
                Requirements: BuildRequirementSelections(
                    context, style, page, pageBindings, [], [],
                    AiRequirementSelectionUiStatus.Analysing, "分析中"));
        }
        if (ledger.Status == "Cancelled")
        {
            return new(context.Batch.BatchId, style.StyleId, page.PageId, pageName,
                AiPageUiStatus.Cancelled, "已取消", "返回批次", [], [],
                Requirements: BuildRequirementSelections(
                    context, style, page, pageBindings, [], [],
                    AiRequirementSelectionUiStatus.Cancelled, "已取消"));
        }
        if (ledger.Status == "AiResultUnknown")
        {
            return new(context.Batch.BatchId, style.StyleId, page.PageId, pageName,
                AiPageUiStatus.SystemError, "结果未知，需要恢复", "查询状态", [], ["供应商处理结果未知，禁止自动重发。"],
                Requirements: BuildRequirementSelections(
                    context, style, page, pageBindings, [], [],
                    AiRequirementSelectionUiStatus.Unavailable, "结果未知，禁止重发"));
        }
        if (ledger.Status == "TerminalError")
        {
            return new(context.Batch.BatchId, style.StyleId, page.PageId, pageName,
                AiPageUiStatus.SystemError, "分析失败，需要恢复", "重试分析", [], ["分析未能安全完成，请检查网络或本地存储后重试。"],
                Requirements: BuildRequirementSelections(
                    context, style, page, pageBindings, [], [],
                    AiRequirementSelectionUiStatus.Unavailable, "分析失败"));
        }

        if (ledger.ResultReference is null)
        {
            return new(context.Batch.BatchId, style.StyleId, page.PageId, pageName,
                AiPageUiStatus.SystemError, "结果缺失，需要恢复", "查询状态", [], ["未找到已校验结果，未启动生成。"],
                Requirements: BuildRequirementSelections(
                    context, style, page, pageBindings, [], [],
                    AiRequirementSelectionUiStatus.Unavailable, "结果缺失"));
        }

        var result = await _results.LoadAsync(ledger.ResultReference, cancellationToken).ConfigureAwait(false);
        if (result is null)
        {
            return new(context.Batch.BatchId, style.StyleId, page.PageId, pageName,
                AiPageUiStatus.SystemError, "结果缺失，需要恢复", "查询状态", [], ["结果文件不可用，未启动生成。"],
                Requirements: BuildRequirementSelections(
                    context, style, page, pageBindings, [], [],
                    AiRequirementSelectionUiStatus.Unavailable, "结果缺失"));
        }

        try
        {
            // Compatibility repair for already measured v1 results that were
            // persisted before transport image aliases were closed over the
            // producing request. This is an in-memory projection only and
            // never resends or rewrites the provider result.
            result = AiAnalysisResultReferenceNormalizer.Normalize(request, result);
        }
        catch (InvalidDataException)
        {
            return new(context.Batch.BatchId, style.StyleId, page.PageId, pageName,
                AiPageUiStatus.SystemError, "结果引用异常，需要恢复", "查询状态", [], ["分析结果未能对应到当前素材，已阻止选图和生成。"],
                Requirements: BuildRequirementSelections(
                    context, style, page, pageBindings, [], [],
                    AiRequirementSelectionUiStatus.Unavailable, "结果引用异常"));
        }

        var allowedFrameIds = pageBindings.Select(binding => binding.FrameId).ToHashSet(StringComparer.Ordinal);
        var matches = result.RequirementMatches
            .Where(match => allowedFrameIds.Contains(match.RequirementId))
            .OrderBy(match => match.Rank)
            .ThenBy(match => match.AssetId, StringComparer.Ordinal)
            .ToArray();
        // A requirement may legitimately have multiple ranked candidates for
        // Top-N review.  Completeness therefore has to be evaluated by the
        // set of bound frame requirements, not by the raw candidate count;
        // otherwise duplicate candidates could make a missing frame look
        // generatable.
        var matchedRequirementIds = matches
            .Select(match => match.RequirementId)
            .ToHashSet(StringComparer.Ordinal);
        var session = await _store.LoadSessionAsync(SessionId(request), cancellationToken).ConfigureAwait(false);
        var lockedByRequirement = session?.LockedSelections ?? new Dictionary<string, AiLockedSelection>();
        var hasMissingRequiredMatch = pageBindings.Any(binding =>
            !matchedRequirementIds.Contains(binding.FrameId)
            && !lockedByRequirement.ContainsKey(binding.FrameId));
        var candidates = matches.Select(match =>
        {
            var asset = context.Batch.Assets.FirstOrDefault(candidate =>
                string.Equals(candidate.AssetId, match.AssetId, StringComparison.Ordinal));
            var bindingIndex = Array.FindIndex(pageBindings, candidate =>
                string.Equals(candidate.FrameId, match.RequirementId, StringComparison.Ordinal));
            var binding = bindingIndex >= 0 ? pageBindings[bindingIndex] : null;
            return new AiCandidateProjection(
                context.Batch.BatchId,
                style.StyleId,
                match.RequirementId,
                match.AssetId,
                asset?.FileName ?? "已登记素材",
                match.Rank,
                match.Confidence,
                match.EvidenceCodes,
                lockedByRequirement.TryGetValue(match.RequirementId, out var locked)
                    && string.Equals(locked.AssetId, match.AssetId, StringComparison.Ordinal),
                bindingIndex >= 0 ? $"图片框 {bindingIndex + 1}" : "图片框",
                asset is null ? null : ResolvePreviewPath(context.Batch.InputRoot, asset.SourceRelativePath),
                ToUserSelectionRequirement(
                    binding?.SelectionRequirement.Description ?? "商品主体清晰完整，构图适合当前图片框。"),
                pageName);
        }).ToArray();
        // The v1 result contract scopes warnings by semantic level
        // (request/product/requirement/asset/size_table), not by page or frame
        // identifier. Keep the warning on this page rather than silently
        // dropping valid contract warnings; page-specific isolation still
        // comes from the requirement matches and frame bindings above.
        var warnings = result.Warnings
            .Select(warning => ToWarningText(warning.Code))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var manualNeedsLock = context.Batch.SelectionMode == BatchSelectionMode.Manual
            && pageBindings.Any(binding => !lockedByRequirement.ContainsKey(binding.FrameId));
        if (manualNeedsLock)
        {
            warnings = warnings.Append("人工选图仍需逐个图片框确认并锁定候选。")
                .Distinct(StringComparer.Ordinal).ToArray();
        }
        if (hasMissingRequiredMatch)
        {
            warnings = warnings.Append("必需图片框缺少可信匹配，请选择候选或补充素材。")
                .Distinct(StringComparer.Ordinal).ToArray();
        }
        var businessWarning = hasMissingRequiredMatch
            || result.Warnings.Any(warning => warning.Code is "asset_missing"
                or "variant_conflict"
                or "size_source_conflict");
        var hasUnconfirmedLowConfidence = candidates.Any(candidate =>
            candidate.Confidence < 0.8m
            && !lockedByRequirement.ContainsKey(candidate.RequirementId));
        var status = businessWarning
            ? AiPageUiStatus.BusinessError
            : manualNeedsLock || hasUnconfirmedLowConfidence
                ? AiPageUiStatus.ReviewRequired
                : AiPageUiStatus.Ready;
        var statusText = status switch
        {
            AiPageUiStatus.Ready => "可生成",
            AiPageUiStatus.ReviewRequired => "待人工确认",
            AiPageUiStatus.BusinessError => "需要修复",
            _ => "待处理"
        };
        return new(
            context.Batch.BatchId,
            style.StyleId,
            page.PageId,
            pageName,
            status,
            statusText,
            status == AiPageUiStatus.Ready ? "已通过" : "处理此页",
            candidates,
            warnings,
            manualNeedsLock || hasUnconfirmedLowConfidence,
            BuildRequirementSelections(
                context,
                style,
                page,
                pageBindings,
                matches,
                lockedByRequirement.Values,
                AiRequirementSelectionUiStatus.Missing,
                "待人工选择"));
    }

    private static AiBatchUiStatus DetermineBatchStatus(
        List<AiStyleAnalysisProjection> styles,
        AiAnalysisSchedulingControlState controlState,
        bool manualSelection,
        bool budgetStopped)
    {
        var pages = styles.SelectMany(style => style.Pages).ToArray();
        if (controlState == AiAnalysisSchedulingControlState.Cancelled
            || pages.Length > 0 && pages.All(page => page.Status == AiPageUiStatus.Cancelled))
        {
            return AiBatchUiStatus.Cancelled;
        }
        if (styles.Any(style => style.Status == AiStyleUiStatus.SystemError))
        {
            return AiBatchUiStatus.RecoveryRequired;
        }
        var allSettled = pages.Length > 0 && pages.All(page => page.Status is
            AiPageUiStatus.Ready or AiPageUiStatus.ReviewRequired
                or AiPageUiStatus.BusinessError or AiPageUiStatus.Cancelled);
        var hasExecutablePage = pages.Any(page => page.Status is AiPageUiStatus.Ready or AiPageUiStatus.ReviewRequired);
        // A hard budget stop is an actionable batch state only while work is
        // still unresolved. If all pages have settled, preserve the terminal
        // business result (ready, review, or no-output) instead of hiding it
        // behind a historical budget boundary.
        if (budgetStopped && !allSettled)
        {
            return AiBatchUiStatus.BudgetStopped;
        }
        if (allSettled && !hasExecutablePage)
        {
            return AiBatchUiStatus.NotGeneratable;
        }
        if (styles.Count > 0 && styles.All(style => style.Status == AiStyleUiStatus.Ready))
        {
            return AiBatchUiStatus.Ready;
        }
        if (manualSelection && pages.Any(page => page.RequiresManualConfiguration))
        {
            return AiBatchUiStatus.ManualConfiguration;
        }
        if (styles.Any(style => style.Status is AiStyleUiStatus.ReviewRequired or AiStyleUiStatus.BusinessError))
        {
            return AiBatchUiStatus.ReviewRequired;
        }
        if (styles.Any(style => style.Status is AiStyleUiStatus.Analysing or AiStyleUiStatus.Waiting))
        {
            return styles.Any(style => style.Status == AiStyleUiStatus.Analysing)
                ? AiBatchUiStatus.Analysing
                : AiBatchUiStatus.Waiting;
        }
        return AiBatchUiStatus.Failed;
    }

    private static AiStyleAssetProjection[] BuildStyleAssetProjections(
        BatchContext context,
        StyleDraft style)
    {
        var assetsById = context.Batch.Assets.ToDictionary(asset => asset.AssetId, StringComparer.Ordinal);
        return style.AssetIds
            .Select((assetId, index) => (assetId, index))
            .Where(item => assetsById.ContainsKey(item.assetId))
            .Select(item =>
            {
                var asset = assetsById[item.assetId];
                return new AiStyleAssetProjection(
                    context.Batch.BatchId,
                    style.StyleId,
                    asset.AssetId,
                    ResolvePreviewPath(context.Batch.InputRoot, asset.SourceRelativePath),
                    item.index + 1);
            })
            .ToArray();
    }

    private static AiRequirementSelectionProjection[] BuildRequirementSelections(
        BatchContext context,
        StyleDraft style,
        TemplatePage page,
        IReadOnlyList<FrameBinding> pageBindings,
        IEnumerable<AiRequirementMatchV1> matches,
        IEnumerable<AiLockedSelection> lockedSelections,
        AiRequirementSelectionUiStatus emptyStatus,
        string emptyStatusText)
    {
        var assetsById = context.Batch.Assets
            .Where(asset => string.Equals(asset.StyleId, style.StyleId, StringComparison.Ordinal))
            .ToDictionary(asset => asset.AssetId, StringComparer.Ordinal);
        var matchesByRequirement = matches
            .GroupBy(match => match.RequirementId, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.OrderBy(match => match.Rank)
                    .ThenBy(match => match.AssetId, StringComparer.Ordinal)
                    .First(),
                StringComparer.Ordinal);
        var lockedByRequirement = lockedSelections
            .GroupBy(selection => selection.RequirementId, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.OrderByDescending(selection => selection.SelectionRevision).First(),
                StringComparer.Ordinal);

        return pageBindings.Select((binding, index) =>
        {
            lockedByRequirement.TryGetValue(binding.FrameId, out var locked);
            matchesByRequirement.TryGetValue(binding.FrameId, out var recommended);
            var selectedAssetId = locked?.AssetId ?? recommended?.AssetId;
            var selectedAsset = selectedAssetId is null
                ? null
                : assetsById.GetValueOrDefault(selectedAssetId);
            var status = locked is not null
                ? AiRequirementSelectionUiStatus.ManualLocked
                : recommended is not null
                    ? AiRequirementSelectionUiStatus.AiSelected
                    : emptyStatus;
            var statusText = status switch
            {
                AiRequirementSelectionUiStatus.ManualLocked => "人工选用 · 已锁定",
                AiRequirementSelectionUiStatus.AiSelected => "AI 推荐 · 已选用",
                _ => emptyStatusText
            };
            return new AiRequirementSelectionProjection(
                context.Batch.BatchId,
                style.StyleId,
                page.PageId,
                page.Name,
                binding.FrameId,
                $"图片框 {index + 1}",
                ToUserSelectionRequirement(binding.SelectionRequirement.Description),
                status,
                statusText,
                selectedAssetId,
                selectedAsset is null
                    ? null
                    : ResolvePreviewPath(context.Batch.InputRoot, selectedAsset.SourceRelativePath));
        }).ToArray();
    }

    private static string ToWarningText(string code) => code switch
    {
        "low_confidence" => "匹配置信度较低，请人工确认。",
        "asset_missing" => "缺少必需商品素材，请补充后重试。",
        "variant_conflict" => "检测到变体冲突，请确认当前款式素材。",
        "size_source_conflict" => "检测到多个尺码来源，请先确认来源。",
        _ => "分析结果需要人工确认。"
    };

    private static string ToUserSelectionRequirement(string value)
    {
        var normalized = value.Trim();
        return normalized switch
        {
            "请为这个图片框选择一张代表款商品图。"
                or "请选择一张代表款商品图放入当前图片框。"
                or "请选择符合当前图片框要求的商品图。"
                => "商品主体清晰完整，构图适合当前图片框。",
            _ => normalized
        };
    }

    private static AiAnalysisProjectionOptions ProjectionOptions(string batchId, string? consentId = null) => new(
        $"wpf-{batchId}",
        consentId ?? $"consent-pending-{batchId}",
        ModelConfigVersion,
        PromptContractVersion,
        CompressionProfileVersion);

    private async Task<AiUploadConsentRecord> RequireUploadConsentAsync(CancellationToken cancellationToken)
    {
        var consent = await _consent.GetConsentAsync(
            AiConsentScopes.AnalysisCopy,
            cancellationToken).ConfigureAwait(false);
        if (consent?.IsGranted != true)
        {
            throw new InvalidOperationException(
                consent?.State == AiUploadConsentState.Revoked
                    ? "AI_UPLOAD_CONSENT_REVOKED"
                    : "AI_UPLOAD_CONSENT_REQUIRED");
        }

        return consent;
    }

    private static string ToInputTypeText(BatchInputType? inputType) => inputType switch
    {
        BatchInputType.SingleStyle => "单款图片目录",
        BatchInputType.StyleDirectoryBatch => "正常批次目录",
        _ => "等待识别"
    };

    private static string? ResolvePreviewPath(string inputRoot, string sourceRelativePath)
    {
        try
        {
            var root = Path.GetFullPath(inputRoot)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var candidate = Path.GetFullPath(Path.Combine(
                root,
                sourceRelativePath.Replace('/', Path.DirectorySeparatorChar)));
            var prefix = root + Path.DirectorySeparatorChar;
            return candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                ? candidate
                : null;
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException)
        {
            return null;
        }
    }

    private static string ToPageSummary(IEnumerable<BatchTemplatePageSnapshot> pages)
    {
        var snapshots = pages.ToArray();
        var sizePages = snapshots.Count(page => page.ContentType == TemplatePageContentType.DynamicSizeTable);
        var ordinaryPages = snapshots.Length - sizePages;
        return sizePages == 0
            ? $"普通页面 {ordinaryPages}"
            : $"普通页面 {ordinaryPages} · 动态尺码页 {sizePages}";
    }

    private static async Task EnsureSchedulerRestoredAsync(BatchContext context, CancellationToken cancellationToken)
    {
        await context.RestoreGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (context.SchedulerRestoreAttempted)
            {
                return;
            }

            context.SchedulerRestoreAttempted = true;
            try
            {
                _ = await context.Scheduler.RestoreAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (InvalidOperationException exception)
            {
                context.SchedulerRestoreError = exception.Message;
            }
        }
        finally
        {
            context.RestoreGate.Release();
        }
    }

    private static AiAnalysisRequestV1 GetFirstLayerRequest(BatchContext context, string styleId) =>
        context.Requests.Values.FirstOrDefault(request =>
            request.StyleId == styleId && request.AnalysisKind == AiAnalysisKind.ProductJointUnderstanding)
        ?? throw new InvalidOperationException("AI_REQUEST_NOT_FOUND");

    private static string SessionId(AiAnalysisRequestV1 request) => $"session-{request.RequestId}";

    private sealed class BatchContext(
        BatchDraft batch,
        TemplateRevision templateRevision,
        string templateGroupName,
        AiAnalysisScheduler scheduler) : IDisposable
    {
        public BatchDraft Batch { get; } = batch;
        public TemplateRevision TemplateRevision { get; } = templateRevision;
        public string TemplateGroupName { get; } = templateGroupName;
        public AiAnalysisScheduler Scheduler { get; } = scheduler;
        public ConcurrentDictionary<string, AiAnalysisRequestV1> Requests { get; } = new(StringComparer.Ordinal);
        public SemaphoreSlim RestoreGate { get; } = new(1, 1);
        public object BackgroundGate { get; } = new();
        public Task? BackgroundTask { get; set; }
        public bool SchedulerRestoreAttempted { get; set; }
        public string? SchedulerRestoreError { get; set; }
        public bool Started { get; set; }

        public void Dispose()
        {
            Scheduler.Dispose();
            RestoreGate.Dispose();
        }
    }
}
