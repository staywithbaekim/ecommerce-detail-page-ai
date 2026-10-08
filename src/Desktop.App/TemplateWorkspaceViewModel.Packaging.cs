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
    private async Task SaveTemplateAsync()
    {
        if (RuleWorkbench is not null && !IsReferenceLearningReviewMode)
        {
            await LoadDraftPagesAsync(populateImportedFiles: true).ConfigureAwait(true);
            SetTemplateScreen(TemplateScreen.Canvas);
            TemplateStatusMessage = "请在工作台查看一次总览后确认保存。";
            return;
        }
        if (IsReferenceLearningReviewMode)
        {
            await CommitReferenceLearningAsync().ConfigureAwait(true);
            return;
        }

        if (_templateWorkspace is null || _templateDraft is null)
        {
            return;
        }

        TemplateBusy = true;
        try
        {
            _templateDetails = await _templateWorkspace.CommitDraftAsync(
                _templateDraft,
                CancellationToken.None).ConfigureAwait(true);
            TemplateStatusMessage = "模板组已原子保存；Photoshop 示例效果未运行。";
            OnPropertyChanged(nameof(TemplateDetailsTitle));
            OnPropertyChanged(nameof(TemplateDetailsMeta));
            OnPropertyChanged(nameof(TemplateDetailPages));
            OnPropertyChanged(nameof(CanIncrementTemplate));
            SetTemplateScreen(TemplateScreen.Details);
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

    private async Task ExportTemplateAsync()
    {
        if (_templatePackages is null || _templateDetails is null)
        {
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = "导出模板包",
            Filter = "模板包 (*.tplpkg)|*.tplpkg",
            FileName = _templateDetails.Name + ".tplpkg",
            AddExtension = true,
            DefaultExt = ".tplpkg"
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        await _templatePackages.ExportCurrentAsync(
            _templateDetails.GroupId,
            dialog.FileName,
            CancellationToken.None).ConfigureAwait(true);
            TemplateStatusMessage = "模板包已导出；不包含源照片、任务历史、API Key 或本机路径。";
    }

    private async Task ImportTemplatePackageAsync()
    {
        if (_templatePackages is null)
        {
            return;
        }

        var dialog = new OpenFileDialog
        {
            Title = "导入模板包",
            Filter = "模板包 (*.tplpkg)|*.tplpkg",
            Multiselect = false
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        TemplateBusy = true;
        try
        {
            _templateDetails = await _templatePackages.ImportAsNewGroupAsync(
                dialog.FileName,
                groupName: null,
                CancellationToken.None).ConfigureAwait(true);
            TemplateStatusMessage = "模板包已作为新的本地模板组导入。";
            OnPropertyChanged(nameof(TemplateDetailsTitle));
            OnPropertyChanged(nameof(TemplateDetailsMeta));
            OnPropertyChanged(nameof(TemplateDetailPages));
            OnPropertyChanged(nameof(CanIncrementTemplate));
            SetTemplateScreen(TemplateScreen.Details);
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

    private async Task SaveTemplateAsAsync()
    {
        if (_templateWorkspace is not null
            && _templateDraft is not null
            && _templateDetails is not null
            && IsFullTemplateEdit
            && IsManualCalibrationReviewMode)
        {
            TemplateBusy = true;
            try
            {
                _templateDetails = await _templateWorkspace.CommitDraftAsNewGroupAsync(
                    _templateDraft,
                    _templateDetails.Name + " 副本",
                    CancellationToken.None).ConfigureAwait(true);
                _templateDraft = null;
                TemplatePages.Clear();
                ImportedTemplateFiles.Clear();
                TemplateStatusMessage = "已将本次全量规则修改另存为独立模板组；原模板组保持不变。";
                OnPropertyChanged(nameof(TemplateDetailsTitle));
                OnPropertyChanged(nameof(TemplateDetailsMeta));
                OnPropertyChanged(nameof(CanIncrementTemplate));
                NotifyTemplateDraftChanged();
                SetTemplateScreen(TemplateScreen.Details);
            }
            catch (InvalidOperationException exception)
            {
                TemplateStatusMessage = ToUserMessage(exception.Message);
            }
            finally
            {
                TemplateBusy = false;
            }

            return;
        }

        if (_templatePackages is null || _templateDetails is null)
        {
            return;
        }

        TemplateBusy = true;
        try
        {
            _templateDetails = await _templatePackages.SaveAsNewGroupAsync(
                _templateDetails.GroupId,
                _templateDetails.Name + " 副本",
                CancellationToken.None).ConfigureAwait(true);
            TemplateStatusMessage = "已另存为独立模板组。";
            OnPropertyChanged(nameof(TemplateDetailsTitle));
            OnPropertyChanged(nameof(TemplateDetailsMeta));
            OnPropertyChanged(nameof(TemplateDetailPages));
            OnPropertyChanged(nameof(CanIncrementTemplate));
        }
        finally
        {
            TemplateBusy = false;
        }
    }

    private Task DeleteTemplateAsync() =>
        _templateDetails is null
            ? Task.CompletedTask
            : DeleteTemplateGroupAsync(_templateDetails.GroupId, _templateDetails.Name);

    private async Task DeleteTemplateGroupAsync(string groupId, string groupName)
    {
        if (_templateWorkspace is null || TemplateBusy)
        {
            return;
        }

        var confirmation = await ShowTemplateDialogAsync(
            "删除模板组？",
            $"将删除“{groupName}”在软件内部保存的 PSD 副本、页面规则和校准结果。此操作无法在应用内撤销。",
            "不会删除最初导入的 PSD、已生成结果或其他模板组。",
            "删除模板组",
            "取消",
            isDestructive: true).ConfigureAwait(true);
        if (confirmation != TemplateDialogChoice.Primary)
        {
            return;
        }

        TemplateBusy = true;
        IReadOnlyList<string> blockers;
        try
        {
            blockers = await _templateWorkspace.DeleteTemplateGroupAsync(
                groupId,
                CancellationToken.None).ConfigureAwait(true);
        }
        finally
        {
            TemplateBusy = false;
        }

        if (blockers.Count > 0)
        {
            TemplateStatusMessage = "存在未完成引用任务，不能删除：" + string.Join("、", blockers);
            await ShowTemplateDialogAsync(
                "模板组正在使用，无法删除",
                "以下未完成任务仍引用此模板组：\n" + string.Join("\n", blockers.Select(blocker => $"• {blocker}")),
                "请先完成或取消这些任务。用户源文件和模板副本均未改变。",
                "知道了",
                secondaryText: null).ConfigureAwait(true);
            return;
        }

        _templateDetails = null;
        SetTemplateScreen(TemplateScreen.Library);
        await LoadTemplateLibraryAsync().ConfigureAwait(true);
        TemplateStatusMessage = "模板组已删除；用户源 PSD 和正式输出未受影响。";
    }


}
