using Desktop.Contracts.Ai;
using Desktop.Domain.Ai;
using Desktop.Domain.Inputs;
using Desktop.Domain.Templates;
using Desktop.Application.Inputs;
using Desktop.Application.SizeTables;
using Desktop.Application.Templates;
using Desktop.Domain.SizeTables;
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
    Cancelled,
    /// <summary>AI selected a usable candidate but recommends a quick check.</summary>
    ReviewRequired,
    /// <summary>The current AI candidate cannot be used safely without a user fix.</summary>
    Unsafe
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
    string? SelectedPreviewPath,
    string? SelectedAssetDisplayName = null)
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

    public string LockText => IsLocked ? "已确认" : "确认";

    public bool CanLock => !IsLocked;

    private static string ToEvidenceText(string code) => code switch
    {
        "front_view" => "正面",
        "back_view" => "背面",
        "detail_view" => "细节",
        AiTargetMatchEvidence.Exact => "精确符合要求",
        AiTargetMatchEvidence.SafeSimilar => "安全的相近替代",
        AiTargetMatchEvidence.MaterialDifference => "与要求存在实质差异",
        AiTargetMatchEvidence.Unsafe => "不安全候选",
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

    /// <summary>
    /// Yellow is an executable state.  Only a page which explicitly carries a
    /// manual-configuration gate is withheld from generation.
    /// </summary>
    public bool CanGenerate => Status == AiPageUiStatus.Ready
        || (Status == AiPageUiStatus.ReviewRequired && !RequiresManualConfiguration);
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

    public bool HasReadyPage => Pages.Any(page => page.CanGenerate);

    /// <summary>
    /// A style can be shown in the executable queue when it has at least one
    /// usable page and every remaining page is either intentionally isolated
    /// or cancelled.  Advisory-yellow pages are deliberately included: they
    /// are already filled by AI and do not require a per-frame confirmation.
    /// </summary>
    public bool CanGenerate => Pages.Any(page => page.CanGenerate)
        && Pages.All(page => page.CanGenerate
            || page.Status is AiPageUiStatus.BusinessError or AiPageUiStatus.Cancelled);

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

    /// <summary>
    /// A terminal page can still require business or system review. Keep the
    /// processed count separate from the executable count so the UI never
    /// makes a completed analysis look like an unfinished upload.
    /// </summary>
    public int ProcessedPageCount => Styles.SelectMany(style => style.Pages)
        .Count(page => page.Status is AiPageUiStatus.Ready
            or AiPageUiStatus.ReviewRequired
            or AiPageUiStatus.BusinessError
            or AiPageUiStatus.SystemError
            or AiPageUiStatus.Cancelled);

    public string ProgressText =>
        $"已完成 {CompletedStyleCount}/{StyleCount} 款 · 已处理 {ProcessedPageCount}/{TotalPageCount} 页 · 可生成 {ExecutablePageCount}/{TotalPageCount} 页";

    public int ExecutablePageCount => Styles.SelectMany(style => style.Pages)
        .Count(page => page.Status == AiPageUiStatus.Ready
            || page.Status == AiPageUiStatus.ReviewRequired
                && !page.RequiresManualConfiguration);

    public int IsolatedPageCount => Styles.SelectMany(style => style.Pages)
        .Count(page => page.Status == AiPageUiStatus.BusinessError);

    public bool HasUnsettledPages => Styles.SelectMany(style => style.Pages)
        .Any(page => page.Status is AiPageUiStatus.Waiting or AiPageUiStatus.Analysing);

    public bool CanEnterGeneration => (Status is AiBatchUiStatus.Ready or AiBatchUiStatus.ReviewRequired)
        && ExecutablePageCount > 0
        && Styles.SelectMany(style => style.Pages).All(page =>
            page.CanGenerate
            || page.Status is AiPageUiStatus.BusinessError or AiPageUiStatus.Cancelled);

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
        or AiBatchUiStatus.Failed
        || Status == AiBatchUiStatus.ReviewRequired && !HasUnsettledPages;
}

/// <summary>
/// Coordinates the already implemented INPUT-01 and AI-S3 producers into the
/// B-03 projection.  Results are always read from the durable result store;
/// an absent result is never interpreted as a successful analysis.
/// </summary>
public sealed class AiBatchAnalysisWorkflow : IAiBatchAnalysisWorkflow, IDisposable
{
    private const string ModelConfigVersion = AiAnalysisProductionPolicy.ModelConfigVersionV2;
    private const string PromptContractVersion = AiAnalysisProductionPolicy.PromptContractVersionV3;
    private const string CompressionProfileVersion = AiAnalysisCopyProfiles.WicBoundedV4;

    private readonly IBatchRepository _batches;
    private readonly ITemplateRepository _templates;
    private readonly IAiAnalysisStore _store;
    private readonly IAiAnalysisResultStore _results;
    private readonly IAiAnalysisSchedulingStateStore _schedulingState;
    private readonly IAiAnalysisSchedulerFactory _schedulerFactory;
    private readonly IConsentPort _consent;
    private readonly ITemplateResourceRepository? _templateResources;
    private readonly IAiRenderPlanFactory? _renderPlans;
    private readonly ISizeTableRepository? _sizeTables;
    private readonly ISizeTableRasterizer? _sizeTableRasterizer;
    private readonly IAiAnalysisCheckpointPort? _checkpointPort;
    private readonly ConcurrentDictionary<string, BatchContext> _contexts = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _contextGate = new(1, 1);
    private readonly CancellationTokenSource _backgroundCancellation = new();

