using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using Desktop.Application.Templates;
using Microsoft.Win32;

namespace Desktop.App;

public sealed partial class TemplateWorkspaceViewModel : INotifyPropertyChanged, IDisposable
{
    private Task<TemplateDialogChoice> ShowTemplateDialogAsync(
        string title,
        string body,
        string safetyNote,
        string primaryText,
        string? secondaryText,
        string? tertiaryText = null,
        bool isDestructive = false) =>
        ShowTemplateDialogCoreAsync(
            title,
            body,
            safetyNote,
            primaryText,
            secondaryText,
            tertiaryText,
            isDestructive,
            hasTextInput: false,
            inputText: "");

    private Task<TemplateDialogChoice> ShowTemplateRenameDialogAsync(string currentName) =>
        ShowTemplateDialogCoreAsync(
            "重命名模板组",
            $"为“{currentName}”输入新的模板组名称。",
            "只修改模板库中的显示名称；页面规则、内部 PSD 副本和既有批次快照保持不变。",
            "保存名称",
            "取消",
            tertiaryText: null,
            isDestructive: false,
            hasTextInput: true,
            inputText: currentName);

    private Task<TemplateDialogChoice> ShowTemplateDialogCoreAsync(
        string title,
        string body,
        string safetyNote,
        string primaryText,
        string? secondaryText,
        string? tertiaryText,
        bool isDestructive,
        bool hasTextInput,
        string inputText)
    {
        if (_templateDialogCompletion is not null)
        {
            throw new InvalidOperationException("A template dialog is already open.");
        }

        TemplateDialogTitle = title;
        TemplateDialogBody = body;
        TemplateDialogSafetyNote = safetyNote;
        TemplateDialogPrimaryText = primaryText;
        TemplateDialogSecondaryText = secondaryText ?? "";
        TemplateDialogTertiaryText = tertiaryText ?? "";
        TemplateDialogHasSecondaryAction = !string.IsNullOrWhiteSpace(secondaryText);
        TemplateDialogHasTertiaryAction = !string.IsNullOrWhiteSpace(tertiaryText);
        TemplateDialogIsDestructive = isDestructive;
        TemplateDialogInputText = inputText;
        TemplateDialogHasTextInput = hasTextInput;
        IsTemplateDialogVisible = true;

        _templateDialogCompletion = new TaskCompletionSource<TemplateDialogChoice>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        return _templateDialogCompletion.Task;
    }

    private void CompleteTemplateDialog(TemplateDialogChoice choice)
    {
        var completion = _templateDialogCompletion;
        if (completion is null)
        {
            return;
        }

        _templateDialogCompletion = null;
        IsTemplateDialogVisible = false;
        completion.TrySetResult(choice);
    }

    private enum TemplateDialogChoice
    {
        Primary,
        Secondary,
        Tertiary
    }

    private void UpdateCalibrationInputs(TemplateCalibrationDraftView draft)
    {
        OnPropertyChanged(nameof(CalibrationDraft));
        OnPropertyChanged(nameof(CalibrationFrameItems));
        var selectedFrameId = draft.SelectedFrameId;
        if (string.IsNullOrWhiteSpace(selectedFrameId)
            || !draft.Frames.Any(frame => string.Equals(
                frame.FrameId,
                selectedFrameId,
                StringComparison.Ordinal)))
        {
            selectedFrameId = draft.Frames.Any(frame => string.Equals(
                frame.FrameId,
                CalibrationFrameIdInput,
                StringComparison.Ordinal))
                ? CalibrationFrameIdInput
                : draft.Frames.Count > 0 ? draft.Frames[0].FrameId : null;
        }

        // Re-apply the selection after Frames changes. WPF temporarily writes an empty
        // SelectedValue back to this two-way property while replacing ItemsSource.
        CalibrationFrameIdInput = selectedFrameId ?? "";
        CalibrationScaleInput = draft.SelectedScale;
        CalibrationOffsetXInput = draft.SelectedOffsetX;
        CalibrationOffsetYInput = draft.SelectedOffsetY;
        CalibrationRequirementInput = draft.SelectionRequirement;
        OnPropertyChanged(nameof(CalibrationPreviewImagePath));
        OnPropertyChanged(nameof(CalibrationTemplatePreviewImagePath));
        OnPropertyChanged(nameof(CalibrationCanvasWidth));
        OnPropertyChanged(nameof(CalibrationCanvasHeight));
        OnPropertyChanged(nameof(CalibrationHeaderTitle));
        OnPropertyChanged(nameof(CanEditCalibration));
        OnPropertyChanged(nameof(CanEditReferenceLearningRule));
        NotifyCalibrationFrameChanged();
    }

    private void NotifyCalibrationFrameChanged()
    {
        OnPropertyChanged(nameof(CalibrationFrameLeft));
        OnPropertyChanged(nameof(CalibrationFrameTop));
        OnPropertyChanged(nameof(CalibrationFrameWidth));
        OnPropertyChanged(nameof(CalibrationFrameHeight));
        OnPropertyChanged(nameof(CalibrationFrameDisplayName));
        OnPropertyChanged(nameof(CalibrationAssetActionText));
        OnPropertyChanged(nameof(CanClearCalibrationAsset));
        OnPropertyChanged(nameof(CanApplyAndConfirmCalibration));
        RaiseTemplateCommandCanExecuteChanged();
    }

