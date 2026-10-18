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
    public async Task ImportPsdFilesAsync(IReadOnlyList<string> paths)
    {
        if (_templateWorkspace is null || paths.Count == 0 || TemplateBusy)
        {
            return;
        }

        TemplateBusy = true;
        TemplateStatusMessage = "";
        try
        {
            await FlushRuleWorkbenchAsync().ConfigureAwait(true);
            _templateDraft ??= await _templateWorkspace.BeginCreateAsync(
                TemplateGroupName,
                CancellationToken.None).ConfigureAwait(true);
            NotifyTemplateDraftChanged();
            string? lastImportedPageId = null;
            foreach (var path in paths)
            {
                var pending = new PendingTemplateImport(
                    $"command_{Guid.NewGuid():N}",
                    $"resource_{Guid.NewGuid():N}",
                    path,
                    Path.GetFileNameWithoutExtension(path),
                    DuplicateResourceId: null);
                if (!await ImportPendingAsync(pending, TemplateDuplicateDecision.AutoReuseDraftDuplicates).ConfigureAwait(true))
                {
                    continue;
                }

                lastImportedPageId = ImportedTemplateFiles.LastOrDefault()?.PageId;
            }

            await LoadDraftPagesAsync(populateImportedFiles: false).ConfigureAwait(true);
            SelectedTemplatePage = TemplatePages.FirstOrDefault(page =>
                string.Equals(page.PageId, lastImportedPageId, StringComparison.Ordinal))
                ?? TemplatePages.LastOrDefault(page => page.IsAffected)
                ?? TemplatePages.FirstOrDefault();
            SetTemplateScreen(TemplateScreen.Canvas);
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException)
        {
            TemplateStatusMessage = ToUserMessage(exception.Message);
        }
        finally
        {
            TemplateBusy = false;
        }
    }

    public async Task ChoosePsdFolderAsync()
    {
        if (_folderPicker is null)
        {
            TemplateStatusMessage = "本机文件夹选择器不可用。";
            return;
        }

        var selected = await _folderPicker.PickFolderAsync(
            "选择包含模板 PSD 的文件夹（只读取直属 PSD）",
            null,
            CancellationToken.None).ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(selected))
        {
            return;
        }

        string[] paths;
        try
        {
            paths = Directory.EnumerateFiles(
                    Path.GetFullPath(selected),
                    "*.psd",
                    SearchOption.TopDirectoryOnly)
                .OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
                .ThenBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            TemplateStatusMessage = "无法读取所选文件夹，请检查权限后重试。";
            return;
        }

        if (paths.Length == 0)
        {
            TemplateStatusMessage = "所选文件夹中没有直属 PSD 文件；请确认 PSD 位于该文件夹内。";
            return;
        }

        await ImportPsdFilesAsync(paths).ConfigureAwait(true);
    }

    public async Task MoveTemplatePageAsync(
        TemplatePageRowViewModel source,
        TemplatePageRowViewModel target)
    {
        if (_templateWorkspace is null || _templateDraft is null || ReferenceEquals(source, target))
        {
            return;
        }

        var sourceIndex = TemplatePages.IndexOf(source);
        var targetIndex = TemplatePages.IndexOf(target);
        if (sourceIndex < 0 || targetIndex < 0)
        {
            return;
        }

        TemplatePages.Move(sourceIndex, targetIndex);
        UpdatePageOrderLabels();
        await SavePageOrderAsync().ConfigureAwait(true);
    }

    public async Task BindCalibrationAssetFileAsync(string sourcePath)
    {
        if (_templateCalibration is null || _templateDraft is null || SelectedTemplatePage is null
            || string.IsNullOrWhiteSpace(CalibrationFrameIdInput))
        {
            return;
        }

        try
        {
            CalibrationDraft = await _templateCalibration.BindReadOnlyAssetAsync(
                _templateDraft,
                SelectedTemplatePage.PageId,
                CalibrationFrameIdInput,
                sourcePath,
                CancellationToken.None).ConfigureAwait(true);
            UpdateCalibrationInputs(CalibrationDraft);
            TemplateStatusMessage = "代表款素材已只读登记并绑定到当前图片框。";
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            TemplateStatusMessage = ToUserMessage(exception.Message);
        }
    }

    public async Task BindCalibrationAssetIdAsync(string assetId)
    {
        CalibrationAssetIdInput = assetId;
        await SelectCalibrationAssetAsync().ConfigureAwait(true);
    }

    public Task CommitCalibrationGeometryAsync() => IsReferenceLearningReviewMode
        ? Task.CompletedTask
        : ApplyCalibrationGeometryAsync();

    public void SelectCalibrationFrame(string frameId)
    {
        if (IsReferenceLearningReviewMode)
        {
            SelectReferenceLearningFrame(frameId);
            return;
        }

        if (CalibrationDraft?.Frames.Any(frame => string.Equals(
                frame.FrameId,
                frameId,
                StringComparison.Ordinal)) != true)
        {
            return;
        }

        var frame = CalibrationDraft.Frames.First(candidate => string.Equals(
            candidate.FrameId,
            frameId,
            StringComparison.Ordinal));
        CalibrationDraft = CalibrationDraft with
        {
            Frames = CalibrationDraft.Frames.Select(candidate => candidate with
            {
                IsSelected = string.Equals(candidate.FrameId, frameId, StringComparison.Ordinal)
            }).ToArray(),
            SelectedFrameId = frameId,
            SelectedAssetId = frame.BoundAssetId,
            SelectedScale = frame.Scale,
            SelectedOffsetX = frame.OffsetX,
            SelectedOffsetY = frame.OffsetY,
            SelectionRequirement = frame.SelectionRequirement
        };
        CalibrationAssetIdInput = frame.BoundAssetId ?? string.Empty;
        UpdateCalibrationInputs(CalibrationDraft);
    }

    public void SetWindowWidth(double width)
    {
        if (TemplateWorkspaceState.SetWindowWidth(width))
        {
            NotifyReferenceLearningStateChanged();
        }
    }

    private void InitializeTemplates()
    {
        RefreshTemplatesCommand = new AsyncRelayCommand(LoadTemplateLibraryAsync, () => !TemplateBusy);
        NewTemplateCommand = new RelayCommand(BeginNewTemplate);
        OpenTemplateCommand = new AsyncRelayCommand<TemplateLibraryCardViewModel>(
            OpenTemplateAsync,
            HandleTemplateCommandException);
        RenameTemplateCommand = new AsyncRelayCommand<TemplateLibraryCardViewModel>(
            RenameTemplateAsync,
            HandleTemplateCommandException);
        DeleteTemplateFromLibraryCommand = new AsyncRelayCommand<TemplateLibraryCardViewModel>(
            DeleteTemplateFromLibraryAsync,
            HandleTemplateCommandException);
        EditTemplateGroupCommand = new AsyncRelayCommand(
            BeginTemplateGroupEditAsync,
            onError: HandleTemplateCommandException);
        EditTemplatePageCommand = new AsyncRelayCommand<TemplatePageDetailsRowViewModel>(
            BeginTemplatePageEditAsync,
            HandleTemplateCommandException);
        BackToTemplateLibraryCommand = new AsyncRelayCommand(BackToLibraryAsync);
        AddTemplatePagesCommand = new AsyncRelayCommand(BeginIncrementAsync, () => _templateDetails is not null);
        RemoveTemplatePageCommand = new AsyncRelayCommand<TemplatePageRowViewModel>(RemoveTemplatePageAsync);
        RemoveImportedPsdCommand = new AsyncRelayCommand<ImportedPsdRowViewModel>(RemoveImportedPsdAsync);
        AbandonTemplateDraftCommand = new AsyncRelayCommand(
            AbandonTemplateDraftAsync,
            onError: HandleTemplateCommandException);
        KeepExistingDuplicateCommand = new AsyncRelayCommand(
            () => ResolveDuplicateAsync(TemplateDuplicateDecision.ReuseExisting));
        KeepImportedDuplicateCommand = new AsyncRelayCommand(
            () => ResolveDuplicateAsync(TemplateDuplicateDecision.ContinueAsSeparate));
        CancelDuplicateCommand = new AsyncRelayCommand(
            () => ResolveDuplicateAsync(TemplateDuplicateDecision.Cancel),
            onError: HandleTemplateCommandException);
        ConfirmTemplateDialogCommand = new RelayCommand(
            () => CompleteTemplateDialog(TemplateDialogChoice.Primary),
            () => !TemplateDialogHasTextInput || !string.IsNullOrWhiteSpace(TemplateDialogInputText));
        SecondaryTemplateDialogCommand = new RelayCommand(
            () => CompleteTemplateDialog(TemplateDialogChoice.Secondary));
        TertiaryTemplateDialogCommand = new RelayCommand(
            () => CompleteTemplateDialog(TemplateDialogChoice.Tertiary));
        StartCalibrationCommand = new AsyncRelayCommand(StartCalibrationAsync, () => TemplatePages.Count > 0);
        SelectCalibrationAssetCommand = new AsyncRelayCommand(SelectCalibrationAssetAsync);
        ClearCalibrationAssetCommand = new AsyncRelayCommand(ClearCalibrationAssetAsync, () => CanClearCalibrationAsset);
        UndoCalibrationCommand = new AsyncRelayCommand(() => ApplyCalibrationHistoryAsync(CalibrationUndoAction.Undo));
        RedoCalibrationCommand = new AsyncRelayCommand(() => ApplyCalibrationHistoryAsync(CalibrationUndoAction.Redo));
        ApplyCalibrationGeometryCommand = new AsyncRelayCommand(ApplyCalibrationGeometryAsync);
        ApplyAndConfirmCalibrationCommand = new AsyncRelayCommand(
            ApplyAndConfirmCalibrationAsync,
            () => CanApplyAndConfirmCalibration);
        SaveTemplateCommand = new AsyncRelayCommand(SaveTemplateAsync, () => CanSaveTemplateCanvas);
        ExportTemplateCommand = new AsyncRelayCommand(ExportTemplateAsync);
        ImportTemplatePackageCommand = new AsyncRelayCommand(ImportTemplatePackageAsync);
        SaveTemplateAsCommand = new AsyncRelayCommand(SaveTemplateAsAsync);
        DeleteTemplateCommand = new AsyncRelayCommand(
            DeleteTemplateAsync,
            onError: HandleTemplateCommandException);
        ShowCalibrationPagesCommand = new RelayCommand(() => SetCalibrationNarrowPanel(TemplateWorkspaceNarrowPanel.Pages));
        ShowCalibrationCanvasCommand = new RelayCommand(() => SetCalibrationNarrowPanel(TemplateWorkspaceNarrowPanel.Canvas));
        ShowCalibrationInspectorCommand = new RelayCommand(() => SetCalibrationNarrowPanel(TemplateWorkspaceNarrowPanel.Inspector));
        ImportedTemplateFiles.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasImportedTemplateFiles));
            RaiseTemplateCommandCanExecuteChanged();
        };
        TemplatePages.CollectionChanged += (_, _) => RaiseTemplateCommandCanExecuteChanged();
        TemplatePages.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasCalibratableTemplatePages));
            OnPropertyChanged(nameof(CalibrationPages));
            OnPropertyChanged(nameof(CalibrationStartActionText));
            OnPropertyChanged(nameof(CalibrationModeHelpText));
            OnPropertyChanged(nameof(CanSaveTemplateCanvas));
        };
        TemplateGroups.CollectionChanged += (_, _) => OnPropertyChanged(nameof(BatchTemplateGroups));
    }

    internal void OnNavigationChanged()
    {
        if (_host.IsTemplateSectionVisible)
        {
            _ = LoadTemplateLibraryAsync();
        }
    }

    public async Task LoadTemplateLibraryAsync()
    {
        if (_templateWorkspace is null || TemplateBusy)
        {
            return;
        }

        TemplateBusy = true;
        TemplateStatusMessage = "";
        try
        {
            var groups = await _templateWorkspace.ListAsync(CancellationToken.None).ConfigureAwait(true);
            TemplateGroups.Clear();
            foreach (var group in groups)
            {
                TemplateGroups.Add(new TemplateLibraryCardViewModel(group));
            }

            OnPropertyChanged(nameof(HasTemplateGroups));
            OnPropertyChanged(nameof(IsTemplateLibraryEmpty));
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

    private void BeginNewTemplate()
    {
        ClosePageDialogs();
        RuleWorkbench?.Dispose(); RuleWorkbench = null;
        OnPropertyChanged(nameof(RuleWorkbench));
        _templateDraft = null;
        _templateDetails = null;
        TemplateGroupName = "新模板组";
        ImportedTemplateFiles.Clear();
        TemplatePages.Clear();
        TemplateStatusMessage = "";
        SetTemplateCalibrationMode(TemplateCalibrationMode.DragAndDrop);
        ResetReferenceLearningDraft();
        NotifyTemplateDraftChanged();
        SetTemplateScreen(TemplateScreen.Canvas);
    }

    private async Task OpenTemplateAsync(TemplateLibraryCardViewModel? item)
    {
        if (_templateWorkspace is null || item is null)
        {
            return;
        }

        TemplateBusy = true;
        try
        {
            _templateDetails = await _templateWorkspace.GetDetailsAsync(item.GroupId, CancellationToken.None)
                .ConfigureAwait(true);
            if (_templateDetails is null)
            {
                TemplateStatusMessage = "此模板组尚未提交可用修订，可从草稿继续。";
                return;
            }

            if (!_templateDetails.IsCommitted)
            {
                _templateDraft = await _templateWorkspace.ResumeDraftAsync(
                    item.GroupId,
                    CancellationToken.None).ConfigureAwait(true);
                TemplateGroupName = _templateDraft.GroupName;
                await LoadDraftPagesAsync(populateImportedFiles: true).ConfigureAwait(true);
                TemplateStatusMessage = "本地草稿已恢复，可继续页面设置和校准。";
                NotifyTemplateDraftChanged();
                SetTemplateScreen(TemplateScreen.Canvas);
                return;
            }

            OnPropertyChanged(nameof(TemplateDetailsTitle));
            OnPropertyChanged(nameof(TemplateDetailsMeta));
            OnPropertyChanged(nameof(TemplateDetailPages));
            OnPropertyChanged(nameof(CanIncrementTemplate));
            SetTemplateScreen(TemplateScreen.Details);
        }
        finally
        {
            TemplateBusy = false;
        }
    }

    private async Task RenameTemplateAsync(TemplateLibraryCardViewModel? item)
    {
        if (_templateWorkspace is null || item is null || TemplateBusy)
        {
            return;
        }

        var confirmation = await ShowTemplateRenameDialogAsync(item.Name).ConfigureAwait(true);
        if (confirmation != TemplateDialogChoice.Primary)
        {
            return;
        }

        var newName = TemplateDialogInputText.Trim();
        if (string.Equals(newName, item.Name, StringComparison.Ordinal))
        {
            TemplateStatusMessage = "模板组名称未改变。";
            return;
        }

        TemplateBusy = true;
        try
        {
            await _templateWorkspace.RenameTemplateGroupAsync(
                item.GroupId,
                newName,
                CancellationToken.None).ConfigureAwait(true);
        }
        finally
        {
            TemplateBusy = false;
        }

        await LoadTemplateLibraryAsync().ConfigureAwait(true);
        TemplateStatusMessage = $"模板组已重命名为“{newName}”。";
    }

    private Task DeleteTemplateFromLibraryAsync(TemplateLibraryCardViewModel? item) =>
        item is null
            ? Task.CompletedTask
            : DeleteTemplateGroupAsync(item.GroupId, item.Name);

    private async Task BackToLibraryAsync()
    {
        try
        {
            await FlushRuleWorkbenchAsync().ConfigureAwait(true);
            SetTemplateScreen(TemplateScreen.Library);
            await LoadTemplateLibraryAsync().ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            // Navigation commands are async-void ICommand boundaries. A local
            // store/cleanup failure must not close the WPF main window.
            TemplateStatusMessage = ToTemplateOperationError(exception);
        }
    }

    private async Task BeginIncrementAsync()
    {
        if (_templateWorkspace is null || _templateDetails is null)
        {
            return;
        }

        TemplateBusy = true;
        try
        {
            _templateDraft = await _templateWorkspace.BeginIncrementAsync(
                _templateDetails.GroupId,
                CancellationToken.None).ConfigureAwait(true);
            TemplateGroupName = _templateDraft.GroupName;
            ImportedTemplateFiles.Clear();
            TemplatePages.Clear();
            TemplateStatusMessage = "原页面保持只读；请选择一个或多个新增 PSD。";
            SetTemplateCalibrationMode(TemplateCalibrationMode.DragAndDrop);
            ResetReferenceLearningDraft();
            NotifyTemplateDraftChanged();
            SetTemplateScreen(TemplateScreen.Canvas);
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException)
        {
            TemplateStatusMessage = ToUserMessage(exception.Message);
        }
        finally
        {
            TemplateBusy = false;
        }
    }

    private async Task<bool> ImportPendingAsync(
        PendingTemplateImport pending,
        TemplateDuplicateDecision decision,
        TemplateDraftSession? draft = null)
    {
        draft ??= _templateDraft;
        if (_templateWorkspace is null || draft is null)
        {
            return false;
        }

        var dialogVersion = _pageDialogVersion;
        var result = await _templateWorkspace.ImportPageAsync(
            draft,
            pending.CommandId,
            pending.ResourceId,
            pending.SourcePath,
            pending.PageName,
            decision,
            decision == TemplateDuplicateDecision.ReuseExisting ? pending.DuplicateResourceId : null,
            CancellationToken.None).ConfigureAwait(true);
        if (result.Import.Status == TemplatePageImportStatus.DuplicateReviewRequired)
        {
            if (decision != TemplateDuplicateDecision.Cancel
                && (dialogVersion != _pageDialogVersion || !_host.IsTemplateSectionVisible))
            {
                // Cleanup must address the original staging copy, never a newly selected draft.
                await ImportPendingAsync(pending, TemplateDuplicateDecision.Cancel, draft).ConfigureAwait(true);
                return false;
            }
            if (decision == TemplateDuplicateDecision.Cancel)
            {
                TemplateStatusMessage = "重复 PSD 取消尚未完成，请恢复原草稿核对；不会重新确认导入。";
                return false;
            }
            _pendingTemplateImport = pending with
            {
                DuplicateResourceId = result.Import.DuplicateCandidates[0].ResourceId
            };
            IsDuplicateReviewVisible = true;
            OnPropertyChanged(nameof(DuplicateExistingLabel));
            OnPropertyChanged(nameof(DuplicateImportedLabel));
            return false;
        }

        if (result.Import.Status == TemplatePageImportStatus.Cancelled)
        {
            TemplateStatusMessage = "已取消重复 PSD，本次只清理应用内部副本。";
            return true;
        }

        if (!result.Import.IsSuccess || result.AddedPage is null)
        {
            TemplateStatusMessage = result.Import.Error is null
                ? "PSD 导入未完成，请重试。"
                : ToUserMessage(result.Import.Error.Code);
            return false;
        }

        if (result.AddedPage.IsAffected)
        {
            AddImportedFile(result.AddedPage);
        }

        TemplateStatusMessage = result.Import.Status == TemplatePageImportStatus.ReusedExisting
            ? "已保留模板库中的现有内部副本。"
            : "PSD 已复制到模板库，用户源文件保持只读。";
        return true;
    }

    private async Task ResolveDuplicateAsync(TemplateDuplicateDecision decision)
    {
        if (_pendingTemplateImport is null)
        {
            return;
        }

        TemplateBusy = true;
        try
        {
            var pending = _pendingTemplateImport;
            IsDuplicateReviewVisible = false;
            _pendingTemplateImport = null;
            await ImportPendingAsync(pending, decision).ConfigureAwait(true);
        }
        finally
        {
            TemplateBusy = false;
        }
    }

    private void AddImportedFile(TemplateDraftPage imported)
    {
        if (ImportedTemplateFiles.Any(item => string.Equals(
            item.ResourceId,
            imported.Resource.ResourceId,
            StringComparison.Ordinal)))
        {
            return;
        }

        ImportedTemplateFiles.Add(new ImportedPsdRowViewModel(imported));
        OnPropertyChanged(nameof(HasImportedTemplateFiles));
    }

    private async Task RemoveImportedPsdAsync(ImportedPsdRowViewModel? item)
    {
        if (_templateWorkspace is null || _templateDraft is null || item is null)
        {
            return;
        }

        var confirmation = await ShowTemplateDialogAsync(
            "移除这个 PSD？",
            $"将从当前模板草稿中移除“{item.FileName}”，它不会再参与后续页面设置和学习。",
            "为支持撤销，应用内部 PSD 副本暂时保留；不会删除用户源 PSD。",
            "移除 PSD",
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
                item.PageId,
                CancellationToken.None).ConfigureAwait(true);

            ImportedTemplateFiles.Remove(item);
            var page = TemplatePages.FirstOrDefault(candidate => string.Equals(
                candidate.PageId,
                item.PageId,
                StringComparison.Ordinal));
            if (page is not null)
            {
                var index = TemplatePages.IndexOf(page);
                TemplatePages.Remove(page);
                UpdatePageOrderLabels();
                SelectedTemplatePage = TemplatePages.ElementAtOrDefault(Math.Max(0, index - 1))
                    ?? TemplatePages.FirstOrDefault();
            }

            OnPropertyChanged(nameof(HasImportedTemplateFiles));
            TemplateStatusMessage = "PSD 已从草稿移除；内部副本暂保留以支持撤销，用户源 PSD 保持只读未改动。";
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

    private async Task LoadDraftPagesAsync(bool populateImportedFiles)
    {
        if (_templateWorkspace is null || _templateDraft is null)
        {
            return;
        }

        var draft = await _templateWorkspace.GetDraftAsync(_templateDraft, CancellationToken.None)
            .ConfigureAwait(true);
        if (populateImportedFiles)
        {
            ImportedTemplateFiles.Clear();
            foreach (var page in draft.Pages.Where(page => page.IsAffected))
            {
                AddImportedFile(page);
            }
        }

        TemplatePages.Clear();
        foreach (var page in draft.Pages.OrderBy(item => item.Page.Order))
        {
            TemplatePages.Add(new TemplatePageRowViewModel(
                page,
                OnTemplatePageChanged,
                OnTemplatePagePresentationChanged));
        }

        SelectedTemplatePage = TemplatePages.FirstOrDefault(page => page.IsAffected)
            ?? TemplatePages.FirstOrDefault();
        await RefreshRuleWorkbenchAsync().ConfigureAwait(true);
    }

    private void OnTemplatePageChanged(TemplatePageRowViewModel page)
    {
        OnPropertyChanged(nameof(HasCalibratableTemplatePages));
        OnPropertyChanged(nameof(CalibrationPages));
        OnPropertyChanged(nameof(CalibrationStartActionText));
        OnPropertyChanged(nameof(IsSelectedNormalComposition));
        OnPropertyChanged(nameof(IsSelectedDynamicSizeTable));
        OnPropertyChanged(nameof(CanSaveTemplateCanvas));
        if (!page.IsReadOnly)
        {
            _ = SavePageAsync(page);
        }
    }

    private async Task SavePageAsync(TemplatePageRowViewModel page)
    {
        if (_templateWorkspace is null || _templateDraft is null)
        {
            return;
        }

        await _templateDraftWrite.WaitAsync().ConfigureAwait(true);
        try
        {
            await _templateWorkspace.ConfigurePageAsync(
                _templateDraft,
                page.PageId,
                page.Name,
                page.ContentType,
                page.GenerationMode,
                page.VariantCapacity,
                page.Enabled,
                CancellationToken.None).ConfigureAwait(true);
            TemplateStatusMessage = "草稿已自动保存";
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

    private async Task SavePageOrderAsync()
    {
        if (_templateWorkspace is null || _templateDraft is null)
        {
            return;
        }

        await _templateDraftWrite.WaitAsync().ConfigureAwait(true);
        try
        {
            await _templateWorkspace.ReorderPagesAsync(
                _templateDraft,
                TemplatePages.Select(page => page.PageId).ToArray(),
                CancellationToken.None).ConfigureAwait(true);
            TemplateStatusMessage = "输出页码已更新，草稿已自动保存";
        }
        finally
        {
            _templateDraftWrite.Release();
        }
    }

}
