using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using Desktop.Application.Templates;
using Desktop.Application.Ai;
using Desktop.Application.SizeTables;
using Desktop.Application.Rendering;
using Desktop.Application.Diagnostics;

namespace Desktop.App;

public sealed partial class MainWindowViewModel : INotifyPropertyChanged, IDisposable
{
    private NavigationItemViewModel _selectedNavigationItem;
    private string _taskListStatusText = "选择任务可恢复已保存的输入、分析和结果；不会自动重新扫描或生成。";
    private readonly CancellationTokenSource _taskListLifetime = new();
    private bool _taskListInitialized;
    private bool _taskListLoading;
    private bool _hasTaskListSnapshot;
    private bool _taskListLoadFailed;

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
        ISizeTableEditingWorkflow? sizeTableEditingWorkflow = null,
        IReferenceLearningService? referenceLearningService = null,
        IReferenceLearningCommitService? referenceLearningCommitService = null,
        IReferenceLearningAiService? referenceLearningAiService = null,
        IAiCredentialManagementPort? aiCredentialManagement = null,
        IPhotoshopPairingPort? photoshopPairing = null,
        IRenderBatchWorkflow? rendering = null,
        IRenderBatchOutputReader? renderOutputs = null,
        IDiagnosticPackageExporter? diagnosticExporter = null,
        IPhotoshopEnvironmentProbe? photoshopEnvironment = null,
        IPhotoshopConnectionCheckPort? photoshopConnectionCheck = null,
        IPhotoshopHostSelectionPort? photoshopHostSelection = null)
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
        _aiAnalysisPage = new AiAnalysisViewModel(aiBatchAnalysisWorkflow, this);

        // Page view models are created before workflow initialization so page
        // state can own presentation data from its first notification onward.
        BatchInputPageViewModel = new BatchInputPageViewModel(this, folderPicker, batchInputWorkflow, templateWorkspace);
        InitializeSizeTableEditor(sizeTableEditingWorkflow, rendering);
        ResultReviewPageViewModel = new ResultReviewPageViewModel(this, rendering, renderOutputs);
        TemplateWorkspacePageViewModel = new TemplateWorkspacePageViewModel(this,
            new TemplateWorkspaceViewModel(templateWorkspace, templateCalibration, templatePackages,
                referenceLearningService, referenceLearningCommitService, referenceLearningAiService,
                folderPicker, this));
        TemplateWorkspace.PropertyChanged += TemplateWorkspace_PropertyChanged;
        SettingsPageViewModel = new SettingsPageViewModel(this, aiCredentialManagement, photoshopPairing,
            diagnosticExporter, photoshopEnvironment, photoshopConnectionCheck, photoshopHostSelection);

        RefreshTasksCommand = new AsyncRelayCommand(RefreshTasksAsync);
        RestoreTaskCommand = new AsyncRelayCommand<string>(RestoreTaskAsync);
        ShowTasksCommand = new RelayCommand(() =>
            SelectedNavigationItem = NavigationItems.First(item => item.Key == "tasks"));
        _aiAnalysisPage.PropertyChanged += BatchSwitchAiStateChanged;
        SizeTableEditorPage.PropertyChanged += BatchSwitchSizeEditorStateChanged;
        ResultReview.ReturnToPreflightCommand.CanExecuteChanged += BatchSwitchRenderStateChanged;
        InitializeAiBatchAnalysis();
        Credentials.Initialize();
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
            TemplateWorkspacePageViewModel.Coordinator.OnNavigationChanged();
            OnBatchNavigationChanged();
            if (value.Key == "tasks" || IsWorkbenchVisible)
            {
                RefreshTasksCommand.Execute(null);
            }
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

    public ObservableCollection<TaskRowViewModel> ActiveTasks { get; } = [];

    public ObservableCollection<TaskRowViewModel> RecentTasks { get; } = [];

    public bool HasActiveTasks => ActiveTasks.Count > 0;

    public bool IsTaskListLoading => _taskListLoading;

    public bool ShowEmptyRecentTasks => _hasTaskListSnapshot && !_taskListLoading
        && !_taskListLoadFailed && RecentTasks.Count == 0;

    public ObservableCollection<TaskRowViewModel> Tasks { get; } = [];

    public ICommand RefreshTasksCommand { get; }

    public ICommand RestoreTaskCommand { get; }

    public ICommand ShowTasksCommand { get; }

    public void InitializeTaskList()
    {
        if (_taskListInitialized || _taskListLifetime.IsCancellationRequested) return;
        _taskListInitialized = true;
        RefreshTasksCommand.Execute(null);
    }

    public string TaskListStatusText
    {
        get => _taskListStatusText;
        private set
        {
            _taskListStatusText = value;
            OnPropertyChanged();
        }
    }

    private async Task RefreshTasksAsync()
    {
        _taskListLoading = true;
        _taskListLoadFailed = false;
        NotifyTaskListState();
        TaskListStatusText = "正在读取本地批次记录…";
        try
        {
            var batches = await BatchInput.ListRecoverableAsync(_taskListLifetime.Token).ConfigureAwait(true);
            _taskListLifetime.Token.ThrowIfCancellationRequested();
            var rows = batches.OrderByDescending(item => item.LatestUpdatedAt)
                .ThenBy(item => item.BatchId, StringComparer.Ordinal)
                .Select(batch => TaskRowViewModel.FromSummary(batch,
                    TemplateGroups.FirstOrDefault(group => group.GroupId == batch.TemplateGroupId)?.Name
                        ?? batch.TemplateGroupId ?? "模板不可用")).ToArray();
            Tasks.Clear();
            foreach (var row in rows) Tasks.Add(row);
            ActiveTasks.Clear();
            foreach (var row in rows.Where(row => row.RequiresAttention).Take(3)) ActiveTasks.Add(row);
            RecentTasks.Clear();
            foreach (var row in rows.Take(3)) RecentTasks.Add(row);
            _hasTaskListSnapshot = true;

            TaskListStatusText = Tasks.Count == 0 ? "尚无可恢复的批次。"
                : $"共 {Tasks.Count} 个批次，按最近更新时间排序。状态来自本地任务记录；无生成记录时显示输入阶段，分析状态请查看详情。";
        }
        catch (OperationCanceledException) when (_taskListLifetime.IsCancellationRequested) { }
        catch (Exception)
        {
            _taskListLoadFailed = true;
            TaskListStatusText = "任务列表暂时无法读取；已有列表未更新。请检查本地存储后点击“刷新列表”重试。";
        }
        finally
        {
            _taskListLoading = false;
            NotifyTaskListState();
        }
    }

    private void NotifyTaskListState()
    {
        OnPropertyChanged(nameof(IsTaskListLoading));
        OnPropertyChanged(nameof(HasActiveTasks));
        OnPropertyChanged(nameof(ShowEmptyRecentTasks));
    }

    private async Task RestoreTaskAsync(string? batchId)
    {
        if (string.IsNullOrWhiteSpace(batchId) || !Tasks.Any(task => task.BatchId == batchId))
        {
            TaskListStatusText = "请选择列表中的具体批次；记录可能已变化，请刷新列表。";
            return;
        }

        if (BatchInput.BatchBusy || !((IBatchInputHost)this).CanSwitchBatch)
        {
            TaskListStatusText = "当前批次操作尚未收口，请等待或在当前任务安全暂停后再切换。";
            return;
        }

        TaskListStatusText = "正在恢复所选批次的本地记录；不会自动重新扫描或生成…";
        try
        {
            await BatchInput.RestoreBatchAsync(batchId).ConfigureAwait(true);
            SelectedNavigationItem = NavigationItems.First(item => item.Key == "workbench");
            TaskListStatusText = "已打开所选批次的已有分析和结果；未自动重新扫描或生成。";
        }
        catch (InvalidOperationException exception) when (exception.Message == "INPUT_BATCH_NOT_FOUND")
        {
            TaskListStatusText = "所选批次已不可用，请刷新列表；未改为恢复最新批次。";
        }
        catch (Exception)
        {
            TaskListStatusText = "所选批次暂时无法恢复，请检查本地记录并刷新列表后重试；未重新扫描或生成。";
        }
    }

    bool IBatchInputHost.CanSwitchBatch => !AiAnalysisPageViewModel.IsAiBusy
        && !SizeTableEditorPage.IsBusy && !SizeTableEditorPage.IsSizeTableEditorVisible
        // Unlike async pause/cancel commands, this predicate stays false even
        // while both control requests are themselves awaiting completion.
        && ResultReview.ReturnToPreflightCommand.CanExecute(null)
        && !(_aiBatchProjection is { } analysis && (analysis.IsBusy
            || analysis.Styles.SelectMany(style => style.Pages).Any(page => page.Status == AiPageUiStatus.Analysing)
            || analysis.ControlState == AiAnalysisSchedulingControlState.Running
                && (analysis.HasUnsettledPages || analysis.Status is AiBatchUiStatus.Waiting or AiBatchUiStatus.Analysing)));

    public BatchInputPageViewModel BatchInputPageViewModel { get; }

    public ResultReviewPageViewModel ResultReviewPageViewModel { get; }

    public TemplateWorkspacePageViewModel TemplateWorkspacePageViewModel { get; }

    public SettingsPageViewModel SettingsPageViewModel { get; }

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
        _taskListLifetime.Cancel();
        _aiAnalysisPage.PropertyChanged -= BatchSwitchAiStateChanged;
        SizeTableEditorPage.PropertyChanged -= BatchSwitchSizeEditorStateChanged;
        ResultReview.ReturnToPreflightCommand.CanExecuteChanged -= BatchSwitchRenderStateChanged;
        _aiAnalysisPage.PropertyChanged -= AiAnalysisPage_PropertyChanged;
        _aiAnalysisPage.Dispose();
        SettingsPageViewModel.Credentials.ClearAiCredentialInput();
        SizeTableEditorPage.Dispose();
        TemplateWorkspace.PropertyChanged -= TemplateWorkspace_PropertyChanged;
        BatchInputPageViewModel.Dispose();
        ResultReviewPageViewModel.Dispose();
        TemplateWorkspacePageViewModel.Dispose();
        SettingsPageViewModel.Dispose();
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
    string ActionText)
{
    public string? BatchId { get; init; }
    public string StatusDetail { get; init; } = "";
    public string StatusDescription { get; init; } = "";
    public bool RequiresAttention { get; init; }
    // Keep the internal batch identifier out of the customer-facing label;
    // the command parameter still carries it for safe task recovery.
    public string RestoreActionAccessibleName =>
        "查看或恢复任务 " + TaskName;

    public static TaskRowViewModel FromSummary(BatchRecoverySummary batch, string templateName)
    {
        ArgumentNullException.ThrowIfNull(batch);
        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(batch.InputDirectory));
        var (text, state, detail, description) = ProjectStatus(batch);
        return new TaskRowViewModel(string.IsNullOrWhiteSpace(name) ? batch.BatchId : name,
            batch.BatchId, templateName, text, state,
            batch.LatestUpdatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture),
            "查看/恢复")
        {
            BatchId = batch.BatchId, StatusDetail = detail, StatusDescription = description,
            RequiresAttention = state == "Recoverable" || batch.Task?.Status is "ReadyToRender" or "Paused"
        };
    }

    private static (string Text, string State, string Detail, string Description) ProjectStatus(BatchRecoverySummary batch)
    {
        var task = batch.Task;
        if (batch.TaskStateUnavailable || task is not null &&
            (task.TaskId != batch.BatchId || task.OutputCount < 0 || task.IssueCount < 0 || task.LastSequence < 0))
            return ("任务状态读取失败", "Recoverable", "请检查本地记录后刷新", "未重新扫描或生成；其他批次仍可查看。请检查本地存储后刷新列表。");

        if (task is null || task is { Status: "None", LastSequence: 0, OutputCount: 0, IssueCount: 0 })
        {
            var text = batch.TemplateGroupId is null ? "模板不可用" : batch.Status switch
            {
                BatchInputUiStatus.Draft => "输入草稿",
                BatchInputUiStatus.Scanning => "扫描记录待核对",
                BatchInputUiStatus.ScanBlocked => "扫描受阻",
                _ => "已扫描 · 查看详情"
            };
            return (text, batch.TemplateGroupId is null || batch.Status == BatchInputUiStatus.ScanBlocked
                    ? "Recoverable" : "Paused", "暂无生成任务记录",
                "此行仅显示输入阶段，不能据此判断分析是否完成；请打开详情。未自动分析或生成。");
        }

        var status = task.Status switch
        {
            "Completed" when task.OutputCount > 0 && task.IssueCount == 0 => ("已完成", "Completed"),
            "CompletedWithIssues" when task.OutputCount > 0 && task.IssueCount > 0 => ("完成但有问题", "Recoverable"),
            "Failed" => ("运行失败", "Recoverable"),
            "RecoveryRequired" => ("需要恢复", "Recoverable"),
            "SystemBlocked" => ("系统阻断", "Recoverable"),
            "UserActionRequired" => ("需要用户处理", "Recoverable"),
            "ReadyToRender" => ("待生成", "Paused"),
            "Rendering" => ("生成记录待核对", "Recoverable"),
            "Scanning" => ("扫描记录待核对", "Recoverable"),
            "Paused" => ("已暂停", "Paused"),
            "Cancelled" => ("已取消", "Paused"),
            "Draft" => ("任务待准备", "Paused"),
            _ => ("任务状态需核对", "Recoverable")
        };
        var counts = $"已登记 {task.OutputCount} 页 · 历史问题 {task.IssueCount} 项";
        var action = task.Status is "Rendering" or "Scanning"
            ? "这是已保存的执行记录，不代表进程仍在运行；请打开详情核对，不会自动重投未知执行。"
            : "请打开详情查看结果、原因和可用操作；列表不会自动分析、恢复执行或重新生成。";
        return (status.Item1, status.Item2, counts, $"{status.Item1}；{counts}。登记数不代表已重新核验输出文件。{action}");
    }
}
