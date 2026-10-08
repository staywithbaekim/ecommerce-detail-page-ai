using System.Collections;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Input;
using Desktop.Application.Templates;

namespace Desktop.App;

public sealed partial class TemplateWorkspaceViewModel
{
    private void InitializeReferenceLearning()
    {
        ChooseReferenceLearningFolderCommand = new AsyncRelayCommand(ChooseReferenceLearningFolderAsync);
        AcceptReferenceLearningFrameCommand = new AsyncRelayCommand(
            () => ApplyReferenceLearningReviewAsync(ReferenceReviewDecision.Accepted),
            () => CanAcceptReferenceLearningFrame);
        ConfirmReferenceLearningPageCommand = new AsyncRelayCommand(
            ConfirmReferenceLearningPageAsync,
            () => CanConfirmReferenceLearningPage);
        ConfirmAllQualifiedReferenceLearningCommand = new AsyncRelayCommand(
            ConfirmAllQualifiedReferenceLearningAsync,
            () => CanConfirmAllQualifiedReferenceLearning);
        RejectReferenceLearningFrameCommand = new AsyncRelayCommand(
            () => ApplyReferenceLearningReviewAsync(ReferenceReviewDecision.Rejected),
            () => SelectedReferenceLearningFrame is not null && !ReferenceLearningBusy);
        ReturnReferenceLearningToManualCommand = new AsyncRelayCommand(
            ReturnReferenceLearningToManualAsync,
            () => SelectedReferenceLearningFrame is not null && !ReferenceLearningBusy);
        ReanalyzeReferenceLearningCommand = new AsyncRelayCommand(
            ReanalyzeCurrentReferenceLearningPageAsync,
            () => SelectedTemplatePage is not null && HasReferenceLearningPhotoDirectory && !ReferenceLearningBusy);
        CancelReferenceLearningCommand = new AsyncRelayCommand(CancelReferenceLearningAsync);

        ReferenceLearningFrames.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(CalibrationFrameItems));
            OnPropertyChanged(nameof(CanCommitReferenceLearning));
            OnPropertyChanged(nameof(CanConfirmReferenceLearningPage));
            OnPropertyChanged(nameof(CanConfirmAllQualifiedReferenceLearning));
            OnPropertyChanged(nameof(ReferenceLearningQualifiedCandidateSummary));
        };
    }

    private void SetTemplateCalibrationMode(TemplateCalibrationMode mode)
    {
        if (_templateCalibrationMode == mode)
        {
            return;
        }

        _templateCalibrationMode = mode;
        OnPropertyChanged(nameof(IsDragCalibrationModeSelected));
        OnPropertyChanged(nameof(IsReferenceLearningModeSelected));
        OnPropertyChanged(nameof(CalibrationStartActionText));
        OnPropertyChanged(nameof(CalibrationModeHelpText));
        OnPropertyChanged(nameof(CalibrationEditorHintText));
        RaiseTemplateCommandCanExecuteChanged();
    }

    private async Task ChooseReferenceLearningFolderAsync()
    {
        if (_folderPicker is null)
        {
            ReferenceLearningStatusMessage = "本机文件夹选择器不可用。";
            return;
        }

        var selected = await _folderPicker.PickFolderAsync(
            "选择制作参考 PSD 时使用的源照片文件夹",
            HasReferenceLearningPhotoDirectory ? _referenceLearningPhotoDirectory : null,
            CancellationToken.None).ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(selected))
        {
            return;
        }

        _referenceLearningPhotoDirectory = Path.GetFullPath(selected);
        ReferenceLearningStatusMessage = "已选择源照片文件夹；路径只在本次本地学习中使用。";
        OnPropertyChanged(nameof(HasReferenceLearningPhotoDirectory));
        OnPropertyChanged(nameof(ReferenceLearningPhotoDirectoryDisplay));
        RaiseTemplateCommandCanExecuteChanged();
        RaiseReferenceLearningCommands();
    }

    public async Task StartReferenceLearningAsync()
    {
        if (_referenceLearning is null || _templateDraft is null || !HasReferenceLearningPhotoDirectory)
        {
            TemplateStatusMessage = "请选择参考款源照片文件夹，并确认本地学习服务可用。";
            return;
        }

        var pages = ReferenceLearningPages();
        if (pages.Length == 0)
        {
            TemplateStatusMessage = "当前模板没有需要学习的普通套图页。";
            return;
        }

        // Reference learning uploads the same bounded analysis copies as a
        // normal batch analysis. Keep the privacy gate in front of the whole
        // workflow so an unconsented run cannot quietly fall back to the
        // generic local placeholder and look like a successful AI result.
        if (!await EnsureAiUploadConsentForReferenceLearningAsync().ConfigureAwait(true))
        {
            return;
        }

        ReferenceLearningBusy = true;
        SetTemplateScreen(TemplateScreen.ReferenceLearning);
        SetReferenceLearningSemanticPending(true);
        ClearReferenceLearningPreview();
        ReferenceLearningStatusMessage = "正在读取参考 PSD 并准备整组 AI 语义分析；完成前不会把本地占位当作最终规则。";
        try
        {
            _referenceLearningDocuments.Clear();
            // A previous review can have captured the old local placeholder
            // in this view-model. It must never override a newly returned
            // AiV2 semantic result.
            _referenceLearningInputDrafts.Clear();
            foreach (var page in pages)
            {
                var request = CreateReferenceLearningRequest(page);
                var result = await _referenceLearning.ResumeAsync(request, CancellationToken.None)
                    .ConfigureAwait(true);
                if (!result.IsSuccess && result.Error?.Code == "TPL_LEARN_SESSION_NOT_FOUND")
                {
                    result = await _referenceLearning.AnalyzeAsync(request, CancellationToken.None)
                        .ConfigureAwait(true);
                }

                if (!result.IsSuccess)
                {
                    SelectedTemplatePage = page;
                    ReferenceLearningStatusMessage = ToReferenceLearningUserMessage(result.Error!);
                    DisplayReferenceLearningPage(page);
                    return;
                }

                if (result.Document!.State is ReferenceLearningState.Cancelled
                    or ReferenceLearningState.Committed)
                {
                    result = await _referenceLearning.AnalyzeAsync(request, CancellationToken.None)
                        .ConfigureAwait(true);
                    if (!result.IsSuccess)
                    {
                        SelectedTemplatePage = page;
                        ReferenceLearningStatusMessage = ToReferenceLearningUserMessage(result.Error!);
                        return;
                    }
                }

                _referenceLearningDocuments[page.PageId] = result.Document!;
            }

            ReferenceLearningAiFailure? semanticFailure = null;
            if (_referenceLearningAi is not null)
            {
                ReferenceLearningStatusMessage = "本地结构已完成，正在调用 AI 生成整组逐框动态规则（只发起一次整组请求）。";
                var semantic = await _referenceLearningAi.GenerateSelectionRequirementsAsync(
                    pages.Select(page => new ReferenceSemanticGenerationCommand(
                        _referenceLearningDocuments[page.PageId],
                        _referenceLearningPhotoDirectory,
                        page.Name)).ToArray(),
                    CancellationToken.None).ConfigureAwait(true);
                foreach (var document in semantic.Documents)
                {
                    _referenceLearningDocuments[document.PageId] = document;
                }
                semanticFailure = semantic.Failure;
            }

            SelectedTemplatePage = pages.FirstOrDefault(page =>
                _referenceLearningDocuments[page.PageId].State != ReferenceLearningState.ReadyToCommit)
                ?? pages[0];
            // Keep the pending gate active while rebuilding the observable
            // frame list. SelectedReferenceLearningFrame's teardown would
            // otherwise capture the old placeholder into the input-draft
            // dictionary and mask the returned AiV2 text.
            _referenceLearningInputDrafts.Clear();
            DisplayReferenceLearningPage(SelectedTemplatePage);
            SetReferenceLearningSemanticPending(false);
            var candidateCount = _referenceLearningDocuments.Values.Sum(document =>
                document.Candidates.Count(candidate => candidate.IsProduct));
            ReferenceLearningStatusMessage = semanticFailure is null
                ? $"已识别 {candidateCount} 个有效可替换素材框。系统只显示同时匹配参考源照片、并在最终页面实际显示的内容层。"
                : semanticFailure.UserAction;
        }
        finally
        {
            SetReferenceLearningSemanticPending(false);
            ReferenceLearningBusy = false;
            NotifyReferenceLearningStateChanged();
        }
    }

    private async Task ReanalyzeCurrentReferenceLearningPageAsync()
    {
        if (_referenceLearning is null || SelectedTemplatePage is null)
        {
            return;
        }

        ReferenceLearningBusy = true;
        SetReferenceLearningSemanticPending(true);
        ClearReferenceLearningPreview();
        ReferenceLearningStatusMessage = "正在重新分析当前页；AI 动态规则返回前不会显示本地占位为最终结果。";
        try
        {
            if (_referenceLearningDocuments.TryGetValue(
                    SelectedTemplatePage.PageId,
                    out var previousDocument))
            {
                foreach (var frame in previousDocument.Candidates)
                {
                    _referenceLearningInputDrafts.Remove(frame.FrameId);
                }
            }

            var result = await _referenceLearning.AnalyzeAsync(
                CreateReferenceLearningRequest(SelectedTemplatePage),
                CancellationToken.None).ConfigureAwait(true);
            if (!result.IsSuccess)
            {
                ReferenceLearningStatusMessage = ToReferenceLearningUserMessage(result.Error!);
                return;
            }

            var document = result.Document!;
            ReferenceLearningAiFailure? semanticFailure = null;
            if (_referenceLearningAi is not null)
            {
                ReferenceLearningStatusMessage = "本地结构已完成，正在调用 AI 生成当前页逐框动态规则。";
                var semantic = await _referenceLearningAi.GenerateSelectionRequirementsAsync(
                    new ReferenceSemanticGenerationCommand(
                        document,
                        _referenceLearningPhotoDirectory,
                        SelectedTemplatePage.Name),
                    CancellationToken.None).ConfigureAwait(true);
                document = semantic.Document;
                semanticFailure = semantic.Failure;
            }
            _referenceLearningDocuments[SelectedTemplatePage.PageId] = document;
            _referenceLearningInputDrafts.Clear();
            DisplayReferenceLearningPage(SelectedTemplatePage);
            SetReferenceLearningSemanticPending(false);
            ReferenceLearningStatusMessage = semanticFailure?.UserAction
                ?? "当前页已重新分析，旧复核结论未沿用。";
        }
        finally
        {
            SetReferenceLearningSemanticPending(false);
            ReferenceLearningBusy = false;
            NotifyReferenceLearningStateChanged();
        }
    }

    private async Task ApplyReferenceLearningReviewAsync(ReferenceReviewDecision decision)
    {
        if (_referenceLearning is null
            || CurrentReferenceLearningDocument is not { } document
            || SelectedReferenceLearningFrame is not { } frame)
        {
            return;
        }

        ReferenceLearningBusy = true;
        try
        {
            var semanticLabel = EffectiveReferenceLearningSemantic(frame);
            var review = new ReferenceFrameReview(
                frame.FrameId,
                decision,
                decision == ReferenceReviewDecision.Accepted
                    ? SelectedReferenceLearningPhoto?.AssetId
                    : null,
                semanticLabel,
                CalibrationScaleInput,
                CalibrationOffsetXInput,
                CalibrationOffsetYInput,
                decision == ReferenceReviewDecision.Rejected
                    ? "用户确认该智能对象不是可替换商品图片框。"
                    : null);
            var result = await _referenceLearning.ApplyReviewAsync(
                document,
                [review],
                CancellationToken.None).ConfigureAwait(true);
            if (!result.IsSuccess)
            {
                ReferenceLearningStatusMessage = ToReferenceLearningUserMessage(result.Error!);
                return;
            }

            var updatedDocument = result.Document!;
            _referenceLearningDocuments[document.PageId] = updatedDocument;
            var nextFrameId = updatedDocument.Candidates
                .FirstOrDefault(candidate => candidate.ReviewDecision == ReferenceReviewDecision.Pending)?.FrameId
                ?? frame.FrameId;
            DisplayReferenceLearningPage(SelectedTemplatePage, nextFrameId);
            ReferenceLearningStatusMessage = updatedDocument.State switch
            {
                ReferenceLearningState.ReadyToCommit => "当前页已全部复核；继续检查其他页面或保存模板组。",
                ReferenceLearningState.RequiresManualCalibration => "当前页已转为人工校准，学习规则不会提交。",
                _ => "当前图片框复核已保存。"
            };
        }
        catch (InvalidOperationException exception) when (
            string.Equals(exception.Message, "TPL_LEARN_SELECTION_REQUIREMENT_INVALID", StringComparison.Ordinal))
        {
            ReferenceLearningStatusMessage = "请把未来选图规则写成 2–40 字的图片类别短语，例如“人物正面端坐图”或“面料纹理特写图”。";
        }
        finally
        {
            ReferenceLearningBusy = false;
            NotifyReferenceLearningStateChanged();
        }
    }

    private async Task ConfirmReferenceLearningPageAsync()
    {
        if (_referenceLearning is null
            || SelectedTemplatePage is null
            || CurrentReferenceLearningDocument is not { } document)
        {
            return;
        }

        var reviews = BuildQualifiedReviews(document);
        if (reviews.Length == 0)
        {
            ReferenceLearningStatusMessage = "本页没有可直接确认的高置信候选；请逐项处理异常候选。";
            return;
        }

        await ApplyReferenceLearningReviewsAsync(
            document,
            reviews,
            $"本页已确认 {reviews.Length} 个高置信候选；异常候选仍需逐项处理。",
            SelectedTemplatePage).ConfigureAwait(true);
    }

    private async Task ConfirmAllQualifiedReferenceLearningAsync()
    {
        if (_referenceLearning is null)
        {
            return;
        }

        var pending = _referenceLearningDocuments.Values
            .Select(document => (Document: document, Reviews: BuildQualifiedReviews(document)))
            .Where(item => item.Reviews.Length > 0)
            .ToArray();
        if (pending.Length == 0)
        {
            ReferenceLearningStatusMessage = "当前没有可直接确认的高置信候选；请逐项处理异常候选。";
            return;
        }

        ReferenceLearningBusy = true;
        try
        {
            var confirmed = 0;
            foreach (var item in pending)
            {
                var result = await _referenceLearning.ApplyReviewAsync(
                    item.Document,
                    item.Reviews,
                    CancellationToken.None).ConfigureAwait(true);
                if (!result.IsSuccess)
                {
                    ReferenceLearningStatusMessage = ToReferenceLearningUserMessage(result.Error!);
                    return;
                }

                _referenceLearningDocuments[item.Document.PageId] = result.Document!;
                confirmed += item.Reviews.Length;
            }

            if (SelectedTemplatePage is not null
                && _referenceLearningDocuments.TryGetValue(SelectedTemplatePage.PageId, out var current))
            {
                DisplayReferenceLearningPage(SelectedTemplatePage);
                var remaining = current.Candidates.Count(candidate =>
                    candidate.ReviewDecision == ReferenceReviewDecision.Pending);
                ReferenceLearningStatusMessage = remaining > 0
                    ? $"已批量确认 {confirmed} 个高置信候选；当前页仍有 {remaining} 个候选需逐项处理。"
                    : $"已批量确认 {confirmed} 个高置信候选；仍需显式检查后才能保存模板组。";
            }
            else
            {
                ReferenceLearningStatusMessage = $"已批量确认 {confirmed} 个高置信候选；异常候选仍需逐项处理。";
            }
        }
        finally
        {
            ReferenceLearningBusy = false;
            NotifyReferenceLearningStateChanged();
        }
    }

    private async Task ApplyReferenceLearningReviewsAsync(
        ReferenceLearningDocument document,
        IReadOnlyList<ReferenceFrameReview> reviews,
        string successMessage,
        TemplatePageRowViewModel page)
    {
        ReferenceLearningBusy = true;
        try
        {
            var result = await _referenceLearning!.ApplyReviewAsync(
                document,
                reviews,
                CancellationToken.None).ConfigureAwait(true);
            if (!result.IsSuccess)
            {
                ReferenceLearningStatusMessage = ToReferenceLearningUserMessage(result.Error!);
                return;
            }

            _referenceLearningDocuments[document.PageId] = result.Document!;
            DisplayReferenceLearningPage(page);
            ReferenceLearningStatusMessage = successMessage;
        }
        finally
        {
            ReferenceLearningBusy = false;
            NotifyReferenceLearningStateChanged();
        }
    }

    private static ReferenceFrameReview[] BuildQualifiedReviews(
        ReferenceLearningDocument document) => document.Candidates
        .Where(candidate => candidate.IsAutoConfirmable)
        .Select(candidate => new ReferenceFrameReview(
            candidate.FrameId,
            ReferenceReviewDecision.Accepted,
            candidate.ReferenceAssetId,
            candidate.SemanticLabel,
            Note: "系统根据闭合证据批量确认；保留 PSD 已提取的裁切、缩放和偏移。"))
        .ToArray();

    private async Task ReturnReferenceLearningToManualAsync()
    {
        await ApplyReferenceLearningReviewAsync(ReferenceReviewDecision.ManualCalibration)
            .ConfigureAwait(true);
        if (SelectedTemplatePage is null)
        {
            return;
        }

        SetTemplateCalibrationMode(TemplateCalibrationMode.DragAndDrop);
        SetTemplateScreen(TemplateScreen.Canvas);
        var pageLoadVersion = Interlocked.Increment(ref _calibrationPageLoadVersion);
        await LoadCalibrationPageAsync(SelectedTemplatePage, pageLoadVersion).ConfigureAwait(true);
        TemplateStatusMessage = "已退回拖拽校准；学习候选不会提交为模板规则。";
    }

    private async Task CancelReferenceLearningAsync()
    {
        if (_referenceLearning is null)
        {
            SetTemplateScreen(TemplateScreen.Canvas);
            return;
        }

        ReferenceLearningBusy = true;
        try
        {
            foreach (var document in _referenceLearningDocuments.Values.ToArray())
            {
                if (document.State is ReferenceLearningState.Cancelled
                    or ReferenceLearningState.Committed)
                {
                    continue;
                }

                var cancelled = await _referenceLearning.CancelAsync(document, CancellationToken.None)
                    .ConfigureAwait(true);
                if (!cancelled.IsSuccess)
                {
                    ReferenceLearningStatusMessage = ToReferenceLearningUserMessage(cancelled.Error!);
                    return;
                }
                _referenceLearningDocuments[document.PageId] = cancelled.Document!;
            }

            TemplateStatusMessage = "本次学习已取消；模板草稿和用户源文件保持不变。";
            SetTemplateScreen(TemplateScreen.Canvas);
        }
        catch (Exception)
        {
            ReferenceLearningStatusMessage = "取消学习未能完成，但主窗口仍保持打开；请重试或返回页面设置。";
        }
        finally
        {
            ReferenceLearningBusy = false;
        }
    }

    private async Task CommitReferenceLearningAsync()
    {
        if (_referenceLearning is null
            || _referenceLearningCommit is null
            || _templateDraft is null
            || !CanCommitReferenceLearning)
        {
            ReferenceLearningStatusMessage = "所有普通套图页逐框确认后才能保存模板组。";
            return;
        }

        ReferenceLearningBusy = true;
        TemplateBusy = true;
        try
        {
            var pages = ReferenceLearningPages();
            var verifiedDocuments = new List<ReferenceLearningDocument>(pages.Length);
            foreach (var page in pages)
            {
                var verification = await _referenceLearning.ResumeAsync(
                    CreateReferenceLearningRequest(page),
                    CancellationToken.None).ConfigureAwait(true);
                if (!verification.IsSuccess)
                {
                    SelectedTemplatePage = page;
                    ReferenceLearningStatusMessage = ToReferenceLearningUserMessage(verification.Error!);
                    return;
                }
                if (verification.Document!.State != ReferenceLearningState.ReadyToCommit)
                {
                    SelectedTemplatePage = page;
                    ReferenceLearningStatusMessage = "学习复核状态已变化；请重新检查当前页面。";
                    _referenceLearningDocuments[page.PageId] = verification.Document;
                    DisplayReferenceLearningPage(page);
                    return;
                }

                _referenceLearningDocuments[page.PageId] = verification.Document;
                verifiedDocuments.Add(verification.Document);
            }

            var documents = verifiedDocuments.ToArray();
            var result = await _referenceLearningCommit.CommitAsync(
                documents,
                _templateDraft,
                CancellationToken.None).ConfigureAwait(true);
            if (!result.IsSuccess)
            {
                ReferenceLearningStatusMessage = ToReferenceLearningUserMessage(result.Error!);
                return;
            }

            _templateDetails = result.Template;
            foreach (var document in documents)
            {
                _referenceLearningDocuments[document.PageId] = document with
                {
                    State = ReferenceLearningState.Committed,
                    UpdatedAt = result.Document!.UpdatedAt,
                    BlockReason = null
                };
            }
            TemplateStatusMessage = "人工结果学习规则已原子保存；参考款照片不会进入后续目标批次。";
            OnPropertyChanged(nameof(TemplateDetailsTitle));
            OnPropertyChanged(nameof(TemplateDetailsMeta));
            OnPropertyChanged(nameof(TemplateDetailPages));
            OnPropertyChanged(nameof(CanIncrementTemplate));
            SetTemplateScreen(TemplateScreen.Details);
        }
        finally
        {
            TemplateBusy = false;
            ReferenceLearningBusy = false;
        }
    }


}
