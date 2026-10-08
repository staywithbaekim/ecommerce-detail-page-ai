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
    private async Task StartCalibrationAsync()
    {
        foreach (var page in TemplatePages.Where(page => page.IsAffected))
        {
            await SavePageAsync(page).ConfigureAwait(true);
        }

        if (!HasCalibratableTemplatePages)
        {
            CalibrationDraft = null;
            TemplateStatusMessage = "请设置尺码表区域与样式；未设置的页面可以保存，但会阻止批次渲染。";
            SetTemplateScreen(TemplateScreen.Canvas);
            return;
        }

        if (SelectedTemplatePage is null
            || SelectedTemplatePage.ContentType != TemplatePageContentTypeOption.NormalComposition)
        {
            SelectedTemplatePage = CalibrationPages.FirstOrDefault(page => page.IsAffected)
                ?? (CalibrationPages.Count > 0 ? CalibrationPages[0] : null);
        }

        TemplateStatusMessage = IsFullTemplateEdit
            ? "模板组页面设置已保存；下一步进入统一校准编辑器。"
            : IsIncrementalTemplateDraft
            ? "新增页面设置已保存；下一步只校准新增页。"
            : "页面设置已保存；下一步进入统一校准编辑器。";
        if (IsReferenceLearningModeSelected)
        {
            await StartReferenceLearningAsync().ConfigureAwait(true);
            return;
        }
        if (_templateCalibration is null || SelectedTemplatePage is null)
        {
            SetTemplateScreen(TemplateScreen.Canvas);
            return;
        }

        try
        {
            SetTemplateScreen(TemplateScreen.Canvas);
            var pageLoadVersion = Interlocked.Increment(ref _calibrationPageLoadVersion);
            await LoadCalibrationPageAsync(SelectedTemplatePage, pageLoadVersion).ConfigureAwait(true);
        }
        catch (InvalidOperationException exception)
        {
            TemplateStatusMessage = ToUserMessage(exception.Message);
            SetTemplateScreen(TemplateScreen.Canvas);
        }
    }

    private async Task RemoveTemplatePageAsync(TemplatePageRowViewModel? page)
    {
        if (_templateWorkspace is null || _templateDraft is null || page is null)
        {
            return;
        }

        if (page.IsReadOnly)
        {
            TemplateStatusMessage = "增量流程的原页面保持只读，不能移除。";
            return;
        }

        if (TemplatePages.Count <= 1)
        {
            TemplateStatusMessage = "模板组至少保留一个页面。";
            return;
        }

        var confirmation = await ShowTemplateDialogAsync(
            "移除模板页面？",
            $"将从当前模板草稿中移除“{page.Name}”。保存模板组后，这一页不会再参与后续批次。",
            "为支持撤销，应用内部 PSD 副本暂时保留；不会删除用户源 PSD。",
            "移除页面",
            "取消",
            isDestructive: true).ConfigureAwait(true);
        if (confirmation != TemplateDialogChoice.Primary)
        {
            return;
        }

        TemplateBusy = true;
        try
        {
            await _templateWorkspace.RemovePageAsync(
                _templateDraft,
                page.PageId,
                CancellationToken.None).ConfigureAwait(true);
            var index = TemplatePages.IndexOf(page);
            TemplatePages.Remove(page);
            var imported = ImportedTemplateFiles.FirstOrDefault(item => string.Equals(
                item.ResourceId,
                page.ResourceId,
                StringComparison.Ordinal));
            if (imported is not null)
            {
                ImportedTemplateFiles.Remove(imported);
            }
            UpdatePageOrderLabels();
            SelectedTemplatePage = TemplatePages.ElementAtOrDefault(Math.Max(0, index - 1))
                ?? TemplatePages.FirstOrDefault();
            TemplateStatusMessage = "页面已从草稿移除；内部副本暂保留以支持撤销，用户源 PSD 保持只读未改动。";
        }
        catch (InvalidOperationException exception)
        {
            TemplateStatusMessage = ToUserMessage(exception.Message);
        }
        finally
        {
            TemplateBusy = false;
        }
    }

    private async Task AbandonTemplateDraftAsync()
    {
        if (_templateWorkspace is null || _templateDraft is null)
        {
            return;
        }

        try
        {
            var confirmation = await ShowTemplateDialogAsync(
                "放弃当前模板组？",
                "将删除当前模板草稿，以及软件内部为这个草稿保存的 PSD 副本和校准输入。此操作无法在应用内撤销。",
                "不会删除用户源 PSD、源照片、正式输出或其他模板组。",
                "放弃模板组",
                "取消",
                isDestructive: true).ConfigureAwait(true);
            if (confirmation != TemplateDialogChoice.Primary)
            {
                return;
            }

            TemplateBusy = true;
            var blockers = await _templateWorkspace.DeleteTemplateGroupAsync(
                _templateDraft.GroupId,
                CancellationToken.None).ConfigureAwait(true);
            if (blockers.Count > 0)
            {
                TemplateStatusMessage = "存在未完成引用任务，不能放弃：" + string.Join("、", blockers);
                return;
            }

            _templateDraft = null;
            _templateDetails = null;
            ImportedTemplateFiles.Clear();
            TemplatePages.Clear();
            TemplateStatusMessage = "当前模板草稿已放弃；用户源文件未受影响。";
            SetTemplateScreen(TemplateScreen.Library);
            await LoadTemplateLibraryAsync().ConfigureAwait(true);
        }
        catch (InvalidOperationException exception)
        {
            TemplateStatusMessage = ToUserMessage(exception.Message);
        }
        catch (Exception exception)
        {
            TemplateStatusMessage = ToTemplateOperationError(exception);
        }
        finally
        {
            TemplateBusy = false;
        }
    }

    private async Task BeginTemplatePageEditAsync(TemplatePageDetailsRowViewModel? page)
    {
        if (_templateWorkspace is null || _templateDetails is null || page is null)
        {
            return;
        }

        TemplateBusy = true;
        try
        {
            _templateDraft = await _templateWorkspace.BeginPageEditAsync(
                _templateDetails.GroupId,
                page.PageId,
                CancellationToken.None).ConfigureAwait(true);
            TemplateGroupName = _templateDraft.GroupName;
            ImportedTemplateFiles.Clear();
            TemplatePages.Clear();
            SetTemplateCalibrationMode(TemplateCalibrationMode.DragAndDrop);
            ResetReferenceLearningDraft();
            await LoadDraftPagesAsync(populateImportedFiles: true).ConfigureAwait(true);
            TemplateStatusMessage = $"已进入“{page.Name}”编辑草稿；其他页面保持只读，保存后才生成新修订。";
            NotifyTemplateDraftChanged();
            SetTemplateScreen(TemplateScreen.Canvas);
        }
        catch (Exception exception)
        {
            TemplateStatusMessage = ToTemplateOperationError(exception);
        }
        finally
        {
            TemplateBusy = false;
        }
    }

    private async Task BeginTemplateGroupEditAsync()
    {
        if (_templateWorkspace is null || _templateDetails is null)
        {
            return;
        }

        TemplateBusy = true;
        try
        {
            _templateDraft = await _templateWorkspace.BeginFullEditAsync(
                _templateDetails.GroupId,
                CancellationToken.None).ConfigureAwait(true);
            TemplateGroupName = _templateDraft.GroupName;
            ImportedTemplateFiles.Clear();
            TemplatePages.Clear();
            SetTemplateCalibrationMode(TemplateCalibrationMode.DragAndDrop);
            ResetReferenceLearningDraft();
            await LoadDraftPagesAsync(populateImportedFiles: true).ConfigureAwait(true);
            TemplateStatusMessage = "已进入模板组全量编辑草稿；全部页面均可修改，原模板修订仍保持不变。";
            NotifyTemplateDraftChanged();
            SetTemplateScreen(TemplateScreen.Canvas);
        }
        catch (Exception exception)
        {
            TemplateStatusMessage = ToTemplateOperationError(exception);
        }
        finally
        {
            TemplateBusy = false;
        }
    }

    private void HandleTemplateCommandException(Exception exception)
    {
        TemplateBusy = false;
        TemplateStatusMessage = ToTemplateOperationError(exception);
        if (IsReferenceLearningReviewMode)
        {
            ReferenceLearningStatusMessage = "模板操作未完成，但主窗口仍保持打开；请重试或返回页面设置。";
        }
    }

    private async Task LoadCalibrationPageAsync(
        TemplatePageRowViewModel page,
        long pageLoadVersion)
    {
        var calibration = _templateCalibration;
        var draftSession = _templateDraft;
        if (calibration is null || draftSession is null)
        {
            return;
        }

        var draft = await calibration.LoadAsync(
            draftSession,
            page.PageId,
            CancellationToken.None).ConfigureAwait(true);
        if (pageLoadVersion != Volatile.Read(ref _calibrationPageLoadVersion)
            || _templateScreen != TemplateScreen.Canvas
            || !ReferenceEquals(SelectedTemplatePage, page)
            || !ReferenceEquals(_templateDraft, draftSession))
        {
            return;
        }

        CalibrationDraft = draft;
        UpdateCalibrationInputs(draft);
    }

    private void OnTemplatePagePresentationChanged(TemplatePageRowViewModel page)
    {
        if (!page.IsReadOnly)
        {
            _ = SavePagePresentationAsync(page);
        }
    }

    private async Task SavePagePresentationAsync(TemplatePageRowViewModel page)
    {
        if (_templateWorkspace is not ITemplatePagePresentationService presentation
            || _templateDraft is null)
        {
            return;
        }

        await _templateDraftWrite.WaitAsync().ConfigureAwait(true);
        try
        {
            await presentation.UpdatePagePresentationAsync(
                _templateDraft,
                new TemplatePagePresentationUpdate(page.PageId, page.Notes, page.CanvasX, page.CanvasY),
                CancellationToken.None).ConfigureAwait(true);
        }
        catch (InvalidOperationException exception)
        {
            TemplateStatusMessage = ToUserMessage(exception.Message);
        }
        finally
        {
            _templateDraftWrite.Release();
        }
    }

    private Task ClearCalibrationForDynamicPageAsync(long pageLoadVersion)
    {
        if (pageLoadVersion != Volatile.Read(ref _calibrationPageLoadVersion)
            || !IsSelectedDynamicSizeTable)
        {
            return Task.CompletedTask;
        }

        CalibrationDraft = null;
        CalibrationAssetIdInput = string.Empty;
        CalibrationFrameIdInput = string.Empty;
        OnPropertyChanged(nameof(CalibrationDraft));
        OnPropertyChanged(nameof(CalibrationFrameItems));
        OnPropertyChanged(nameof(CalibrationPreviewImagePath));
        OnPropertyChanged(nameof(CalibrationPreviewImageSource));
        OnPropertyChanged(nameof(CanEditCalibration));
        OnPropertyChanged(nameof(CanApplyAndConfirmCalibration));
        return Task.CompletedTask;
    }

    public async Task ConfigureSizeTableAsync(Window owner)
    {
        if (_templateWorkspace is not ISizeTableTemplateCalibrationService calibrationService
            || _templateDraft is null || SelectedTemplatePage is not { IsReadOnly: false } selected
            || !IsSelectedDynamicSizeTable) return;
        await _templateDraftWrite.WaitAsync().ConfigureAwait(true);
        try
        {
            var session = _templateDraft;
            var draft = await _templateWorkspace.GetDraftAsync(session, CancellationToken.None).ConfigureAwait(true);
            var page = draft.Pages.Single(item => item.Page.PageId == selected.PageId);
            var dialog = new SizeTableCalibrationDialog(page) { Owner = owner };
            if (dialog.ShowDialog() != true || dialog.Calibration is null) return;
            await calibrationService.CalibrateSizeTableAsync(session, selected.PageId, dialog.Calibration, CancellationToken.None).ConfigureAwait(true);
            await LoadDraftPagesAsync(populateImportedFiles: false).ConfigureAwait(true);
            TemplateStatusMessage = "尺码表区域与样式已保存到草稿；保存模板组后用于新批次。";
            SelectedTemplatePage = TemplatePages.Single(item => item.PageId == selected.PageId);
        }
        catch (InvalidOperationException error) { TemplateStatusMessage = ToUserMessage(error.Message); }
        finally { _templateDraftWrite.Release(); }
    }

    private async Task SelectCalibrationAssetAsync()
    {
        if (_templateCalibration is null || _templateDraft is null || SelectedTemplatePage is null
            || string.IsNullOrWhiteSpace(CalibrationFrameIdInput))
        {
            return;
        }

        try
        {
            var frameId = CalibrationFrameIdInput;
            CalibrationDraft = await _templateCalibration.BindAssetAsync(
                _templateDraft,
                SelectedTemplatePage.PageId,
                frameId,
                CalibrationAssetIdInput,
                CancellationToken.None).ConfigureAwait(true);
            UpdateCalibrationInputs(CalibrationDraft);
            CalibrationAssetIdInput = "";
        }
        catch (InvalidOperationException exception)
        {
            TemplateStatusMessage = ToUserMessage(exception.Message);
        }
    }

    public async Task ClearCalibrationAssetAsync()
    {
        if (_templateCalibration is null || _templateDraft is null || SelectedTemplatePage is null
            || string.IsNullOrWhiteSpace(CalibrationFrameIdInput))
        {
            return;
        }

        try
        {
            CalibrationDraft = await _templateCalibration.ClearAssetAsync(
                _templateDraft,
                SelectedTemplatePage.PageId,
                CalibrationFrameIdInput,
                CancellationToken.None).ConfigureAwait(true);
            UpdateCalibrationInputs(CalibrationDraft);
            TemplateStatusMessage = "已移除当前图片框中的素材；源文件未删除，可用撤销恢复。";
        }
        catch (InvalidOperationException exception)
        {
            TemplateStatusMessage = ToUserMessage(exception.Message);
        }
    }

    private async Task ApplyCalibrationHistoryAsync(CalibrationUndoAction action)
    {
        if (_templateCalibration is null || _templateDraft is null || SelectedTemplatePage is null)
        {
            return;
        }

        CalibrationDraft = await _templateCalibration.ApplyHistoryAsync(
            _templateDraft,
            SelectedTemplatePage.PageId,
            action,
            CancellationToken.None).ConfigureAwait(true);
        UpdateCalibrationInputs(CalibrationDraft);
        TemplateStatusMessage = action == CalibrationUndoAction.Undo
            ? "已撤销最近一次校准操作。"
            : "已重做最近一次校准操作。";
    }

    private async Task ApplyCalibrationGeometryAsync()
    {
        if (_templateCalibration is null || _templateDraft is null || SelectedTemplatePage is null
            || string.IsNullOrWhiteSpace(CalibrationFrameIdInput))
        {
            return;
        }

        try
        {
            CalibrationDraft = await _templateCalibration.SetGeometryAsync(
                _templateDraft,
                SelectedTemplatePage.PageId,
                CalibrationFrameIdInput,
                CalibrationScaleInput,
                CalibrationOffsetXInput,
                CalibrationOffsetYInput,
                CancellationToken.None).ConfigureAwait(true);
            CalibrationDraft = await _templateCalibration.SetSelectionRequirementAsync(
                _templateDraft,
                SelectedTemplatePage.PageId,
                CalibrationFrameIdInput,
                CalibrationRequirementInput,
                CancellationToken.None).ConfigureAwait(true);
            UpdateCalibrationInputs(CalibrationDraft);
            TemplateStatusMessage = "布局与选图规则已保存。";
        }
        catch (InvalidOperationException exception)
        {
            TemplateStatusMessage = ToUserMessage(exception.Message);
        }
    }

    private Task ApplyAndConfirmCalibrationAsync() => IsReferenceLearningReviewMode
        ? ApplyReferenceLearningReviewAsync(ReferenceReviewDecision.Accepted)
        : ApplyCalibrationGeometryAsync();


}
