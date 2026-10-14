using Desktop.Contracts.Ai;
using Desktop.Domain.Ai;
using Desktop.Domain.Inputs;
using Desktop.Domain.Templates;
using System.Text.Json;
using System.Collections.Immutable;

namespace Desktop.Application.Ai;

/// <summary>Projects batch facts and durable results without scheduling or writing state.</summary>
public sealed class AiBatchProjectionService
{
    private readonly IAiAnalysisResultReader _reader;

    public AiBatchProjectionService(IAiAnalysisResultReader reader)
    {
        _reader = reader ?? throw new ArgumentNullException(nameof(reader));
    }

    public async Task<AiBatchAnalysisProjection> BuildAsync(
        AiBatchProjectionSnapshot context,
        CancellationToken cancellationToken,
        Func<ImmutableDictionary<string, string>>? captureTerminalErrors = null)
    {
        var styleInputs = new Dictionary<string, AiStyleAnalysisResultInput[]>(StringComparer.Ordinal);
        foreach (var style in context.Batch.Styles.OrderBy(style => style.Order))
            styleInputs.Add(style.StyleId, await _reader.LoadStyleResultsAsync(
                context.Requests, style.StyleId, cancellationToken).ConfigureAwait(false));
        if (captureTerminalErrors is not null)
        {
            var terminalRequests = styleInputs.Values.SelectMany(inputs => inputs)
                .Where(input => AiLedgerStatusPolicy.IsTerminalFailure(input.Ledger?.Status))
                .Select(input => input.Request.RequestId).ToHashSet(StringComparer.Ordinal);
            context = context with { TerminalErrors = captureTerminalErrors()
                .Where(pair => terminalRequests.Contains(pair.Key)).ToImmutableDictionary(StringComparer.Ordinal) };
        }
        var styles = new List<AiStyleAnalysisProjection>(context.Batch.Styles.Length);
        foreach (var style in context.Batch.Styles.OrderBy(style => style.Order))
        {
            var request = context.Requests.FirstOrDefault(candidate =>
                candidate.StyleId == style.StyleId
                && candidate.AnalysisKind == AiAnalysisKind.ProductJointUnderstanding);
            styles.Add(await BuildStyleProjectionAsync(context, style, request, styleInputs[style.StyleId], cancellationToken).ConfigureAwait(false));
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
        var controlState = context.ControlState;
        var status = context.SchedulerRestoreError is not null
            ? AiBatchUiStatus.RecoveryRequired
            : AiBatchStatusPolicy.Determine(
                styles,
                controlState,
                context.Batch.SelectionMode == BatchSelectionMode.Manual,
                context.BudgetHardStopped);
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
            AiAnalysisSchedulingControlState.Paused when context.ControlReason ==
                "AI_UPLOAD_CONSENT_UNAVAILABLE"
                => "无法读取本地上传授权状态；已安全暂停且未发送请求。修复本地存储后可恢复分析。",
            AiAnalysisSchedulingControlState.Paused when context.ControlReason is
                "AI_PROVIDER_CREDENTIAL_MISSING"
                or "AI_PROVIDER_CREDENTIAL_UNAVAILABLE"
                or "AI_PROVIDER_CREDENTIAL_INVALID"
                => "本机 AI 凭据尚未就绪；请求未发送。修复凭据后点击恢复即可继续当前失败范围。",
            AiAnalysisSchedulingControlState.Paused when context.ControlReason is
                "AI_UPLOAD_CONSENT_REQUIRED" or "AI_UPLOAD_CONSENT_REVOKED"
                => "上传授权未完成或已撤回；请在设置中重新授权后恢复分析。",
            AiAnalysisSchedulingControlState.Paused => "分析已暂停；点击恢复后从安全检查点继续。",
            AiAnalysisSchedulingControlState.WaitingForNetwork => context.ControlReason
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
            context.ControlReason,
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
        AiBatchProjectionSnapshot context,
        StyleDraft style,
        AiAnalysisRequestV1? request,
        IReadOnlyList<AiStyleAnalysisResultInput> styleResults,
        CancellationToken cancellationToken)
    {
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
        var status = AiProjectionStatusPolicy.DetermineStyleStatus(pages);
        var statusText = AiProjectionStatusPolicy.StyleStatusText(status, pages);
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
        AiBatchProjectionSnapshot context,
        StyleDraft style,
        TemplatePage page,
        AiAnalysisRequestV1? request,
        IReadOnlyList<AiStyleAnalysisResultInput> styleResults,
        CancellationToken cancellationToken)
    {
        var pageBindings = context.TemplateRevision.ActiveFrameBindings
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
        var ledgerStatus = ProjectionStatus(context, firstLayerInput);
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
        if (firstLayerInput?.Failure == AiAnalysisResultReadFailure.InvalidReference)
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
        if (ledger is null || AiLedgerStatusPolicy.IsRunnable(ledgerStatus))
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
        if (AiLedgerStatusPolicy.IsInFlight(ledgerStatus))
        {
            return new(context.Batch.BatchId, style.StyleId, page.PageId, pageName,
                AiPageUiStatus.Analysing, "分析中", "分析中", [], [],
                Requirements: BuildRequirementSelections(
                    context, style, page, pageBindings, [], [],
                    AiRequirementSelectionUiStatus.Analysing, "分析中"));
        }
        if (ledgerStatus == AiLedgerStatusPolicy.Cancelled)
        {
            return new(context.Batch.BatchId, style.StyleId, page.PageId, pageName,
                AiPageUiStatus.Cancelled, "已取消", "返回批次", [], [],
                Requirements: BuildRequirementSelections(
                    context, style, page, pageBindings, [], [],
                    AiRequirementSelectionUiStatus.Cancelled, "已取消"));
        }
        if (ledgerStatus == AiLedgerStatusPolicy.AiResultUnknown)
        {
            var unknownSession = await _reader.LoadSessionAsync(
                $"session-{request.RequestId}", cancellationToken).ConfigureAwait(false);
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
        if (ledgerStatus == AiLedgerStatusPolicy.TerminalError)
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

        if (firstLayerInput is null || firstLayerInput.Failure == AiAnalysisResultReadFailure.Missing)
        {
            return new(context.Batch.BatchId, style.StyleId, page.PageId, pageName,
                AiPageUiStatus.SystemError, "结果缺失，需要恢复", "查询状态", [], ["结果文件不可用，未启动生成。"],
                Requirements: BuildRequirementSelections(
                    context, style, page, pageBindings, [], [],
                    AiRequirementSelectionUiStatus.Unavailable, "结果缺失"));
        }

        if (firstLayerInput.Failure == AiAnalysisResultReadFailure.Unreadable)
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
        if (firstLayerInput.Failure == AiAnalysisResultReadFailure.InvalidReference)
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
        var session = await _reader.LoadSessionAsync($"session-{request.RequestId}", cancellationToken).ConfigureAwait(false);
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
            ProjectionStatus(context, input) == AiLedgerStatusPolicy.AiResultUnknown);
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
            input.Ledger?.Status == AiLedgerStatusPolicy.TerminalError);
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
            input.Failure != AiAnalysisResultReadFailure.None);
        if (relevantRefinementFailure is not null)
        {
            return ResultFailureProjection(
                context,
                style,
                page,
                pageBindings,
                relevantRefinementFailure.Failure == AiAnalysisResultReadFailure.InvalidReference
                    ? "结果引用异常，需要恢复"
                    : "结果文件不可用，需要恢复",
                "已选素材的主体分析结果不可用，未启动生成。",
                "主体分析结果不可用");
        }
        var hasActiveGeometryRefinement = relevantRefinements.Any(input =>
            AiLedgerStatusPolicy.IsRecoverableAdmissionState(input.Ledger?.Status));

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
        var jointInput = relevantResults.FirstOrDefault(input =>
            string.Equals(input.Request.AnalysisKind, AiAnalysisKind.ProductJointUnderstanding, StringComparison.Ordinal));
        var variantPlanning = jointInput?.Result is { } jointResult
            ? AiVariantPlanner.Plan(
                jointResult.VariantGroups,
                jointInput.Request.Requirements,
                jointResult.RequirementMatches,
                lockedByRequirement.ToDictionary(
                    item => item.Key,
                    item => item.Value.AssetId,
                    StringComparer.Ordinal))
            : null;
        var hasVariantConflict = relevantResults.SelectMany(input => input.Result!.Warnings)
            .Any(warning => string.Equals(warning.Code, "variant_conflict", StringComparison.Ordinal))
            && matches.Length > 0
            && pageBindings.Any(binding => !lockedByRequirement.ContainsKey(binding.FrameId));
        // A product-once page honors its explicit frame locks; it is not an
        // automatically synthesized color version. Keep color-version gates
        // on per-variant pages, without contaminating unrelated product pages.
        var hasPlannedVariantConflict = page.GenerationMode != TemplateGenerationMode.OncePerProduct
            && variantPlanning?.Status == AiVariantPlanningStatus.Conflict;
        hasVariantConflict |= hasPlannedVariantConflict;
        var hasIncompleteVariant = variantPlanning?.Variants.Any(variant => !variant.IsComplete) == true;
        var hasUnconfirmedReview = assessmentByRequirement.Values.Any(assessment =>
            assessment == AiTargetRequirementAssessment.ReviewRequired);
        var hasUnsafeOrNoSafeCandidate = assessmentByRequirement.Values.Any(assessment =>
            assessment == AiTargetRequirementAssessment.BusinessError);
        var warnings = relevantResults
            .SelectMany(input => input.Result!.Warnings)
            .Where(warning => AiProjectionWarningPolicy.IsRelevant(
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
        if (hasIncompleteVariant)
        {
            warnings = warnings.Append("部分商品配色缺少所需视角或部位；只阻塞对应颜色版本，不跨色借图。")
                .Distinct(StringComparer.Ordinal).ToArray();
        }
        if (hasPlannedVariantConflict)
        {
            warnings = warnings.Append("人工选图与颜色版本规则冲突，请为当前颜色重新选择同色素材。")
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

    private static string? ProjectionStatus(AiBatchProjectionSnapshot context, AiStyleAnalysisResultInput? input)
    {
        var status = input?.Ledger?.Status;
        return AiLedgerStatusPolicy.IsInFlight(status)
            && context.RecoveredInFlightRequestIds.Contains(input!.Request.RequestId)
                ? AiLedgerStatusPolicy.AiResultUnknown
                : status;
    }

    private static AiPageAnalysisProjection BuildDynamicSizePageProjection(
        AiBatchProjectionSnapshot context,
        StyleDraft style,
        TemplatePage page,
        IReadOnlyList<AiStyleAnalysisResultInput> styleResults,
        IReadOnlyDictionary<string, AiLockedSelection> lockedByRequirement)
    {
        var pageBindings = context.TemplateRevision.ActiveFrameBindings
            .Where(binding => string.Equals(binding.PageId, page.PageId, StringComparison.Ordinal))
            .OrderBy(binding => binding.FrameId, StringComparer.Ordinal)
            .ToArray();
        var sizeRefinements = styleResults.Where(input =>
                string.Equals(input.Request.AnalysisKind, AiAnalysisKind.SizeTableExtraction, StringComparison.Ordinal))
            .ToArray();
        var unknownSizeResult = sizeRefinements.FirstOrDefault(input =>
            ProjectionStatus(context, input) == AiLedgerStatusPolicy.AiResultUnknown);
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
            input.Ledger?.Status == AiLedgerStatusPolicy.TerminalError);
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
            input.Failure != AiAnalysisResultReadFailure.None);
        if (failedSizeResult is not null)
        {
            return ResultFailureProjection(
                context,
                style,
                page,
                pageBindings,
                failedSizeResult.Failure == AiAnalysisResultReadFailure.InvalidReference
                    ? "结果引用异常，需要恢复"
                    : "结果文件不可用，需要恢复",
                "尺码来源分析结果不可用，未启动生成。",
                "尺码分析结果不可用");
        }
        var hasActiveSizeRefinement = sizeRefinements.Any(input =>
            AiLedgerStatusPolicy.IsRecoverableAdmissionState(input.Ledger?.Status));

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

        var sizeRequirementId = AiAnalysisInputProjector.SizeTableRequirementId(page.PageId);
        var lockedSource = lockedByRequirement.GetValueOrDefault(sizeRequirementId);
        var detectedByAsset = sizeCandidates
            .GroupBy(candidate => candidate.SourceAssetId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.OrderByDescending(item => item.Confidence).First(), StringComparer.Ordinal);
        var automaticallySelectedAssetId = lockedSource?.AssetId
            ?? (sizeCandidates.Length == 1 && !hasProviderConflict ? sizeCandidates[0].SourceAssetId : null);
        var sourceCandidates = style.AssetIds
            .Select((assetId, index) =>
            {
                var asset = context.Batch.Assets.FirstOrDefault(item => item.AssetId == assetId);
                detectedByAsset.TryGetValue(assetId, out var candidate);
                return new AiSizeTableSourceCandidateProjection(
                    context.Batch.BatchId,
                    style.StyleId,
                    assetId,
                    $"照片 {index + 1:D2}",
                    asset is null ? null : ResolvePreviewPath(context.Batch.InputRoot, asset.SourceRelativePath),
                    candidate is not null,
                    candidate?.Confidence,
                    string.Equals(assetId, automaticallySelectedAssetId, StringComparison.Ordinal),
                    candidate is not null);
            })
            .ToArray();
        var sourceStatus = hasActiveSizeRefinement
            ? AiSizeTableSourceStatus.Analysing
            : automaticallySelectedAssetId is not null
                ? (sizeCandidates.Length == 1 && !hasProviderConflict
                    ? AiSizeTableSourceStatus.AutoSelected
                    : AiSizeTableSourceStatus.ReviewRequired)
                : hasProviderConflict || sizeCandidates.Length > 1
                    ? AiSizeTableSourceStatus.NeedsSelection
                    : AiSizeTableSourceStatus.Missing;
        var sourceStatusText = sourceStatus switch
        {
            AiSizeTableSourceStatus.Analysing => "正在识别尺码图",
            AiSizeTableSourceStatus.AutoSelected => "已自动找到尺码图",
            AiSizeTableSourceStatus.ReviewRequired => "已选择尺码图，等待结构确认",
            AiSizeTableSourceStatus.NeedsSelection => "找到多张候选，请选择尺码图",
            _ => "未找到尺码图，请选择一张"
        };
        var sourceProjection = new AiSizeTableSourceProjection(
            sourceStatus,
            sourceStatusText,
            automaticallySelectedAssetId,
            context.Batch.Assets.FirstOrDefault(item => item.AssetId == automaticallySelectedAssetId) is { } selectedAsset
                ? ResolvePreviewPath(context.Batch.InputRoot, selectedAsset.SourceRelativePath)
                : null,
            sourceCandidates);

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
            // Render preparation still validates table data and template calibration.
            status = AiPageUiStatus.Ready;
            statusText = "可生成";
            actionText = "已通过";
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
            RequiresManualConfiguration: status != AiPageUiStatus.Ready,
            Requirements: BuildRequirementSelections(
                context,
                style,
                page,
                pageBindings,
                [],
                lockedByRequirement.Values,
                AiRequirementSelectionUiStatus.Missing,
                "待确认尺码来源"),
            SizeTableSource: sourceProjection);
    }

    private static AiPageAnalysisProjection ResultFailureProjection(
        AiBatchProjectionSnapshot context,
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

    private static AiStyleAssetProjection[] BuildStyleAssetProjections(
        AiBatchProjectionSnapshot context,
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
        AiBatchProjectionSnapshot context,
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
        "variant_incomplete" => "部分颜色缺少所需视角或部位，只阻塞对应颜色版本。",
        "variant_manual_selection_conflict" => "人工选图与颜色版本规则冲突，请重新选择同色素材。",
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

    private static bool IsLocalAnalysisPersistenceFailure(string? code) =>
        !string.IsNullOrWhiteSpace(code)
        && (code.StartsWith("AI_CHECKPOINT_", StringComparison.Ordinal)
            || code.StartsWith("AI_SCHEDULER_STATE_", StringComparison.Ordinal)
            || code is "AI_SCHEDULER_PENDING_LEDGER_MISSING"
                or "AI_SCHEDULER_PENDING_SESSION_MISSING");
}
