using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Desktop.Application.Templates;
using Desktop.Application.Ai;
using Desktop.Application.SizeTables;

namespace Desktop.App;

public sealed partial class MainWindowViewModel : INotifyPropertyChanged, IDisposable
{
    private NavigationItemViewModel _selectedNavigationItem;

    public MainWindowViewModel(AppLaunchOptions launchOptions)
        : this(launchOptions, templateWorkspace: null)
    {
    }

    public MainWindowViewModel(
        AppLaunchOptions launchOptions,
        ITemplateWorkspaceService? templateWorkspace,
        ITemplateCalibrationService? templateCalibration = null,
        ITemplatePackageService? templatePackages = null,
        IFolderPicker? folderPicker = null,
        IBatchInputWorkflow? batchInputWorkflow = null,
        IAiBatchAnalysisWorkflow? aiBatchAnalysisWorkflow = null,
        ISizeTableEditingWorkflow? sizeTableEditingWorkflow = null)
    {
        NavigationItems =
        [
            new("workbench", "\uE710", "工作台", "WorkbenchNavigation"),
            new("templates", "\uE8A5", "模板", "TemplatesNavigation"),
            new("tasks", "\uE823", "任务", "TasksNavigation"),
            new("settings", "\uE713", "设置", "SettingsNavigation", IsFooterNavigation: true)
        ];
        _selectedNavigationItem = NavigationItems[0];
        _selectedNavigationItem.IsSelected = true;
        IsFixtureVisible = launchOptions.ShowRecoveryFixture;

        ActiveTasks =
        [
            new("秋季女装批次", "女装详情模板 · 120 款", "女装详情模板", "AI 分析 38/120", "Analyzing", "今天 14:18", "继续查看")
        ];
        RecentTasks =
        [
            new("A6015 春季上新", "女装详情模板 · 10 款", "女装详情模板", "已完成 10/10 款", "Completed", "今天 14:20", "查看结果"),
            new("童装夏季批次", "童装夏季模板 · 48 款", "童装夏季模板", "已暂停，可继续", "Paused", "昨天 18:42", "继续"),
            new("外置盘批次", "外置盘模板 · 25 款", "外置盘模板", "等待恢复", "Recoverable", "昨天 16:08", "查看")
        ];
        InitializeTemplates(templateWorkspace, templateCalibration, templatePackages);
        InitializeBatchInput(folderPicker, batchInputWorkflow);
        InitializeAiBatchAnalysis(aiBatchAnalysisWorkflow);
        InitializeSizeTableEditor(sizeTableEditingWorkflow);
    }

    public ObservableCollection<NavigationItemViewModel> NavigationItems { get; }

    public NavigationItemViewModel SelectedNavigationItem
    {
        get => _selectedNavigationItem;
        set
        {
            if (ReferenceEquals(_selectedNavigationItem, value) || value is null)
            {
                return;
            }

            _selectedNavigationItem.IsSelected = false;
            _selectedNavigationItem = value;
            _selectedNavigationItem.IsSelected = true;
            OnPropertyChanged();
            OnPropertyChanged(nameof(PageTitle));
            OnPropertyChanged(nameof(PageSubtitle));
            OnPropertyChanged(nameof(EmptyTitle));
            OnPropertyChanged(nameof(PrimaryActionText));
            OnPropertyChanged(nameof(HasPrimaryAction));
            OnPropertyChanged(nameof(PageRows));
            OnPropertyChanged(nameof(AdditionalSettingsRows));
            OnPropertyChanged(nameof(IsWorkbenchVisible));
            OnPropertyChanged(nameof(IsBatchInputVisible));
            OnPropertyChanged(nameof(IsAiAnalysisVisible));
            OnPropertyChanged(nameof(IsAiStyleReviewVisible));
            OnPropertyChanged(nameof(IsResultOverviewVisible));
            OnPropertyChanged(nameof(IsResultDetailVisible));
            OnPropertyChanged(nameof(IsSettingsVisible));
            OnPropertyChanged(nameof(IsTemplateSectionVisible));
            OnPropertyChanged(nameof(IsPlaceholderVisible));
            OnNavigationChanged();
            OnBatchNavigationChanged();
        }
    }