    private void SetCalibrationNarrowPanel(TemplateWorkspaceNarrowPanel panel) =>
        TemplateWorkspaceState.SetNarrowPanel(panel);

    private void UpdatePageOrderLabels()
    {
        for (var index = 0; index < TemplatePages.Count; index++)
        {
            TemplatePages[index].SetDisplayOrder(index + 1);
        }
    }

    private void SetTemplateScreen(TemplateScreen screen)
    {
        if (screen != TemplateScreen.ReferenceLearning)
        {
            CaptureReferenceLearningInputs();
        }

        _templateScreen = screen;
        if (screen is not (TemplateScreen.Canvas or TemplateScreen.ReferenceLearning))
        {
            Interlocked.Increment(ref _calibrationPageLoadVersion);
        }
        OnPropertyChanged(nameof(IsTemplateLibraryVisible));
        OnPropertyChanged(nameof(IsTemplateDetailsVisible));
        OnPropertyChanged(nameof(IsTemplateCalibrationVisible));
        OnPropertyChanged(nameof(IsTemplateCanvasVisible));
        OnPropertyChanged(nameof(IsRuleWorkbenchVisible));
        OnPropertyChanged(nameof(SelectedCalibrationPage));
        OnPropertyChanged(nameof(IsReferenceLearningReviewMode));
        OnPropertyChanged(nameof(IsManualCalibrationReviewMode));
        OnPropertyChanged(nameof(IsReferenceLearningSemanticPending));
        OnPropertyChanged(nameof(IsReferenceLearningSemanticWarningVisible));
        OnPropertyChanged(nameof(CalibrationFrameItems));
        OnPropertyChanged(nameof(CalibrationWorkflowStatusMessage));
        OnPropertyChanged(nameof(CalibrationFlowStepText));
        OnPropertyChanged(nameof(CanSaveCurrentCalibration));
        OnPropertyChanged(nameof(CanEditCalibration));
        OnPropertyChanged(nameof(CanEditReferenceLearningRule));
        OnPropertyChanged(nameof(CalibrationPreviewImagePath));
        OnPropertyChanged(nameof(CalibrationPreviewImageSource));
        OnPropertyChanged(nameof(CalibrationEditorHintText));
        OnPropertyChanged(nameof(CanApplyAndConfirmCalibration));
        OnPropertyChanged(nameof(IsSelectedNormalComposition));
        OnPropertyChanged(nameof(IsSelectedDynamicSizeTable));
        OnPropertyChanged(nameof(CanSaveTemplateCanvas));
    }

    private void NotifyTemplateDraftChanged()
    {
        OnPropertyChanged(nameof(IsIncrementalTemplateDraft));
        OnPropertyChanged(nameof(IsTemplatePageEditDraft));
        OnPropertyChanged(nameof(IsFullTemplateEdit));
        OnPropertyChanged(nameof(CanEditTemplateGroup));
        OnPropertyChanged(nameof(CanSaveAsTemplate));
        OnPropertyChanged(nameof(TemplateCanvasTitle));
        OnPropertyChanged(nameof(TemplateCanvasSubtitle));
        OnPropertyChanged(nameof(TemplateCanvasStatusText));
        OnPropertyChanged(nameof(CanSaveTemplateCanvas));
        RaiseTemplateCommandCanExecuteChanged();
    }