    public void Dispose()
    {
        _backgroundCancellation.Cancel();
        foreach (var context in _contexts.Values)
        {
            context.StopAdmission();
        }

        // StartAsync now returns while admission/consumer work continues. Do
        // not dispose the scheduler or its file store underneath those tasks;
        // otherwise shutdown can race an atomic staging write and leave a
        // temporary file locked. The bounded wait only applies to shutdown;
        // Provider calls retain the adapter's bounded total/stage deadlines
        // (300 seconds total, with a 180-second upload cap).  Shutdown must
        // not dispose a request while its deferred observer is still draining.
        foreach (var context in _contexts.Values)
        {
            foreach (var task in new[] { context.AdmissionTask, context.BackgroundTask }
                         .Where(task => task is not null)
                         .Select(task => task!))
            {
                try
                {
                    _ = task.Wait(TimeSpan.FromSeconds(5));
                }
                catch (AggregateException)
                {
                    // The durable scheduler state is the recovery source; an
                    // exception during shutdown must not mask application exit.
                }
            }
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
        _results = results ?? throw new ArgumentNullException(nameof(results));
        _schedulingState = schedulingState ?? throw new ArgumentNullException(nameof(schedulingState));
        _schedulerFactory = schedulerFactory ?? throw new ArgumentNullException(nameof(schedulerFactory));
        _consent = consent ?? throw new ArgumentNullException(nameof(consent));
        _templateResources = templateResources;
        _renderPlans = renderPlans;
        _sizeTables = sizeTables;
        _sizeTableRasterizer = sizeTableRasterizer;
        _checkpointPort = checkpointPort;
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
        await EnsureAnalysisStateRecoveredAsync(context, cancellationToken).ConfigureAwait(false);
        ThrowIfAnalysisStateUnavailable(context);
        if (context.Batch.ScanStatus != BatchScanStatus.ScanReady)
        {
            throw new InvalidOperationException("AI_INPUT_BATCH_NOT_READY");
        }

        var consent = await RequireUploadConsentAsync(cancellationToken).ConfigureAwait(false);

        var projectionOptions = ProjectionOptions(context.Batch.BatchId, consent.ConsentId);
        context.Started = true;
        await RefreshRequestsAsync(context, cancellationToken).ConfigureAwait(false);
        StartAdmission(context, projectionOptions);
        StartBackgroundProcessing(context);
        var projection = await BuildProjectionAsync(context, cancellationToken).ConfigureAwait(false);
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
        await EnsureAnalysisStateRecoveredAsync(context, cancellationToken).ConfigureAwait(false);
        await RefreshRequestsAsync(context, cancellationToken).ConfigureAwait(false);
        if (context.Started
            && context.Scheduler.ControlState == AiAnalysisSchedulingControlState.Running
            && (context.Scheduler.ReadyCount > 0 || context.Scheduler.PendingCount > 0))
        {
            StartBackgroundProcessing(context);
        }
        return await BuildProjectionAsync(context, cancellationToken).ConfigureAwait(false);
    }

    public Task<AiBatchAnalysisProjection> PauseAsync(string batchId, CancellationToken cancellationToken) =>
        ControlAsync(batchId, (scheduler, token) => scheduler.RequestPauseAsync("用户已暂停分析；可恢复继续。", token), cancellationToken);

    public Task<AiBatchAnalysisProjection> ResumeAsync(string batchId, CancellationToken cancellationToken) =>
        ControlAsync(batchId, (scheduler, token) => scheduler.ResumeAsync(token), cancellationToken);

    public async Task<AiBatchAnalysisProjection> CancelAsync(
        string batchId,
        CancellationToken cancellationToken)
    {
        var context = await LoadContextAsync(batchId, cancellationToken).ConfigureAwait(false);
        context.StopAdmission();
        return await ControlAsync(
            batchId,
            (scheduler, token) => scheduler.CancelAsync(batchId, token),
            cancellationToken).ConfigureAwait(false);
    }

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
            var resultInputs = await LoadStyleResultsAsync(
                context,
                style.StyleId,
                cancellationToken).ConfigureAwait(false);
            foreach (var input in resultInputs)
            {
                if (input.Ledger?.UpdatedAt > planCreatedAt)
                {
                    planCreatedAt = input.Ledger.UpdatedAt;
                }
            }

            var selectedRequirements = projection.Styles
                .Single(item => string.Equals(item.StyleId, style.StyleId, StringComparison.Ordinal))
                .Pages
                .Where(page => page.CanGenerate)
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
                        null,
                        revision);
                })
                .ToArray();
            var selectedAssetIds = selectedRequirements
                .Select(selection => selection.AssetId)
                .ToHashSet(StringComparer.Ordinal);
            var geometryByAsset = resultInputs
                .Where(input => input.Result is not null
                    && input.Request.AnalysisKind is AiAnalysisKind.ProductJointUnderstanding
                        or AiAnalysisKind.SelectedAssetGeometry
                    && (input.Request.AnalysisKind == AiAnalysisKind.ProductJointUnderstanding
                        || input.Request.Assets.Any(asset => selectedAssetIds.Contains(asset.AssetId))))
                .SelectMany(input => input.Result!.SubjectGeometry)
                .Where(geometry => selectedAssetIds.Contains(geometry.AssetId))
                .GroupBy(geometry => geometry.AssetId, StringComparer.Ordinal)
                .ToDictionary(
                    group => group.Key,
                    group => group.OrderByDescending(geometry => geometry.Confidence).First(),
                    StringComparer.Ordinal);
            selectedRequirements = selectedRequirements.Select(selection => selection with
            {
                SubjectGeometry = geometryByAsset.GetValueOrDefault(selection.AssetId)
            }).ToArray();
            var sizeResultInputs = resultInputs.Where(input => input.Result is not null
                && input.Request.AnalysisKind is AiAnalysisKind.ProductJointUnderstanding
                    or AiAnalysisKind.SizeTableExtraction)
                .ToArray();
            var sizeCandidates = sizeResultInputs
                .Where(input => input.Result!.SizeTableCandidate is not null)
                .Select(input => input.Result!.SizeTableCandidate!)
                .GroupBy(candidate => JsonSerializer.Serialize(new
                {
                    candidate.SourceAssetId,
                    candidate.Columns,
                    candidate.Rows
                }), StringComparer.Ordinal)
                .Select(group => group.OrderByDescending(candidate => candidate.Confidence).First())
                .ToArray();
            var hasProviderConflict = sizeResultInputs.SelectMany(input => input.Result!.Warnings)
                .Any(warning => warning.Code == "size_source_conflict");
            var sizeCandidate = sizeCandidates.Length == 1 && !hasProviderConflict
                ? sizeCandidates[0]
                : null;
            var preparedSizePages = Array.Empty<AiPreparedSizeTablePage>();
            string? sizeTableReason = null;
            if (context.TemplateRevision.Pages.Any(page =>
                    page.ContentType == TemplatePageContentType.DynamicSizeTable))
            {
                var preparation = await PrepareSizeTablesAsync(
                    context,
                    style,
                    sizeCandidates,
                    hasProviderConflict,
                    planCreatedAt,
                    cancellationToken).ConfigureAwait(false);
                preparedSizePages = preparation.Pages;
                sizeTableReason = preparation.ReasonCode;
            }
            styleInputs.Add(new AiRenderStyleInput(
                style.StyleId,
                selectedRequirements,
                sizeCandidate,
                preparedSizePages,
                sizeTableReason));
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

    private async Task<PreparedSizeTableResult> PrepareSizeTablesAsync(
        BatchContext context,
        StyleDraft style,
        AiSizeTableCandidateV1[] candidates,
        bool hasProviderConflict,
        DateTimeOffset createdAt,
        CancellationToken cancellationToken)
    {
        if (hasProviderConflict)
        {
            return new PreparedSizeTableResult([], "SIZE_TABLE_SOURCE_CONFLICT");
        }
        if (candidates.Length == 0)
        {
            return new PreparedSizeTableResult([], "SIZE_TABLE_SOURCE_MISSING");
        }
        if (_sizeTables is null || _sizeTableRasterizer is null)
        {
            return new PreparedSizeTableResult([], "AI_SIZE_TABLE_RASTERIZER_PENDING");
        }

        var manifest = ProjectManifest(context.Batch);
        var prepared = new List<AiPreparedSizeTablePage>();
        string? blockingReason = null;
        foreach (var page in context.TemplateRevision.Pages
                     .Where(page => page.ContentType == TemplatePageContentType.DynamicSizeTable)
                     .OrderBy(page => page.Order))
        {
            var result = SizeTableService.Prepare(new PrepareSizeTableCommand(
                manifest,
                style.StyleId,
                page.PageId,
                candidates,
                null,
                0,
                createdAt));
            if (result.Table is null)
            {
                blockingReason = result.ReasonCode;
                continue;
            }

            var table = result.Table;
            var persisted = await _sizeTables.LoadAsync(
                context.Batch.BatchId,
                style.StyleId,
                page.PageId,
                cancellationToken).ConfigureAwait(false);
            if (persisted.Status == SizeTableLoadStatus.RecoveryRequired)
            {
                blockingReason = "SIZE_TABLE_STATE_CORRUPT";
                continue;
            }
            if (persisted.Table is not null)
            {
                if (!string.Equals(
                        persisted.Table.Source.ContentDigest,
                        table.Source.ContentDigest,
                        StringComparison.Ordinal))
                {
                    blockingReason = "SIZE_TABLE_SOURCE_CHANGE_REVIEW_REQUIRED";
                    continue;
                }
                table = persisted.Table;
            }
            else
            {
                var save = await _sizeTables.SaveAsync(table, null, cancellationToken).ConfigureAwait(false);
                if (save.Status != SizeTableSaveStatus.Saved)
                {
                    blockingReason = save.ReasonCode ?? "SIZE_TABLE_STATE_CORRUPT";
                    continue;
                }
            }

            if (!table.CanSave)
            {
                blockingReason = table.Status == SizeTableStatus.ReviewRequired
                    ? "SIZE_TABLE_REVIEW_REQUIRED"
                    : "SIZE_TABLE_INVALID";
                continue;
            }

            var raster = await _sizeTableRasterizer.RasterizeAsync(
                new SizeTableRasterizationRequest(
                    table,
                    TemplateBoundRasterStyle(context.TemplateRevision.RuleDigest!)),
                cancellationToken).ConfigureAwait(false);
            prepared.Add(new AiPreparedSizeTablePage(page.PageId, table, raster));
        }

        return new PreparedSizeTableResult(
            prepared.ToArray(),
            blockingReason ?? (prepared.Count > 0 ? "SIZE_TABLE_VALID" : "SIZE_TABLE_SOURCE_MISSING"));
    }

    private static AssetManifestV1 ProjectManifest(BatchDraft batch) => new(
        AssetManifestProtocol.Version,
        $"manifest_{batch.BatchId}",
        batch.BatchId,
        batch.InputType == BatchInputType.SingleStyle ? "single_style" : "style_directory_batch",
        new AssetManifestTemplateSnapshot(
            batch.TemplateSnapshot.SnapshotId,
            batch.TemplateSnapshot.RevisionId,
            batch.TemplateSnapshot.RulesDigest,
            batch.TemplateSnapshot.Pages.OrderBy(page => page.Order).Select(page => page.PageId).ToArray()),
        batch.Styles.OrderBy(style => style.Order).Select(style => new AssetManifestStyle(
            style.StyleId,
            style.DefaultStyleCode,
            style.SourceDirectoryRelativePath,
            style.Order,
            style.AssetIds.ToArray())).ToArray(),
        batch.Assets.OrderBy(asset => asset.SourceRelativePath, StringComparer.Ordinal).Select(asset =>
            new AssetManifestAsset(
                asset.AssetId,
                asset.StyleId,
                asset.SourceRelativePath,
                asset.FileName,
                asset.Length,
                asset.Sha256,
                asset.PixelWidth,
                asset.PixelHeight,
                asset.Format.ToString().ToLowerInvariant(),
                asset.MimeType,
                asset.Readability == AssetReadability.Readable ? "readable" : "unreadable")).ToArray(),
        batch.ManifestDigest ?? throw new InvalidOperationException("SIZE_TABLE_MANIFEST_MISSING"),
        batch.UpdatedAt);

    private static SizeTableRasterStyle TemplateBoundRasterStyle(string rulesDigest)
    {
        var suffix = rulesDigest.StartsWith("sha256:", StringComparison.Ordinal)
            ? new string(rulesDigest.AsSpan(7, 16))
            : throw new InvalidOperationException("SIZE_TABLE_TEMPLATE_RULES_DIGEST_INVALID");
        return SizeTableRasterStyle.Create(
            $"template_{suffix}_v1",
            "dynamic_grid_v1",
            "Microsoft YaHei UI",
            24,
            1000,
            56,
            24,
            12,
            "#FFFFFFFF",
            "#FFF2F4F7",
            "#FF101828",
            "#FF98A2B3");
    }

    private sealed record PreparedSizeTableResult(
        AiPreparedSizeTablePage[] Pages,
        string ReasonCode);

    private async Task<AiBatchAnalysisProjection> ControlAsync(
        string batchId,
        Func<AiAnalysisScheduler, CancellationToken, Task> command,
        CancellationToken cancellationToken)
    {
        var context = await LoadContextAsync(batchId, cancellationToken).ConfigureAwait(false);
        await EnsureSchedulerRestoredAsync(context, cancellationToken).ConfigureAwait(false);
        await EnsureAnalysisStateRecoveredAsync(context, cancellationToken).ConfigureAwait(false);
        await command(context.Scheduler, cancellationToken).ConfigureAwait(false);
        await RefreshRequestsAsync(context, cancellationToken).ConfigureAwait(false);
        if (context.Scheduler.ControlState == AiAnalysisSchedulingControlState.Running)
        {
            StartBackgroundProcessing(context);
        }
        return await BuildProjectionAsync(context, cancellationToken).ConfigureAwait(false);
    }

    private void StartAdmission(
        BatchContext context,
        AiAnalysisProjectionOptions projectionOptions)
    {
        if (!context.TryStartAdmission())
        {
            return;
        }

        context.AdmissionTask = Task.Run(
            async () =>
            {
                using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                    _backgroundCancellation.Token,
                    context.AdmissionCancellation.Token);
                try
                {
                    var result = await context.Scheduler.EnqueueBatchAsync(
                        context.Batch,
                        context.TemplateRevision,
                        projectionOptions,
                        linkedCancellation.Token).ConfigureAwait(false);
                    foreach (var styleResult in result.StyleResults.Where(item =>
                                 item.Disposition == AiAnalysisWorkDisposition.TerminalError
                                 && !string.IsNullOrWhiteSpace(item.ErrorCode)))
                    {
                        context.AdmissionFailures[styleResult.RequestId] = styleResult.ErrorCode!;
                    }
                }
                catch (OperationCanceledException) when (linkedCancellation.IsCancellationRequested)
                {
                    if (!_backgroundCancellation.IsCancellationRequested
                        && !context.AdmissionCancellation.IsCancellationRequested)
                    {
                        context.AdmissionError = "AI_ANALYSIS_ADMISSION_CANCELLED";
                    }
                }
                catch (Exception exception)
                {
                    // Keep the UI actionable without exposing exception text or
                    // local paths. The durable scheduler remains the source of
                    // truth for any requests admitted before the failure.
                    context.AdmissionError = NormalizeAdmissionError(exception);
                }
                finally
                {
                    context.MarkAdmissionCompleted();
                }
            });
    }

    private static string NormalizeAdmissionError(Exception exception) => exception switch
    {
        InvalidOperationException invalid when !string.IsNullOrWhiteSpace(invalid.Message)
            && invalid.Message.StartsWith("AI_", StringComparison.Ordinal)
            => invalid.Message,
        InvalidDataException invalid when !string.IsNullOrWhiteSpace(invalid.Message)
            && invalid.Message.StartsWith("AI_", StringComparison.Ordinal)
            => invalid.Message,
        IOException io when !string.IsNullOrWhiteSpace(io.Message)
            && io.Message.StartsWith("AI_", StringComparison.Ordinal)
            => io.Message,
        _ => "AI_ANALYSIS_ADMISSION_FAILED"
    };

    private static async Task ProcessBatchAsync(
        BatchContext context,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // A producer can be preparing the next款 while the ready buffer is
            // temporarily empty. Do not exit early; otherwise StartAsync would
            // regress to "prepare everything, then consume".
            if (context.AdmissionCompleted
                && context.Scheduler.PendingCount == 0
                && context.Scheduler.ReadyCount == 0)
            {
                // Keep the consumer alive while the batch remains open. A user
                // can enqueue a selected-asset refinement immediately after
                // the first layer becomes ready; returning here creates a
                // narrow enqueue/exit race in which the refinement is durable
                // but no consumer remains to process it.
                await Task.Delay(50, cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (context.Scheduler.ControlState is AiAnalysisSchedulingControlState.Paused
                or AiAnalysisSchedulingControlState.PauseRequested
                or AiAnalysisSchedulingControlState.WaitingForNetwork
                or AiAnalysisSchedulingControlState.CancelRequested
                or AiAnalysisSchedulingControlState.Cancelled)
            {
                await Task.Delay(100, cancellationToken).ConfigureAwait(false);
                continue;
            }

            var readyCount = context.Scheduler.ReadyCount;
            if (readyCount == 0)
            {
                await Task.Delay(50, cancellationToken).ConfigureAwait(false);
                continue;
            }

            var workerCount = Math.Min(
                AiAnalysisProductionPolicy.QueueV1.MaxConcurrentRequests,
                readyCount);
            var results = await Task.WhenAll(Enumerable.Range(0, workerCount)
                .Select(_ => context.Scheduler.ProcessNextAsync(cancellationToken))).ConfigureAwait(false);
            foreach (var result in results.Where(result =>
                         result.Disposition is (AiAnalysisWorkDisposition.TerminalError
                             or AiAnalysisWorkDisposition.ResultUnknown)
                         && !string.IsNullOrWhiteSpace(result.ErrorCode)))
            {
                context.RuntimeFailures[result.RequestId] = result.ErrorCode!;
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

        context.TerminalErrors.Clear();
        foreach (var pair in state?.TerminalErrors
                     ?? Enumerable.Empty<KeyValuePair<string, string>>())
        {
            if (context.Requests.ContainsKey(pair.Key))
            {
                context.TerminalErrors[pair.Key] = pair.Value;
            }
        }
        foreach (var pair in context.Scheduler.TerminalErrorsSnapshot)
        {
            if (context.Requests.ContainsKey(pair.Key))
            {
                context.TerminalErrors[pair.Key] = pair.Value;
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
        // The product queue accepts both green and advisory-yellow pages. Keep
        // the aggregate count aligned with the same page-level gate used by
        // the generation button.
        var readyPages = pages.Count(page => page.CanGenerate);
        var controlState = context.Scheduler.ControlState;
        var status = context.SchedulerRestoreError is not null
            ? AiBatchUiStatus.RecoveryRequired
            : DetermineBatchStatus(
                styles,
                controlState,
                context.Batch.SelectionMode == BatchSelectionMode.Manual,
                context.Scheduler.Budget.HardStopped);
        var oversizedRequest = context.TerminalErrors.Any(pair =>
            string.Equals(pair.Value, "AI_PROVIDER_REQUEST_TOO_LARGE", StringComparison.Ordinal))
            || context.RuntimeFailures.Any(pair =>
                string.Equals(pair.Value, "AI_PROVIDER_REQUEST_TOO_LARGE", StringComparison.Ordinal));
        var responseReferenceMismatch = context.TerminalErrors.Any(pair =>
            string.Equals(pair.Value, "AI_PROVIDER_RESPONSE_REFERENCE_MISMATCH", StringComparison.Ordinal))
            || context.RuntimeFailures.Any(pair =>
                string.Equals(pair.Value, "AI_PROVIDER_RESPONSE_REFERENCE_MISMATCH", StringComparison.Ordinal));
        var providerSubmissionFailure = context.TerminalErrors.Values
            .Concat(context.RuntimeFailures.Values)
            .FirstOrDefault(IsProviderSubmissionFailureCode);
        var providerUnknownCode = context.TerminalErrors.Values
            .Concat(context.RuntimeFailures.Values)
            .FirstOrDefault(IsSpecificProviderUnknownCode);
        var hasBlockingConfiguration = pages.Any(page =>
            page.Status == AiPageUiStatus.ReviewRequired
            && page.RequiresManualConfiguration);
        var hasBlockedPages = pages.Any(page =>
            page.Status is AiPageUiStatus.BusinessError or AiPageUiStatus.SystemError
            || page.Status == AiPageUiStatus.ReviewRequired
                && page.RequiresManualConfiguration);
        var message = controlState switch
        {
            AiAnalysisSchedulingControlState.Paused when context.Scheduler.ControlReason ==
                "AI_UPLOAD_CONSENT_UNAVAILABLE"
                => "无法读取本地上传授权状态；已安全暂停且未发送请求。修复本地存储后可恢复分析。",
            AiAnalysisSchedulingControlState.Paused when context.Scheduler.ControlReason is
                "AI_PROVIDER_CREDENTIAL_MISSING"
                or "AI_PROVIDER_CREDENTIAL_UNAVAILABLE"
                or "AI_PROVIDER_CREDENTIAL_INVALID"
                => "本机 AI 凭据尚未就绪；请求未发送。修复凭据后点击恢复即可继续当前失败范围。",
            AiAnalysisSchedulingControlState.Paused when context.Scheduler.ControlReason is
                "AI_UPLOAD_CONSENT_REQUIRED" or "AI_UPLOAD_CONSENT_REVOKED"
                => "上传授权未完成或已撤回；请在设置中重新授权后恢复分析。",
            AiAnalysisSchedulingControlState.Paused => "分析已暂停；点击恢复后从安全检查点继续。",
            AiAnalysisSchedulingControlState.WaitingForNetwork => context.Scheduler.ControlReason
                ?? "等待网络恢复；不会自动重发未知结果。",
            _ when oversizedRequest => "本款最终 JSON 请求体超过 16 MiB；未发送 AI 请求。请减少本款照片后重新建立批次。",
            _ when responseReferenceMismatch => "AI 已返回，但结果引用无法与本批次安全对应，已阻止选图和生成。请使用修复版本重新建立批次。",
            _ when providerSubmissionFailure is not null => "AI 请求体未完整上传，火山未受理。请检查网络后重试分析。",
            _ when providerUnknownCode is not null => DescribeProviderUnknown(providerUnknownCode).BatchMessage,
            _ => status switch
            {
                AiBatchUiStatus.Ready => "分析完成，所有已选页面均可生成。",
                AiBatchUiStatus.ManualConfiguration => "分析完成，请逐个图片框选择并锁定素材。",
                AiBatchUiStatus.ReviewRequired when !hasBlockedPages
                    => "分析完成；绿色和黄色页面均可直接生成，黄色页面建议快速检查。",
                AiBatchUiStatus.ReviewRequired when pages.Any(page =>
                    page.Status == AiPageUiStatus.BusinessError)
                    => "分析完成；绿色和黄色页面可直接生成，红色页面已隔离并需单独处理。",
                AiBatchUiStatus.ReviewRequired => "分析完成，部分页面需要人工处理后才能生成。",
                AiBatchUiStatus.BudgetStopped => "本轮 AI 预算已达到停止线；请降低范围或等待人工批准后继续。",
                AiBatchUiStatus.NotGeneratable => "没有可生成页面，请先处理标红页面。",
                AiBatchUiStatus.Cancelled => "批次已取消；源图片和模板均未修改。",
                AiBatchUiStatus.RecoveryRequired when !context.AdmissionFailures.IsEmpty
                    => $"部分款式未进入 AI 分析：单次目标批次请求最多 {AiAnalysisProductionPolicy.MaxTargetAssetsPerRequest} 张照片，或本地 admission 发生可行动错误；请按款式提示修复后重新扫描。",
                AiBatchUiStatus.RecoveryRequired when IsLocalAnalysisPersistenceFailure(context.SchedulerRestoreError)
                    => "本地分析状态未保存或不一致，未发送新的 AI 请求；请重新扫描并建立新批次。",
                AiBatchUiStatus.RecoveryRequired => context.SchedulerRestoreError is null
                    ? "部分分析结果未知或需要恢复。请查询状态后再继续，不会自动重发。"
                    : "上次分析未能安全恢复，请刷新状态或重新打开批次；不会自动重发。",
                AiBatchUiStatus.Failed when context.AdmissionError is not null
                    => "分析准备失败，未完成的款式未发送请求；请按页面提示修复后重新扫描。",
                AiBatchUiStatus.Failed => "分析失败。请按页面提示修复后重试。",
                AiBatchUiStatus.Analysing => "正在分析商品素材；完成后会逐页更新状态。",
                _ => "等待分析任务开始。"
            }
        };
        return new AiBatchAnalysisProjection(
            context.Batch.BatchId,
            status,
            message,
            status is AiBatchUiStatus.Ready
                || status == AiBatchUiStatus.ReviewRequired && !hasBlockedPages
                ? "进入生成"
                : oversizedRequest || responseReferenceMismatch
                    ? "重新建立批次"
                    : providerSubmissionFailure is not null
                        ? "重试分析"
                    : "继续处理",
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
        var styleResults = await LoadStyleResultsAsync(
            context,
            style.StyleId,
            cancellationToken).ConfigureAwait(false);
        var pages = new List<AiPageAnalysisProjection>();
        foreach (var page in context.TemplateRevision.Pages.Where(page =>
                     page.Enabled && context.Batch.TemplateSnapshot.Pages.Any(snapshot => snapshot.PageId == page.PageId)))
        {
            pages.Add(await BuildPageProjectionAsync(
                context,
                style,
                page,
                request,
                styleResults,
                cancellationToken).ConfigureAwait(false));
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
            AiStyleUiStatus.ReviewRequired when pages.Any(page =>
                page.Status == AiPageUiStatus.ReviewRequired
                && !page.RequiresManualConfiguration)
                && !pages.Any(page => page.RequiresManualConfiguration
                    || page.Status is AiPageUiStatus.BusinessError or AiPageUiStatus.SystemError)
                => "可生成 · 建议检查",
            AiStyleUiStatus.ReviewRequired => "部分页面需处理",
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
        IReadOnlyList<StyleResultInput> styleResults,
        CancellationToken cancellationToken)
    {
        var pageBindings = context.TemplateRevision.FrameBindings
            .Where(binding => string.Equals(binding.PageId, page.PageId, StringComparison.Ordinal))
            .OrderBy(binding => binding.FrameId, StringComparer.Ordinal)
            .ToArray();
        var pageName = page.Name;
        if (IsLocalAnalysisPersistenceFailure(context.SchedulerRestoreError))
        {
            return ResultFailureProjection(
                context,
                style,
                page,
                pageBindings,
                "本地状态未保存，需要恢复",
                "本地批次或分析状态未能与清单、数据库一致；未发送新的 AI 请求，也不是图片匹配失败。请重新扫描并建立新批次。",
                "本地状态未保存");
        }
        if (request is not null
            && context.AdmissionFailures.TryGetValue(request.RequestId, out var admissionFailure))
        {
            return ResultFailureProjection(
                context,
                style,
                page,
                pageBindings,
                "分析未提交",
                ToAdmissionFailureText(admissionFailure),
                "未提交分析");
        }
        if (request is null)
        {
            if (context.AdmissionError is not null)
            {
                return ResultFailureProjection(
                    context,
                    style,
                    page,
                    pageBindings,
                    "分析准备失败",
                    "本款未能进入分析队列，未发送 AI 请求；请重新扫描并查看错误提示。",
                    "分析未提交");
            }

            return new(context.Batch.BatchId, style.StyleId, page.PageId, pageName,
                AiPageUiStatus.Waiting, "待分析", "等待分析", [], [],
                Requirements: BuildRequirementSelections(
                    context, style, page, pageBindings, [], [],
                    AiRequirementSelectionUiStatus.Waiting, "等待分析"));
        }

        var firstLayerInput = styleResults.FirstOrDefault(input =>
            string.Equals(input.Request.RequestId, request.RequestId, StringComparison.Ordinal));
        var ledger = firstLayerInput?.Ledger;
        if (ledger is null && context.AdmissionError is not null)
        {
            return ResultFailureProjection(
                context,
                style,
                page,
                pageBindings,
                "分析准备失败",
                "本款未能进入分析队列，未发送 AI 请求；请重新扫描并查看错误提示。",
                "分析未提交");
        }
        if (firstLayerInput?.Failure == ResultReadFailure.InvalidReference)
        {
            return ResultFailureProjection(
                context,
                style,
                page,
                pageBindings,
                "结果引用异常，需要恢复",
                "分析记录未能对应到当前请求，已阻止选图和生成。",
                "结果引用异常");
        }
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
            var unknownSession = await _store.LoadSessionAsync(
                SessionId(request), cancellationToken).ConfigureAwait(false);
            _ = context.TerminalErrors.TryGetValue(request.RequestId, out var unknownCode)
                || context.RuntimeFailures.TryGetValue(request.RequestId, out unknownCode);
            var presentation = DescribeProviderUnknown(unknownCode);
            return new(context.Batch.BatchId, style.StyleId, page.PageId, pageName,
                AiPageUiStatus.SystemError,
                presentation.StatusText,
                presentation.ActionText,
                [],
                [presentation.WarningText],
                Requirements: BuildRequirementSelections(
                    context, style, page, pageBindings, [], unknownSession?.LockedSelections.Values ?? [],
                    AiRequirementSelectionUiStatus.Unavailable,
                    presentation.RequirementStatusText));
        }
        if (ledger.Status == "TerminalError")
        {
            var hasTerminalError = context.TerminalErrors.TryGetValue(
                    request.RequestId,
                    out var terminalError)
                || context.RuntimeFailures.TryGetValue(request.RequestId, out terminalError);
            if (hasTerminalError
                && string.Equals(terminalError, "AI_PROVIDER_REQUEST_TOO_LARGE", StringComparison.Ordinal))
            {
                return ResultFailureProjection(
                    context,
                    style,
                    page,
                    pageBindings,
                    "请求体过大，需要重新建立批次",
                    "最终 UTF-8 JSON 请求体超过 16 MiB；未发送 AI 请求。请减少本款照片后重新建立批次。",
                    "请求体过大",
                    "重新建立批次");
            }
            if (hasTerminalError
                && string.Equals(
                    terminalError,
                    "AI_PROVIDER_RESPONSE_REFERENCE_MISMATCH",
                    StringComparison.Ordinal))
            {
                return ResultFailureProjection(
                    context,
                    style,
                    page,
                    pageBindings,
                    "AI 返回引用不匹配，需要重新建立批次",
                    "AI 已返回，但结果中的请求、图片或图片框标识无法与本批次安全对应，已阻止选图和生成。请使用修复版本重新建立批次。",
                    "AI 返回引用不匹配",
                    "重新建立批次");
            }
            if (hasTerminalError && IsProviderSubmissionFailureCode(terminalError!))
            {
                return ResultFailureProjection(
                    context,
                    style,
                    page,
                    pageBindings,
                    "AI 请求未送达",
                    "完整请求体未上传，火山未受理。请检查网络后重试分析。",
                    "提交失败，可安全重试",
                    "重试分析");
            }
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

        if (firstLayerInput is null || firstLayerInput.Failure == ResultReadFailure.Missing)
        {
            return new(context.Batch.BatchId, style.StyleId, page.PageId, pageName,
                AiPageUiStatus.SystemError, "结果缺失，需要恢复", "查询状态", [], ["结果文件不可用，未启动生成。"],
                Requirements: BuildRequirementSelections(
                    context, style, page, pageBindings, [], [],
                    AiRequirementSelectionUiStatus.Unavailable, "结果缺失"));
        }

        if (firstLayerInput.Failure == ResultReadFailure.Unreadable)
        {
            return ResultFailureProjection(
                context,
                style,
                page,
                pageBindings,
                "结果文件不可用，需要恢复",
                "结果文件损坏或无法读取，未启动生成。",
                "结果文件不可用");
        }
        if (firstLayerInput.Failure == ResultReadFailure.InvalidReference)
        {
            return ResultFailureProjection(
                context,
                style,
                page,
                pageBindings,
                "结果引用异常，需要恢复",
                "分析结果未能对应到当前素材，已阻止选图和生成。",
                "结果引用异常");
        }

        var result = firstLayerInput.Result!;

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

        if (page.ContentType == TemplatePageContentType.DynamicSizeTable)
        {
            return BuildDynamicSizePageProjection(
                context,
                style,
                page,
                styleResults,
                lockedByRequirement);
        }

        var topMatchByRequirement = matches
            .GroupBy(match => match.RequirementId, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.OrderBy(match => match.Rank)
                    .ThenBy(match => match.AssetId, StringComparer.Ordinal)
                    .First(),
                StringComparer.Ordinal);
        var assessmentByRequirement = pageBindings.ToDictionary(
            binding => binding.FrameId,
            binding => AiTargetMatchPolicy.Assess(
                binding,
                matches.Where(match => string.Equals(
                        match.RequirementId,
                        binding.FrameId,
                        StringComparison.Ordinal))
                    .ToArray(),
                lockedByRequirement.ContainsKey(binding.FrameId)),
            StringComparer.Ordinal);
        if (assessmentByRequirement.Values.Any(assessment =>
                assessment == AiTargetRequirementAssessment.InvalidResult))
        {
            return ResultFailureProjection(
                context,
                style,
                page,
                pageBindings,
                "匹配结果无效，需要恢复",
                "AI 未按已确认规则返回可判定的匹配类别，已阻止选图和生成。",
                "匹配结果无效");
        }
        var selectedAssetIds = pageBindings.Select(binding =>
                lockedByRequirement.TryGetValue(binding.FrameId, out var locked)
                    ? locked.AssetId
                    : topMatchByRequirement.GetValueOrDefault(binding.FrameId)?.AssetId)
            .Where(assetId => assetId is not null)
            .Select(assetId => assetId!)
            .ToHashSet(StringComparer.Ordinal);
        var relevantRefinements = styleResults.Where(input =>
                string.Equals(input.Request.AnalysisKind, AiAnalysisKind.SelectedAssetGeometry, StringComparison.Ordinal)
                && input.Request.Assets.Any(asset => selectedAssetIds.Contains(asset.AssetId)))
            .ToArray();
        var unknownRefinement = relevantRefinements.FirstOrDefault(input =>
            input.Ledger?.Status == "AiResultUnknown");
        if (unknownRefinement is not null)
        {
            return ResultFailureProjection(
                context,
                style,
                page,
                pageBindings,
                "结果未知，需要恢复",
                "已选素材的主体分析结果未知，禁止自动重发。",
                "主体分析结果未知");
        }
        var terminalRefinement = relevantRefinements.FirstOrDefault(input =>
            input.Ledger?.Status == "TerminalError");
        if (terminalRefinement is not null)
        {
            return ResultFailureProjection(
                context,
                style,
                page,
                pageBindings,
                "分析失败，需要恢复",
                "已选素材的主体分析未能安全完成，请修复后重试。",
                "主体分析失败");
        }
        var relevantRefinementFailure = relevantRefinements.FirstOrDefault(input =>
            input.Failure != ResultReadFailure.None);
        if (relevantRefinementFailure is not null)
        {
            return ResultFailureProjection(
                context,
                style,
                page,
                pageBindings,
                relevantRefinementFailure.Failure == ResultReadFailure.InvalidReference
                    ? "结果引用异常，需要恢复"
                    : "结果文件不可用，需要恢复",
                "已选素材的主体分析结果不可用，未启动生成。",
                "主体分析结果不可用");
        }
        var hasActiveGeometryRefinement = relevantRefinements.Any(input =>
            input.Ledger?.Status is "Queued" or "RetryableError" or "Uploading" or "Analysing");

        var geometryByAsset = styleResults
            .Where(input => input.Result is not null
                && input.Request.AnalysisKind is AiAnalysisKind.ProductJointUnderstanding
                    or AiAnalysisKind.SelectedAssetGeometry)
            .SelectMany(input => input.Result!.SubjectGeometry)
            .Where(geometry => selectedAssetIds.Contains(geometry.AssetId))
            .GroupBy(geometry => geometry.AssetId, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.OrderByDescending(geometry => geometry.Confidence).First(),
                StringComparer.Ordinal);
        var selectedAssetsMissingGeometry = selectedAssetIds
            .Where(assetId => !geometryByAsset.ContainsKey(assetId))
            .ToHashSet(StringComparer.Ordinal);
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
                // The semantic category is the useful user-facing identity of
                // a frame.  Do not expose an arbitrary ordinal such as
                // “图片框 1”; it does not tell the user which template slot
                // is being discussed and becomes misleading after template
                // edits or reordering.
                bindingIndex >= 0
                    ? ToUserSelectionRequirement(binding!.SelectionRequirement.Description)
                    : "当前图片",
                asset is null ? null : ResolvePreviewPath(context.Batch.InputRoot, asset.SourceRelativePath),
                ToUserSelectionRequirement(
                    binding?.SelectionRequirement.Description ?? "商品主体清晰完整，构图适合当前图片框。"),
                pageName);
        }).ToArray();
        var relevantResults = styleResults.Where(input => input.Result is not null
            && (input.Request.AnalysisKind == AiAnalysisKind.ProductJointUnderstanding
                || input.Request.AnalysisKind == AiAnalysisKind.SelectedAssetGeometry
                && input.Request.Assets.Any(asset => selectedAssetIds.Contains(asset.AssetId))))
            .ToArray();
        var hasVariantConflict = relevantResults.SelectMany(input => input.Result!.Warnings)
            .Any(warning => string.Equals(warning.Code, "variant_conflict", StringComparison.Ordinal))
            && matches.Length > 0
            && pageBindings.Any(binding => !lockedByRequirement.ContainsKey(binding.FrameId));
        var hasUnconfirmedReview = assessmentByRequirement.Values.Any(assessment =>
            assessment == AiTargetRequirementAssessment.ReviewRequired);
        var hasUnsafeOrNoSafeCandidate = assessmentByRequirement.Values.Any(assessment =>
            assessment == AiTargetRequirementAssessment.BusinessError);
        var warnings = relevantResults
            .SelectMany(input => input.Result!.Warnings)
            .Where(warning => IsNormalPageWarningRelevant(
                warning,
                matches.Length > 0,
                hasMissingRequiredMatch,
                hasVariantConflict))
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
        if (hasUnconfirmedReview)
        {
            var hasMaterialDifference = pageBindings.Any(binding =>
                assessmentByRequirement.GetValueOrDefault(binding.FrameId)
                    == AiTargetRequirementAssessment.ReviewRequired
                && matches
                    .Where(match => string.Equals(
                        match.RequirementId,
                        binding.FrameId,
                        StringComparison.Ordinal))
                    .OrderBy(match => match.Rank)
                    .ThenBy(match => match.AssetId, StringComparer.Ordinal)
                    .FirstOrDefault() is { } top
                && top.EvidenceCodes.Contains(
                    AiTargetMatchEvidence.MaterialDifference,
                    StringComparer.Ordinal));
            warnings = warnings.Append(
                    hasMaterialDifference
                        ? "首选素材与当前图片类别存在实质差异，请检查或替换用图。"
                        : "首选素材未达到绿色置信度门槛，请快速检查，需要时再替换用图。")
                .Distinct(StringComparer.Ordinal).ToArray();
        }
        if (hasUnsafeOrNoSafeCandidate && !hasMissingRequiredMatch)
        {
            warnings = warnings.Append("没有可自动采用的安全候选，请补充素材或人工修正。")
                .Distinct(StringComparer.Ordinal).ToArray();
        }
        if (selectedAssetsMissingGeometry.Count > 0)
        {
            warnings = warnings.Append("部分已选素材未返回主体边界；本页按已校准布局标黄，仍可生成，建议检查构图。")
                .Distinct(StringComparer.Ordinal).ToArray();
        }
        if (hasActiveGeometryRefinement)
        {
            warnings = warnings.Append("已选素材正在进行主体边界分析，完成后将自动更新此页。")
                .Distinct(StringComparer.Ordinal).ToArray();
        }
        var businessWarning = hasMissingRequiredMatch
            || hasVariantConflict
            || hasUnsafeOrNoSafeCandidate;
        // Yellow review and a missing optional subject boundary are advisory.
        // Only an actual business error, an active refinement request, or the
        // explicit manual-selection mode keeps a page out of the queue.
        var requiresManualConfiguration = manualNeedsLock
            || hasMissingRequiredMatch
            || hasVariantConflict
            || hasUnsafeOrNoSafeCandidate
            || hasActiveGeometryRefinement;
        var status = businessWarning
            ? AiPageUiStatus.BusinessError
            : hasActiveGeometryRefinement
                ? AiPageUiStatus.Analysing
                : manualNeedsLock || hasUnconfirmedReview || selectedAssetsMissingGeometry.Count > 0
                ? AiPageUiStatus.ReviewRequired
                : AiPageUiStatus.Ready;
        var statusText = status switch
        {
            AiPageUiStatus.Ready => "可生成",
            AiPageUiStatus.Analysing => "主体分析中",
            AiPageUiStatus.ReviewRequired when !requiresManualConfiguration => "可生成 · 建议检查",
            AiPageUiStatus.ReviewRequired => "待人工处理",
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
            status switch
            {
                AiPageUiStatus.Ready => "已通过",
                AiPageUiStatus.Analysing => "分析中",
                AiPageUiStatus.ReviewRequired when !requiresManualConfiguration => "可生成 · 建议检查",
                _ => "处理此页"
            },
            candidates,
            warnings,
            requiresManualConfiguration,
            BuildRequirementSelections(
                context,
                style,
                page,
                pageBindings,
                matches,
                lockedByRequirement.Values,
                AiRequirementSelectionUiStatus.Missing,
                "待人工选择",
                assessmentByRequirement));
    }

    private async Task<StyleResultInput[]> LoadStyleResultsAsync(
        BatchContext context,
        string styleId,
        CancellationToken cancellationToken)
    {
        var inputs = new List<StyleResultInput>();
        var styleRequests = context.Requests.Values
            .Where(request => string.Equals(request.StyleId, styleId, StringComparison.Ordinal))
            .OrderBy(request => request.RequestId, StringComparer.Ordinal)
            .ToArray();
        var firstLayerRequest = styleRequests.FirstOrDefault(request =>
            string.Equals(
                request.AnalysisKind,
                AiAnalysisKind.ProductJointUnderstanding,
                StringComparison.Ordinal));
        foreach (var request in styleRequests)
        {
            var expectedFingerprint = AiAnalysisCacheKeyFactory.Create(request);
            var ledger = await _store.FindRequestByFingerprintAsync(
                expectedFingerprint,
                cancellationToken).ConfigureAwait(false);
            if (ledger is not null
                && (!string.Equals(ledger.RequestId, request.RequestId, StringComparison.Ordinal)
                    || !string.Equals(ledger.Fingerprint, expectedFingerprint, StringComparison.Ordinal)
                    || !string.Equals(ledger.BatchId, request.BatchId, StringComparison.Ordinal)
                    || !string.Equals(ledger.StyleId, request.StyleId, StringComparison.Ordinal)))
            {
                inputs.Add(new(request, ledger, null, ResultReadFailure.InvalidReference));
                continue;
            }

            if (request.AnalysisKind is AiAnalysisKind.SelectedAssetGeometry
                    or AiAnalysisKind.SizeTableExtraction
                && (firstLayerRequest is null
                    || !IsRefinementClosedOverParent(firstLayerRequest, request)))
            {
                inputs.Add(new(request, ledger, null, ResultReadFailure.InvalidReference));
                continue;
            }

            if (ledger is null
                || ledger.Status is not ("Validated" or "ManualReview" or "PlanClosed"))
            {
                inputs.Add(new(request, ledger, null, ResultReadFailure.None));
                continue;
            }

            if (ledger.ResultReference is null)
            {
                inputs.Add(new(request, ledger, null, ResultReadFailure.Missing));
                continue;
            }

            AiAnalysisResultV1? result;
            try
            {
                // Successful ledgers can outlive a removed, malformed, locked,
                // or ACL-inaccessible result file. Keep those local storage
                // faults as data for the page projection; cancellation must
                // still propagate to the caller.
                result = await _results.LoadAsync(
                    ledger.ResultReference,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is
                InvalidDataException or IOException or UnauthorizedAccessException)
            {
                inputs.Add(new(request, ledger, null, ResultReadFailure.Unreadable));
                continue;
            }

            if (result is null)
            {
                inputs.Add(new(request, ledger, null, ResultReadFailure.Missing));
                continue;
            }

            if (!string.Equals(result.RequestId, request.RequestId, StringComparison.Ordinal)
                || !string.Equals(
                    result.ModelConfigVersion,
                    request.ModelConfigVersion,
                    StringComparison.Ordinal))
            {
                inputs.Add(new(request, ledger, null, ResultReadFailure.InvalidReference));
                continue;
            }

            try
            {
                // Compatibility repair for already measured v1 results that
                // predate transport-alias closure. This is in-memory only: it
                // never rewrites the durable result or resends the request.
                var normalized = AiAnalysisResultReferenceNormalizer.Normalize(request, result);
                inputs.Add(new(request, ledger, normalized, ResultReadFailure.None));
            }
            catch (InvalidDataException exception) when (
                AiAnalysisResultSemanticValidator.IsSemanticError(exception))
            {
                inputs.Add(new(request, ledger, null, ResultReadFailure.Unreadable));
            }
            catch (InvalidDataException)
            {
                inputs.Add(new(request, ledger, null, ResultReadFailure.InvalidReference));
            }
        }

        return inputs.ToArray();
    }

    private static bool IsRefinementClosedOverParent(
        AiAnalysisRequestV1 parent,
        AiAnalysisRequestV1 child)
    {
        var refinementKind = child.AnalysisKind switch
        {
            AiAnalysisKind.SelectedAssetGeometry => "geometry",
            AiAnalysisKind.SizeTableExtraction => "size",
            _ => null
        };
        if (refinementKind is null
            || child.Assets.Count == 0
            || !string.Equals(
                child.RequestId,
                ExpectedRefinementRequestId(parent.RequestId, refinementKind, child.Assets.Select(asset => asset.AssetId)),
                StringComparison.Ordinal)
            || !string.Equals(child.SchemaVersion, parent.SchemaVersion, StringComparison.Ordinal)
            || !string.Equals(child.DocumentKind, parent.DocumentKind, StringComparison.Ordinal)
            || !string.Equals(child.CorrelationId, parent.CorrelationId, StringComparison.Ordinal)
            || !string.Equals(child.ProductId, parent.ProductId, StringComparison.Ordinal)
            || !string.Equals(child.BatchId, parent.BatchId, StringComparison.Ordinal)
            || !string.Equals(child.StyleId, parent.StyleId, StringComparison.Ordinal)
            || !string.Equals(child.TemplateSnapshotId, parent.TemplateSnapshotId, StringComparison.Ordinal)
            || !string.Equals(child.TemplateRevisionId, parent.TemplateRevisionId, StringComparison.Ordinal)
            || !string.Equals(child.ModelConfigVersion, parent.ModelConfigVersion, StringComparison.Ordinal)
            || !string.Equals(child.PromptContractVersion, parent.PromptContractVersion, StringComparison.Ordinal)
            || !string.Equals(child.ResponseSchemaVersion, parent.ResponseSchemaVersion, StringComparison.Ordinal)
            || !string.Equals(child.CompressionProfileVersion, parent.CompressionProfileVersion, StringComparison.Ordinal)
            || !string.Equals(child.ConsentId, parent.ConsentId, StringComparison.Ordinal)
            || !string.Equals(child.RequirementsDigest, parent.RequirementsDigest, StringComparison.Ordinal)
            || !child.Requirements.SequenceEqual(parent.Requirements))
        {
            return false;
        }

        var parentAssets = parent.Assets.ToDictionary(asset => asset.AssetId, StringComparer.Ordinal);
        return child.Assets.Select(asset => asset.AssetId).Distinct(StringComparer.Ordinal).Count()
                == child.Assets.Count
            && child.Assets.All(asset => parentAssets.TryGetValue(asset.AssetId, out var expected)
                && asset == expected);
    }

    private static string ExpectedRefinementRequestId(
        string parentRequestId,
        string kind,
        IEnumerable<string> assetIds)
    {
        var assetSet = string.Join("\n", assetIds.Order(StringComparer.Ordinal));
        var digest = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes($"{parentRequestId}|{kind}|{assetSet}")))
            .ToLowerInvariant()[..16];
        var tail = $"-{kind}-{digest}";
        var prefixLength = Math.Min(parentRequestId.Length, 128 - tail.Length);
        return parentRequestId[..prefixLength] + tail;
    }

    private static AiPageAnalysisProjection BuildDynamicSizePageProjection(
        BatchContext context,
        StyleDraft style,
        TemplatePage page,
        IReadOnlyList<StyleResultInput> styleResults,
        IReadOnlyDictionary<string, AiLockedSelection> lockedByRequirement)
    {
        var pageBindings = context.TemplateRevision.FrameBindings
            .Where(binding => string.Equals(binding.PageId, page.PageId, StringComparison.Ordinal))
            .OrderBy(binding => binding.FrameId, StringComparer.Ordinal)
            .ToArray();
        var sizeRefinements = styleResults.Where(input =>
                string.Equals(input.Request.AnalysisKind, AiAnalysisKind.SizeTableExtraction, StringComparison.Ordinal))
            .ToArray();
        var unknownSizeResult = sizeRefinements.FirstOrDefault(input =>
            input.Ledger?.Status == "AiResultUnknown");
        if (unknownSizeResult is not null)
        {
            return ResultFailureProjection(
                context,
                style,
                page,
                pageBindings,
                "结果未知，需要恢复",
                "尺码来源分析结果未知，禁止自动重发。",
                "尺码分析结果未知");
        }
        var terminalSizeResult = sizeRefinements.FirstOrDefault(input =>
            input.Ledger?.Status == "TerminalError");
        if (terminalSizeResult is not null)
        {
            return ResultFailureProjection(
                context,
                style,
                page,
                pageBindings,
                "分析失败，需要恢复",
                "尺码来源分析未能安全完成，请修复后重试。",
                "尺码分析失败");
        }
        var failedSizeResult = sizeRefinements.FirstOrDefault(input =>
            input.Failure != ResultReadFailure.None);
        if (failedSizeResult is not null)
        {
            return ResultFailureProjection(
                context,
                style,
                page,
                pageBindings,
                failedSizeResult.Failure == ResultReadFailure.InvalidReference
                    ? "结果引用异常，需要恢复"
                    : "结果文件不可用，需要恢复",
                "尺码来源分析结果不可用，未启动生成。",
                "尺码分析结果不可用");
        }
        var hasActiveSizeRefinement = sizeRefinements.Any(input =>
            input.Ledger?.Status is "Queued" or "RetryableError" or "Uploading" or "Analysing");

        var styleAssetIds = style.AssetIds.ToHashSet(StringComparer.Ordinal);
        var relevantResults = styleResults.Where(input => input.Result is not null
            && input.Request.AnalysisKind is AiAnalysisKind.ProductJointUnderstanding
                or AiAnalysisKind.SizeTableExtraction)
            .ToArray();
        var sizeCandidates = relevantResults
            .Select(input => input.Result!.SizeTableCandidate)
            .Where(candidate => candidate is not null && styleAssetIds.Contains(candidate.SourceAssetId))
            .Select(candidate => candidate!)
            .GroupBy(candidate => JsonSerializer.Serialize(new
            {
                candidate.SourceAssetId,
                candidate.Columns,
                candidate.Rows
            }), StringComparer.Ordinal)
            .Select(group => group.OrderByDescending(candidate => candidate.Confidence).First())
            .ToArray();
        var hasProviderConflict = relevantResults
            .SelectMany(input => input.Result!.Warnings)
            .Any(warning => string.Equals(warning.Code, "size_source_conflict", StringComparison.Ordinal));
        var warnings = relevantResults
            .SelectMany(input => input.Result!.Warnings)
            .Where(warning => warning.Scope == "size_table"
                || warning.Code == "size_source_conflict")
            .Select(warning => ToWarningText(warning.Code))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        AiPageUiStatus status;
        string statusText;
        string actionText;
        if (hasActiveSizeRefinement)
        {
            status = AiPageUiStatus.Analysing;
            statusText = "尺码分析中";
            actionText = "分析中";
            warnings = warnings.Append("正在确认尺码表来源与结构，完成后将自动更新此页。")
                .Distinct(StringComparer.Ordinal).ToArray();
        }
        else if (sizeCandidates.Length == 0)
        {
            status = AiPageUiStatus.BusinessError;
            statusText = "需要修复";
            actionText = "确认尺码来源";
            warnings = warnings.Append("未找到可可靠提取的尺码表来源，此页不会进入生成。")
                .Distinct(StringComparer.Ordinal).ToArray();
        }
        else if (hasProviderConflict || sizeCandidates.Length > 1)
        {
            status = AiPageUiStatus.BusinessError;
            statusText = "需要修复";
            actionText = "确认尺码来源";
            warnings = warnings.Append("存在多个内容不同的尺码表来源，请先选择唯一来源。")
                .Distinct(StringComparer.Ordinal).ToArray();
        }
        else if (sizeCandidates[0].Confidence < 0.8m)
        {
            status = AiPageUiStatus.BusinessError;
            statusText = "需要修复";
            actionText = "检查尺码数据";
            warnings = warnings.Append("尺码表结构无法可靠解析，禁止猜测后生成。")
                .Distinct(StringComparer.Ordinal).ToArray();
        }
        else
        {
            // AI-01 closes the structured candidate and provenance only.
            // SIZE-01 owns deterministic rasterization and is intentionally
            // not impersonated as a generatable page here.
            status = AiPageUiStatus.ReviewRequired;
            statusText = "等待尺码页生成能力";
            actionText = "等待 SIZE-01";
            warnings = warnings.Append("尺码数据与唯一来源已确认，等待 SIZE-01 确定性绘制。")
                .Distinct(StringComparer.Ordinal).ToArray();
        }

        return new AiPageAnalysisProjection(
            context.Batch.BatchId,
            style.StyleId,
            page.PageId,
            page.Name,
            status,
            statusText,
            actionText,
            [],
            warnings,
            RequiresManualConfiguration: true,
            Requirements: BuildRequirementSelections(
                context,
                style,
                page,
                pageBindings,
                [],
                lockedByRequirement.Values,
                AiRequirementSelectionUiStatus.Missing,
                "待确认尺码来源"));
    }

    private static AiPageAnalysisProjection ResultFailureProjection(
        BatchContext context,
        StyleDraft style,
        TemplatePage page,
        IReadOnlyList<FrameBinding> pageBindings,
        string statusText,
        string warningText,
        string requirementStatusText,
        string actionText = "查询状态") => new(
            context.Batch.BatchId,
            style.StyleId,
            page.PageId,
            page.Name,
            AiPageUiStatus.SystemError,
            statusText,
            actionText,
            [],
            [warningText],
            Requirements: BuildRequirementSelections(
                context,
                style,
                page,
                pageBindings,
                [],
                [],
                AiRequirementSelectionUiStatus.Unavailable,
                requirementStatusText));

    private sealed record ProviderUnknownPresentation(
        string StatusText,
        string WarningText,
        string RequirementStatusText,
        string ActionText,
        string BatchMessage);

    private static bool IsProviderSubmissionFailureCode(string code) => code is
        "AI_PROVIDER_SUBMISSION_TIMEOUT"
        or "AI_PROVIDER_SUBMISSION_NETWORK_FAILURE";

    private static bool IsSpecificProviderUnknownCode(string code) => code is
        "AI_PROVIDER_RESPONSE_HEADERS_TIMEOUT"
        or "AI_PROVIDER_RESPONSE_HEADERS_NETWORK_FAILURE"
        or "AI_PROVIDER_RESPONSE_BODY_TIMEOUT"
        or "AI_PROVIDER_RESPONSE_BODY_NETWORK_FAILURE";

    private static ProviderUnknownPresentation DescribeProviderUnknown(string? code) => code switch
    {
        "AI_PROVIDER_RESPONSE_HEADERS_TIMEOUT" or "AI_PROVIDER_RESPONSE_HEADERS_NETWORK_FAILURE" => new(
            "已上传，未收到响应",
            "请求体已写入传输通道，但应用没有收到供应商响应头；是否被火山接收仍未知，已记录脱敏阶段日志且不会自动重发。",
            "未收到响应，可人工选图",
            "核对账单或人工选图",
            "AI 请求体已写入传输通道，但没有收到供应商响应头；是否被火山接收仍未知。请核对账单；系统不会自动重发。"),
        "AI_PROVIDER_RESPONSE_BODY_TIMEOUT" or "AI_PROVIDER_RESPONSE_BODY_NETWORK_FAILURE" => new(
            "响应读取失败，结果未知",
            "应用已收到供应商响应头，但响应体未完整读回；处理结果未知，已记录脱敏阶段日志且不会自动重发。",
            "响应未读完，可人工选图",
            "人工选图",
            "供应商已经返回响应头，但响应体未完整读回；处理结果未知，系统不会自动重发。"),
        _ => new(
            "结果未知，需要恢复",
            "供应商处理结果未知，禁止自动重发。",
            "结果未知，禁止重发",
            "查询状态",
            "供应商处理结果未知；系统不会自动重发，请查询状态或人工处理。")
    };

    private static bool IsNormalPageWarningRelevant(
        AiWarningV1 warning,
        bool hasMatches,
        bool hasMissingRequiredMatch,
        bool hasVariantConflict)
    {
        if (warning.Code == "low_confidence"
            || warning.Code == "size_source_conflict"
            || warning.Scope == "size_table")
        {
            return false;
        }

        return warning.Code switch
        {
            "asset_missing" => hasMissingRequiredMatch,
            "variant_conflict" => hasVariantConflict,
            _ => warning.Scope is "request" or "product"
                || hasMatches
                || hasMissingRequiredMatch
        };
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
        var hasExecutablePage = pages.Any(page => page.CanGenerate);
        var hasBlockingConfiguration = pages.Any(page =>
            page.Status == AiPageUiStatus.ReviewRequired
            && page.RequiresManualConfiguration);
        // A hard budget stop is an actionable batch state only while work is
        // still unresolved. If all pages have settled, preserve the terminal
        // business result (ready, review, or no-output) instead of hiding it
        // behind a historical budget boundary.
        if (budgetStopped && !allSettled)
        {
            return AiBatchUiStatus.BudgetStopped;
        }
        if (allSettled && !hasExecutablePage && !hasBlockingConfiguration)
        {
            return AiBatchUiStatus.NotGeneratable;
        }
        if (styles.Count > 0 && styles.All(style => style.Status == AiStyleUiStatus.Ready))
        {
            return AiBatchUiStatus.Ready;
        }
        if (manualSelection && hasBlockingConfiguration)
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
        string emptyStatusText,
        IReadOnlyDictionary<string, AiTargetRequirementAssessment>? assessments = null)
    {
        var assetsById = context.Batch.Assets
            .Where(asset => string.Equals(asset.StyleId, style.StyleId, StringComparison.Ordinal))
            .ToDictionary(asset => asset.AssetId, StringComparer.Ordinal);
        var assetDisplayNames = style.AssetIds
            .Select((assetId, index) => (assetId, displayName: $"照片 {index + 1:D2}"))
            .ToDictionary(item => item.assetId, item => item.displayName, StringComparer.Ordinal);
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
                : recommended is null
                    ? emptyStatus
                    : assessments?.GetValueOrDefault(binding.FrameId) switch
                    {
                        AiTargetRequirementAssessment.ReviewRequired
                            => AiRequirementSelectionUiStatus.ReviewRequired,
                        AiTargetRequirementAssessment.BusinessError
                            => AiRequirementSelectionUiStatus.Unsafe,
                        AiTargetRequirementAssessment.InvalidResult
                            => AiRequirementSelectionUiStatus.Unavailable,
                        _ => AiRequirementSelectionUiStatus.AiSelected
                    };
            var statusText = status switch
            {
                AiRequirementSelectionUiStatus.ManualLocked => "人工选用 · 已锁定",
                AiRequirementSelectionUiStatus.AiSelected => "AI 推荐 · 已确认",
                AiRequirementSelectionUiStatus.ReviewRequired => "AI 推荐 · 可生成，建议检查",
                AiRequirementSelectionUiStatus.Unsafe => "AI 推荐 · 需处理",
                _ => emptyStatusText
            };
            return new AiRequirementSelectionProjection(
                context.Batch.BatchId,
                style.StyleId,
                page.PageId,
                page.Name,
                binding.FrameId,
                ToUserSelectionRequirement(binding.SelectionRequirement.Description),
                ToUserSelectionRequirement(binding.SelectionRequirement.Description),
                status,
                statusText,
                selectedAssetId,
                selectedAsset is null
                    ? null
                    : ResolvePreviewPath(context.Batch.InputRoot, selectedAsset.SourceRelativePath),
                selectedAsset is null
                    ? null
                    : assetDisplayNames.GetValueOrDefault(selectedAsset.AssetId));
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

    private static string ToAdmissionFailureText(string code) => code switch
    {
        "AI_ANALYSIS_ASSET_LIMIT_EXCEEDED"
            => $"本款照片数量超过单次目标批次分析上限 {AiAnalysisProductionPolicy.MaxTargetAssetsPerRequest} 张；未创建 provider 请求。请减少照片后重新建立批次。",
        "AI_ANALYSIS_REQUEST_LIMIT_EXCEEDED"
            => "本款逻辑分析请求已达到上限；未继续提交新的 provider 请求。",
        "AI_ANALYSIS_REQUEST_ID_CONFLICT"
            => "本款分析请求标识与已有记录冲突；已安全阻断，不能直接重发。",
        "AI_PROVIDER_REQUEST_TOO_LARGE"
            => "最终 JSON 请求体超过 16 MiB；未发送 AI 请求。请减少本款照片后重新建立批次。",
        "AI_ANALYSIS_ADMISSION_CANCELLED"
            => "本款尚未完成本地准备，已取消提交；源文件未修改。",
        _ => "本款未能安全进入 AI 分析队列；未提交 provider 请求，请重新扫描。"
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

    private async Task EnsureAnalysisStateRecoveredAsync(
        BatchContext context,
        CancellationToken cancellationToken)
    {
        if (_checkpointPort is null)
        {
            return;
        }

        await context.CheckpointGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (context.AnalysisStateRecoveryAttempted)
            {
                return;
            }

            context.AnalysisStateRecoveryAttempted = true;
            try
            {
                var result = await _checkpointPort.RecoverAsync(
                    context.Batch.BatchId,
                    cancellationToken).ConfigureAwait(false);
                if (result.Outcome == AiAnalysisCheckpointOutcome.RecoveryRequired)
                {
                    context.SchedulerRestoreError = result.ReasonCode;
                }
            }
            catch (InvalidOperationException exception)
            {
                context.SchedulerRestoreError = exception.Message;
            }
        }
        finally
        {
            context.CheckpointGate.Release();
        }
    }

    private static void ThrowIfAnalysisStateUnavailable(BatchContext context)
    {
        if (!string.IsNullOrWhiteSpace(context.SchedulerRestoreError))
        {
            throw new InvalidOperationException(context.SchedulerRestoreError);
        }
    }

    private static bool IsLocalAnalysisPersistenceFailure(string? code) =>
        !string.IsNullOrWhiteSpace(code)
        && (code.StartsWith("AI_CHECKPOINT_", StringComparison.Ordinal)
            || code.StartsWith("AI_SCHEDULER_STATE_", StringComparison.Ordinal)
            || code is "AI_SCHEDULER_PENDING_LEDGER_MISSING"
                or "AI_SCHEDULER_PENDING_SESSION_MISSING");

    private static AiAnalysisRequestV1 GetFirstLayerRequest(BatchContext context, string styleId) =>
        context.Requests.Values.FirstOrDefault(request =>
            request.StyleId == styleId && request.AnalysisKind == AiAnalysisKind.ProductJointUnderstanding)
        ?? throw new InvalidOperationException("AI_REQUEST_NOT_FOUND");

    private static string SessionId(AiAnalysisRequestV1 request) => $"session-{request.RequestId}";

    private enum ResultReadFailure
    {
        None = 0,
        Missing,
        Unreadable,
        InvalidReference
    }

    private sealed record StyleResultInput(
        AiAnalysisRequestV1 Request,
        AiRequestLedgerEntry? Ledger,
        AiAnalysisResultV1? Result,
        ResultReadFailure Failure);

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
        public ConcurrentDictionary<string, string> AdmissionFailures { get; } = new(StringComparer.Ordinal);
        public ConcurrentDictionary<string, string> TerminalErrors { get; } = new(StringComparer.Ordinal);
        public ConcurrentDictionary<string, string> RuntimeFailures { get; } = new(StringComparer.Ordinal);
        public SemaphoreSlim RestoreGate { get; } = new(1, 1);
        public SemaphoreSlim CheckpointGate { get; } = new(1, 1);
        public CancellationTokenSource AdmissionCancellation { get; } = new();
        public object BackgroundGate { get; } = new();
        public Task? BackgroundTask { get; set; }
        public Task? AdmissionTask { get; set; }
        public bool SchedulerRestoreAttempted { get; set; }
        public bool AnalysisStateRecoveryAttempted { get; set; }
        public string? SchedulerRestoreError { get; set; }
        public bool Started { get; set; }
        public string? AdmissionError { get; set; }
        private int _admissionStarted;
        private int _admissionCompleted = 1;

        public bool AdmissionCompleted => Volatile.Read(ref _admissionCompleted) != 0;

        public bool TryStartAdmission()
        {
            if (Interlocked.CompareExchange(ref _admissionStarted, 1, 0) != 0)
            {
                return false;
            }

            Volatile.Write(ref _admissionCompleted, 0);
            return true;
        }

        public void MarkAdmissionCompleted() => Volatile.Write(ref _admissionCompleted, 1);

        public void StopAdmission() => AdmissionCancellation.Cancel();

        public void Dispose()
        {
            AdmissionCancellation.Cancel();
            Scheduler.Dispose();
            RestoreGate.Dispose();
            CheckpointGate.Dispose();
            AdmissionCancellation.Dispose();
        }
    }
}
