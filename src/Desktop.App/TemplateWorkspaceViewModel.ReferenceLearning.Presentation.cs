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
    private void DisplayReferenceLearningPage(
        TemplatePageRowViewModel? page,
        string? preferredFrameId = null)
    {
        _refreshingReferenceLearningPage = true;
        try
        {
            ReferenceLearningFrames.Clear();
            ReferenceLearningPhotos.Clear();
            SelectedReferenceLearningFrame = null;
            if (page is null || !_referenceLearningDocuments.TryGetValue(page.PageId, out var document))
            {
                NotifyReferenceLearningStateChanged();
                return;
            }

            foreach (var photo in document.ReferencePhotos)
            {
                ReferenceLearningPhotos.Add(new ReferenceLearningPhotoViewModel(
                    photo,
                    ResolveReferencePhotoPath(photo.RelativePath)));
            }
            foreach (var candidate in document.Candidates)
            {
                ReferenceLearningFrames.Add(new ReferenceLearningFrameViewModel(candidate));
            }

            SelectedReferenceLearningFrame = ReferenceLearningFrames.FirstOrDefault(frame =>
                    string.Equals(frame.FrameId, preferredFrameId, StringComparison.Ordinal))
                ?? ReferenceLearningFrames.FirstOrDefault(frame =>
                    frame.Decision == ReferenceReviewDecision.Pending)
                ?? ReferenceLearningFrames.FirstOrDefault();
            NotifyReferenceLearningStateChanged();
        }
        finally
        {
            _refreshingReferenceLearningPage = false;
        }
    }

    private void SelectReferenceLearningFrame(string frameId)
    {
        SelectedReferenceLearningFrame = ReferenceLearningFrames.FirstOrDefault(frame =>
            string.Equals(frame.FrameId, frameId, StringComparison.Ordinal));
    }

    private ReferenceLearningRequest CreateReferenceLearningRequest(TemplatePageRowViewModel page)
    {
        if (_templateDraft is null || string.IsNullOrWhiteSpace(page.InternalPsdRelativePath))
        {
            throw new InvalidOperationException("TPL_LEARN_REQUEST_INVALID");
        }

        return new ReferenceLearningRequest(
            StableLearningId(_templateDraft.DraftRevisionId, page.PageId),
            page.PageId,
            page.InternalPsdRelativePath,
            _referenceLearningPhotoDirectory,
            TemplatePageImportService.GetPreviewRelativePath(page.InternalPsdRelativePath),
            page.Name);
    }

    private TemplatePageRowViewModel[] ReferenceLearningPages() => TemplatePages
        .Where(page => page.IsAffected
            && page.Enabled
            && page.ContentType == TemplatePageContentTypeOption.NormalComposition)
        .ToArray();

    private string? ResolveReferencePhotoPath(string relativePath)
    {
        if (!HasReferenceLearningPhotoDirectory)
        {
            return null;
        }

        var root = Path.GetFullPath(_referenceLearningPhotoDirectory);
        var candidate = Path.GetFullPath(Path.Combine(
            root,
            relativePath.Replace('/', Path.DirectorySeparatorChar)));
        var rootPrefix = root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return candidate.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase)
            && File.Exists(candidate)
                ? candidate
                : null;
    }

    private static string StableLearningId(string revisionId, string pageId)
    {
        var digest = Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes($"{revisionId}\n{pageId}"))).ToLowerInvariant();
        return $"learn_{digest[..32]}";
    }

    private string ToReferenceLearningUserMessage(
        ReferenceLearningError error,
        TemplatePageRowViewModel? page = null)
    {
        page ??= SelectedTemplatePage;
        var fileName = page?.InternalPsdRelativePath is { Length: > 0 } path
            ? Path.GetFileName(path)
            : null;
        var prefix = string.IsNullOrWhiteSpace(fileName)
            ? ""
            : $"PSD“{fileName}”：";
        var message = error.Code switch
        {
            "TPL_SOURCE_UNREADABLE" => "应用内部副本不可读取。",
            "TPL_LEARN_SOURCE_MISSING" => "当前学习会话引用的应用内部 PSD 副本已不存在。",
            "TPL_LEARN_SOURCE_CHANGED" => "参考 PSD 或源照片集合已发生变化。",
            "TPL_PSD_UNSUPPORTED_STRUCTURE" => "PSD 结构暂不支持自动学习，不能据此判断原始 PSD 损坏。",
            "TPL_PSD_RESOURCE_LIMIT" => "PSD 过大或结构复杂，检查器已安全停止。",
            "TPL_PSD_WORKER_UNAVAILABLE" => "PSD 检查组件不可用。",
            "TPL_PSD_WORKER_TIMEOUT" => "PSD 检查超时，源文件未被修改。",
            "TPL_PSD_WORKER_RESPONSE_TOO_LARGE" => "PSD 检查结果过大，已安全停止。",
            "TPL_PSD_WORKER_FAILED" => "PSD 检查组件异常退出。",
            "TPL_PSD_INVALID_RESPONSE" or "TPL_PSD_WORKER_INVALID_RESPONSE" => "PSD 检查组件返回了无效结果。",
            _ => error.Message
        };
        var action = error.Code switch
        {
            "TPL_SOURCE_UNREADABLE" or "TPL_LEARN_SOURCE_MISSING" => "请重新导入对应 PSD 后重新开始学习；不会修改用户原始 PSD。",
            "TPL_LEARN_SOURCE_CHANGED" => "请确认当前 PSD 和源照片文件夹后重新分析；不会沿用旧坐标或匹配结果。",
            "TPL_PSD_UNSUPPORTED_STRUCTURE" => "请确认 PSD 保留可读的智能对象；也可以返回页面设置改用拖拽校准。",
            "TPL_PSD_WORKER_UNAVAILABLE" => "请使用带 psd-inspection-worker 的完整运行目录重试。",
            _ => error.UserAction
        };
        return $"{prefix}{message} {action}".Trim();
    }

    private Task<bool> EnsureAiUploadConsentForReferenceLearningAsync() =>
        _host.EnsureUploadConsentAsync();

    private void NotifyReferenceLearningStateChanged()
    {
        OnPropertyChanged(nameof(CalibrationFrameItems));
        OnPropertyChanged(nameof(CalibrationWorkflowStatusMessage));
        OnPropertyChanged(nameof(CalibrationPreviewImagePath));
        OnPropertyChanged(nameof(CalibrationPreviewImageSource));
        OnPropertyChanged(nameof(CalibrationTemplatePreviewImagePath));
        OnPropertyChanged(nameof(CalibrationCanvasWidth));
        OnPropertyChanged(nameof(CalibrationCanvasHeight));
        OnPropertyChanged(nameof(CalibrationHeaderTitle));
        OnPropertyChanged(nameof(CanEditCalibration));
        OnPropertyChanged(nameof(CanClearCalibrationAsset));
        OnPropertyChanged(nameof(CanSaveCurrentCalibration));
        OnPropertyChanged(nameof(CanSaveTemplateCanvas));
        OnPropertyChanged(nameof(ReferenceLearningPageProgress));
        OnPropertyChanged(nameof(ReferenceLearningCurrentPageProgress));
        OnPropertyChanged(nameof(ReferenceLearningOverallProgress));
        OnPropertyChanged(nameof(ReferenceLearningReviewInstruction));
        OnPropertyChanged(nameof(ReferenceLearningFrameSummary));
        OnPropertyChanged(nameof(IsReferenceLearningAiUnavailable));
        OnPropertyChanged(nameof(IsReferenceLearningSemanticPending));
        OnPropertyChanged(nameof(IsReferenceLearningSemanticWarningVisible));
        OnPropertyChanged(nameof(ReferenceLearningSemanticWarningText));
        OnPropertyChanged(nameof(ReferenceLearningRuleSummary));
        OnPropertyChanged(nameof(CalibrationRuleStatusText));
        OnPropertyChanged(nameof(CalibrationEditorHintText));
        OnPropertyChanged(nameof(CanEditReferenceLearningRule));
        OnPropertyChanged(nameof(CanApplyAndConfirmCalibration));
        OnPropertyChanged(nameof(IsReferenceLearningManualFallbackVisible));
        OnPropertyChanged(nameof(CalibrationFooterMetaText));
        OnPropertyChanged(nameof(CanCommitReferenceLearning));
        OnPropertyChanged(nameof(CanConfirmReferenceLearningPage));
        OnPropertyChanged(nameof(CanConfirmAllQualifiedReferenceLearning));
        OnPropertyChanged(nameof(ReferenceLearningQualifiedCandidateSummary));
        NotifyCalibrationFrameChanged();
        RaiseReferenceLearningCommands();
    }

    private void SetReferenceLearningSemanticPending(bool pending)
    {
        if (_referenceLearningSemanticPending == pending)
        {
            return;
        }

        _referenceLearningSemanticPending = pending;
        OnPropertyChanged(nameof(IsReferenceLearningSemanticPending));
        OnPropertyChanged(nameof(IsReferenceLearningSemanticWarningVisible));
        OnPropertyChanged(nameof(ReferenceLearningSemanticWarningText));
        OnPropertyChanged(nameof(CalibrationRuleStatusText));
        OnPropertyChanged(nameof(CanEditReferenceLearningRule));
        OnPropertyChanged(nameof(CanAcceptReferenceLearningFrame));
        OnPropertyChanged(nameof(CanApplyAndConfirmCalibration));
        OnPropertyChanged(nameof(CanConfirmReferenceLearningPage));
        OnPropertyChanged(nameof(CanConfirmAllQualifiedReferenceLearning));
        RaiseReferenceLearningCommands();
    }

    private void ClearReferenceLearningPreview()
    {
        _refreshingReferenceLearningPage = true;
        try
        {
            ReferenceLearningFrames.Clear();
            ReferenceLearningPhotos.Clear();
            SelectedReferenceLearningFrame = null;
            SelectedReferenceLearningPhoto = null;
        }
        finally
        {
            _refreshingReferenceLearningPage = false;
        }

        NotifyReferenceLearningStateChanged();
    }

    private void ResetReferenceLearningDraft()
    {
        _referenceLearningDocuments.Clear();
        _referenceLearningInputDrafts.Clear();
        ReferenceLearningFrames.Clear();
        ReferenceLearningPhotos.Clear();
        SelectedReferenceLearningFrame = null;
        ReferenceLearningStatusMessage = "请选择制作这些 PSD 时使用的同款源照片文件夹。";
        OnPropertyChanged(nameof(CanCommitReferenceLearning));
        OnPropertyChanged(nameof(CanSaveTemplateCanvas));
        OnPropertyChanged(nameof(CanConfirmReferenceLearningPage));
        OnPropertyChanged(nameof(CanConfirmAllQualifiedReferenceLearning));
        OnPropertyChanged(nameof(ReferenceLearningQualifiedCandidateSummary));
    }

    private void RaiseReferenceLearningCommands()
    {
        (AcceptReferenceLearningFrameCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (ApplyAndConfirmCalibrationCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (ConfirmReferenceLearningPageCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (ConfirmAllQualifiedReferenceLearningCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (RejectReferenceLearningFrameCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (ReturnReferenceLearningToManualCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (ReanalyzeReferenceLearningCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
    }

    private void CaptureReferenceLearningInputs()
    {
        if (_refreshingReferenceLearningPage
            || IsReferenceLearningSemanticPending
            || !IsReferenceLearningReviewMode
            || _selectedReferenceLearningFrame is null)
        {
            return;
        }

        var semanticLabel = EffectiveReferenceLearningSemantic(_selectedReferenceLearningFrame);
        _referenceLearningInputDrafts[_selectedReferenceLearningFrame.FrameId] =
            new ReferenceLearningInputDraft(
                semanticLabel,
                !string.Equals(
                    semanticLabel,
                    _selectedReferenceLearningFrame.SemanticLabel?.Trim(),
                    StringComparison.Ordinal),
                CalibrationScaleInput,
                CalibrationOffsetXInput,
                CalibrationOffsetYInput,
                SelectedReferenceLearningPhoto?.AssetId);
    }

    private string EffectiveReferenceLearningSemantic(ReferenceLearningFrameViewModel frame)
    {
        if (!string.IsNullOrWhiteSpace(CalibrationRequirementInput))
        {
            return CalibrationRequirementInput.Trim();
        }

        if (_referenceLearningInputDrafts.TryGetValue(frame.FrameId, out var draft)
            && draft.IsSemanticUserOverride
            && !string.IsNullOrWhiteSpace(draft.SemanticLabel))
        {
            return draft.SemanticLabel.Trim();
        }

        return frame.SemanticLabel?.Trim() ?? "";
    }

    private static string FormatRuleNumber(double value) =>
        value.ToString("0.###", CultureInfo.InvariantCulture);

    private (int Reviewed, int Total, int Pending, int Manual) GetReferenceLearningProgress(
        TemplatePageRowViewModel? page = null)
    {
        IEnumerable<ReferenceLearningDocument> documents = page is not null
            ? _referenceLearningDocuments.TryGetValue(page.PageId, out var document)
                ? [document]
                : []
            : _referenceLearningDocuments.Values;
        var candidates = documents.SelectMany(document => document.Candidates).ToArray();
        var pending = candidates.Count(candidate =>
            candidate.ReviewDecision == ReferenceReviewDecision.Pending);
        var manual = candidates.Count(candidate =>
            candidate.ReviewDecision == ReferenceReviewDecision.ManualCalibration);
        return (candidates.Length - pending, candidates.Length, pending, manual);
    }

}