    public string PageTitle => SelectedNavigationItem.Key switch
    {
        "workbench" => "工作台",
        "templates" => "模板",
        "tasks" => "任务",
        "settings" => "设置",
        _ => "工作台"
    };

    public string PageSubtitle => SelectedNavigationItem.Key switch
    {
        "workbench" => "继续活动任务或开始新的批量生产",
        "templates" => "管理模板组和校准页面",
        "tasks" => "查看和继续批次任务",
        "settings" => "管理本机运行配置",
        _ => ""
    };

    public string EmptyTitle => SelectedNavigationItem.Key switch
    {
        "workbench" => "尚无任务",
        "templates" => "尚无模板组",
        "tasks" => "尚无任务",
        "settings" => "本机设置",
        _ => ""
    };

    public string PrimaryActionText => SelectedNavigationItem.Key switch
    {
        "workbench" => "新建批次",
        "templates" => "新建模板组",
        "tasks" => "",
        "settings" => "",
        _ => ""
    };

    public bool HasPrimaryAction => !string.IsNullOrEmpty(PrimaryActionText);

    public IReadOnlyList<string> PageRows => SelectedNavigationItem.Key == "settings"
        ? ["AI 服务", "Photoshop", "存储与清理", "隐私与诊断", "软件信息"]
        : [];

    public IReadOnlyList<string> AdditionalSettingsRows => SelectedNavigationItem.Key == "settings"
        ? ["Photoshop", "存储与清理", "隐私与诊断", "软件信息"]
        : [];

    public IReadOnlyList<TaskRowViewModel> ActiveTasks { get; }

    public IReadOnlyList<TaskRowViewModel> RecentTasks { get; }

    public bool IsWorkbenchVisible => SelectedNavigationItem.Key == "workbench"
        && !IsBatchInputVisible
        && !IsAiAnalysisVisible
        && !IsAiStyleReviewVisible
        && !IsResultOverviewVisible
        && !IsResultDetailVisible;

    public bool IsTemplateSectionVisible => SelectedNavigationItem.Key == "templates";

    public bool IsSettingsVisible => SelectedNavigationItem.Key == "settings";

    public bool IsPlaceholderVisible => !IsWorkbenchVisible
        && !IsBatchInputVisible
        && !IsAiAnalysisVisible
        && !IsAiStyleReviewVisible
        && !IsResultOverviewVisible
        && !IsResultDetailVisible
        && !IsSettingsVisible
        && !IsTemplateSectionVisible;

    public bool IsFixtureVisible { get; }

    public event PropertyChangedEventHandler? PropertyChanged;

    public void Dispose()
    {
        DisposeAiAutoRefresh();
        SizeTableEditor.PropertyChanged -= SizeTableEditor_PropertyChanged;
        _templateDraftWrite.Dispose();
        _batchDraftWrite.Dispose();
        _batchScanCancellation?.Dispose();
        GC.SuppressFinalize(this);
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public sealed class NavigationItemViewModel(
    string key,
    string glyph,
    string label,
    string automationId,
    bool IsFooterNavigation = false) : INotifyPropertyChanged
{
    private bool _isSelected;

    public string Key { get; } = key;

    public string Glyph { get; } = glyph;

    public string Label { get; } = label;

    public string AutomationId { get; } = automationId;

    public bool IsFooterNavigation { get; } = IsFooterNavigation;

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value)
            {
                return;
            }

            _isSelected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed record TaskRowViewModel(
    string TaskName,
    string Meta,
    string TemplateName,
    string StatusText,
    string StatusState,
    string UpdatedText,
    string ActionText);