    private void RaiseTemplateCommandCanExecuteChanged()
    {
        (RefreshTemplatesCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (AddTemplatePagesCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (StartCalibrationCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (ClearCalibrationAssetCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (SaveTemplateCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (EditTemplateGroupCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (SaveTemplateAsCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
    }

    private bool SetTemplateField<T>(ref T field, T value, [System.Runtime.CompilerServices.CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private static string ToUserMessage(string code) => code switch
    {
        "TPL_GROUP_NAME_INVALID" => "请输入 1–256 个字符的模板组名称。",
        "SIZE_TABLE_CALIBRATION_INVALID" => "尺码表区域、图层或样式与模板不匹配，请重新设置。",
        "TPL_PAGE_NAME_INVALID" => "页面名称不能为空，且不能超过 256 个字符。",
        "TPL_PAGE_VARIANT_CAPACITY_INVALID" => "按变体数量组合时必须填写大于 0 的容量。",
        "TPL_REVISION_PAGE_SET_EMPTY" => "模板组至少保留一个页面。",
        "TPL_INCREMENT_ORIGINAL_PAGE_READ_ONLY" => "增量流程默认不修改原页面，请只设置新增页面。",
        "TPL_CALIBRATION_ASSET_REF_INVALID" => "请选择可读取的 JPG、PNG、WebP、TIFF 或 BMP 代表款素材。",
        "TPL_CALIBRATION_ASSET_SOURCE_UNAVAILABLE" => "本机代表款素材只读检查服务不可用。",
        "TPL_FRAME_STRUCTURE_EVIDENCE_INCOMPLETE" => "PSD 图片框的结构证据不完整，已阻止注册；请重新检查模板。",
        "TPL_SOURCE_UNREADABLE" => "应用内部 PSD 副本不可读取；请重新导入对应 PSD。",
        "TPL_PSD_UNSUPPORTED_STRUCTURE" => "这个 PSD 的结构暂不支持自动学习；请确认保留了可读的智能对象，或改用拖拽校准。",
        "TPL_PSD_RESOURCE_LIMIT" => "这个 PSD 过大或结构复杂，检查器已安全停止；请改用较小的副本或 Photoshop 兼容性检查。",
        "TPL_PSD_WORKER_UNAVAILABLE" => "PSD 检查组件不可用；请使用带 psd-inspection-worker 的完整运行目录重试。",
        "TPL_PSD_WORKER_TIMEOUT" => "PSD 检查超时但源文件未被修改；请重试或改用 Photoshop 兼容性检查。",
        "TPL_PSD_WORKER_RESPONSE_TOO_LARGE" => "PSD 检查结果过大，已安全停止；请改用 Photoshop 兼容性检查。",
        "TPL_PSD_WORKER_FAILED" => "PSD 检查组件异常退出；请确认应用目录中的检查组件完整后重试。",
        "TPL_PSD_WORKER_INVALID_RESPONSE" or "TPL_PSD_INVALID_RESPONSE" => "PSD 检查组件返回了无效结果；请重启完整运行目录后重试。",
        "TPL_LEARN_SOURCE_MISSING" => "当前学习会话引用的应用内部 PSD 副本已不存在；请重新导入对应 PSD 后重新开始学习。",
        "TPL_DRAFT_IN_PROGRESS" => "已有未完成的模板编辑草稿；请先打开模板组恢复草稿，或放弃该草稿后再编辑。",
        "TPL_DELETE_FILES_FAILED" => "模板文件删除失败，文件可能被其他程序占用；请关闭相关程序后重试。",
        "TPL_DELETE_ACCESS_DENIED" => "没有权限删除模板文件；请检查文件权限或以管理员身份运行。",
        "TPL_DELETE_FILES_LOCKED" => "模板文件被占用无法删除；请关闭正在访问这些文件的程序后重试。",
        _ => $"操作未完成（{code}），请检查后重试。"
    };

    private static string ToTemplateOperationError(Exception exception)
    {
        var root = UnwrapTemplateOperationException(exception);
        return root switch
        {
            IOException or UnauthorizedAccessException =>
                "模板库或内部副本暂时不可访问；请关闭占用文件的程序后重试。",
            InvalidOperationException invalidOperation => ToUserMessage(invalidOperation.Message),
            JsonException or InvalidDataException =>
                "模板内部资源记录无法读取；模板组未被删除，请重新导入对应 PSD 后再试。",
            FormatException =>
                "模板修订记录格式异常；模板组未被删除，请重新打开模板组后再试。",
            ArgumentException or NullReferenceException =>
                "模板页面的内部资源信息不完整；模板组未被删除，请重新导入对应 PSD 后再试。",
            _ =>
                $"模板编辑草稿加载失败（程序内部：{root.GetType().Name}）；模板组未被删除，请将此提示截图交给技术处理。"
        };
    }

    private static Exception UnwrapTemplateOperationException(Exception exception)
    {
        while (exception is AggregateException aggregate
               && aggregate.InnerExceptions.Count == 1)
        {
            exception = aggregate.InnerExceptions[0];
        }

        return exception;
    }

    private enum TemplateScreen
    {
        Library,
        Details,
        Canvas,
        ReferenceLearning
    }

    private sealed record PendingTemplateImport(
        string CommandId,
        string ResourceId,
        string SourcePath,
        string PageName,
        string? DuplicateResourceId);
    private void TemplateWorkspaceState_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        OnPropertyChanged(e.PropertyName);
        switch (e.PropertyName)
        {
            case nameof(TemplateWorkspacePageState.CalibrationViewportZoom):
                OnPropertyChanged(nameof(CalibrationFooterMetaText));
                break;
            case nameof(TemplateWorkspacePageState.TemplateStatusMessage):
                OnPropertyChanged(nameof(HasTemplateStatusMessage));
                break;
            case nameof(TemplateWorkspacePageState.TemplateBusy):
                OnPropertyChanged(nameof(IsTemplateLibraryEmpty));
                OnPropertyChanged(nameof(CanEditTemplateGroup));
                OnPropertyChanged(nameof(CanSaveAsTemplate));
                RaiseTemplateCommandCanExecuteChanged();
                break;
        }
    }
    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    public void Dispose()
    {
        RuleWorkbench?.Dispose();
        State.PropertyChanged -= TemplateWorkspaceState_PropertyChanged;
        _templateDraftWrite.Dispose();
        GC.SuppressFinalize(this);
    }

}
