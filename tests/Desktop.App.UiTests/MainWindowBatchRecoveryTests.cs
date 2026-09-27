using Desktop.Application.Ai;
using Desktop.Application.Rendering;
using Desktop.Application.Tasks;

namespace Desktop.App.UiTests;

public sealed class MainWindowBatchRecoveryTests
{
    [Fact]
    public void WorkbenchInitializesRealRecentAndAttentionRowsOnceWithoutResumingAnything()
    {
        var input = new InputWorkflow
        {
            Summaries = [Summary("batch_A", "Completed", 4), Summary("batch_B", "RecoveryRequired", 3),
                Summary("batch_C", "Cancelled", 2), Summary("batch_D", "ReadyToRender", 1)]
        };
        var ai = new AiWorkflow();
        var render = new RenderWorkflow(RenderBatchPageStatus.Committed);
        using var shell = CreateShell(input, ai, render);
        Assert.False(shell.ShowEmptyRecentTasks);

        shell.InitializeTaskList();
        shell.InitializeTaskList();

        Assert.Equal(1, input.ListReads);
        Assert.True(shell.IsWorkbenchVisible);
        Assert.Equal(4, shell.Tasks.Count);
        Assert.Collection(shell.RecentTasks,
            row => Assert.Equal("batch_A", row.BatchId), row => Assert.Equal("batch_B", row.BatchId),
            row => Assert.Equal("batch_C", row.BatchId));
        Assert.Collection(shell.ActiveTasks,
            row => Assert.Equal("batch_B", row.BatchId), row => Assert.Equal("batch_D", row.BatchId));
        Assert.Same(shell.Tasks[0], shell.RecentTasks[0]);
        Assert.True(shell.HasActiveTasks);
        Assert.False(shell.ShowEmptyRecentTasks);
        Assert.Empty(ai.Reads);
        Assert.Empty(render.Reads);
        Assert.Empty(input.Restored);
        Assert.Null(shell.BatchInputPageViewModel.Coordinator.CurrentBatchId);

        shell.RestoreTaskCommand.Execute(shell.RecentTasks[0].BatchId);

        Assert.Equal("batch_A", Assert.Single(input.Restored));
        Assert.Equal("batch_A", Assert.Single(ai.Reads));
        Assert.True(shell.IsResultOverviewVisible);
        Assert.Equal(0, input.Mutations);
        Assert.Equal(0, ai.Mutations);
        Assert.Equal(0, render.Mutations);
    }

    [Fact]
    public void WorkbenchFailureNeverLooksEmptyAndRetrySharesRowsWithAllTasks()
    {
        var input = new InputWorkflow { FailList = true };
        using var shell = CreateShell(input, new AiWorkflow(), new RenderWorkflow(RenderBatchPageStatus.Committed));
        shell.InitializeTaskList();
        Assert.False(shell.ShowEmptyRecentTasks);
        Assert.Contains("暂时无法读取", shell.TaskListStatusText, StringComparison.Ordinal);
        input.FailList = false;
        input.Empty = true;
        shell.RefreshTasksCommand.Execute(null);
        Assert.True(shell.ShowEmptyRecentTasks);
        Assert.False(shell.HasActiveTasks);
        input.Empty = false;
        input.Summaries = [Summary("batch_A", "Completed", 3), Summary("batch_B", "RecoveryRequired", 2)];
        shell.ShowTasksCommand.Execute(null);
        Assert.Equal("tasks", shell.SelectedNavigationItem.Key);
        var previous = shell.RecentTasks[0];
        var attention = Assert.Single(shell.ActiveTasks);
        input.FailList = true;
        shell.SelectedNavigationItem = shell.NavigationItems.Single(item => item.Key == "workbench");
        Assert.True(shell.IsWorkbenchVisible);
        Assert.Same(previous, shell.RecentTasks[0]);
        Assert.Same(attention, Assert.Single(shell.ActiveTasks));
        Assert.False(shell.ShowEmptyRecentTasks);
        Assert.Contains("已有列表未更新", shell.TaskListStatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WorkbenchLoadingIsSingleFlightAndClosingPreventsLatePublication()
    {
        var gate = new TaskCompletionSource<IReadOnlyList<BatchRecoverySummary>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var input = new InputWorkflow { ListGate = gate.Task };
        var shell = CreateShell(input, new AiWorkflow(), new RenderWorkflow(RenderBatchPageStatus.Committed));
        var settled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        shell.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainWindowViewModel.IsTaskListLoading) && !shell.IsTaskListLoading)
                settled.TrySetResult();
        };
        try
        {
            shell.InitializeTaskList();
            shell.InitializeTaskList();
            shell.RefreshTasksCommand.Execute(null);
            Assert.True(shell.IsTaskListLoading);
            Assert.False(shell.ShowEmptyRecentTasks);
            Assert.Equal(1, input.ListReads);
        }
        finally { shell.Dispose(); }
        await settled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        gate.SetResult([Summary("batch_A", "Completed", 1)]);
        Assert.Empty(shell.Tasks);
        Assert.Empty(shell.RecentTasks);
        Assert.False(shell.ShowEmptyRecentTasks);
    }

    private static BatchRecoverySummary Summary(string batchId, string status, int hours) => new(
        batchId, "group", @"C:\same-input", BatchInputUiStatus.ScanReady, DateTimeOffset.UnixEpoch)
    {
        Task = new(batchId, status, 10, status == "Completed" ? 7 : 0, 0, false)
        { UpdatedAt = DateTimeOffset.UnixEpoch.AddHours(hours) }
    };

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CompletedBatchCanOpenANewDraftFromTasksAndStillRestoreItsExactId(bool fromDetails)
    {
        var input = new InputWorkflow();
        var ai = new AiWorkflow();
        var render = new RenderWorkflow(RenderBatchPageStatus.Committed);
        using var shell = CreateShell(input, ai, render);
        var tasks = shell.NavigationItems.Single(item => item.Key == "tasks");
        var workbench = shell.NavigationItems.Single(item => item.Key == "workbench");
        shell.SelectedNavigationItem = tasks;
        shell.RestoreTaskCommand.Execute("batch_A");
        if (fromDetails)
            shell.ResultReviewPageViewModel.OpenResultPageCommand.Execute(shell.ResultReviewPageViewModel.State.AllResultPages[0]);
        var original = shell.ResultReviewPageViewModel.State.RenderJournal;

        shell.SelectedNavigationItem = tasks;
        shell.SelectedNavigationItem = workbench;
        Assert.Equal(fromDetails, shell.IsResultDetailVisible);
        Assert.Equal(!fromDetails, shell.IsResultOverviewVisible);
        shell.SelectedNavigationItem = tasks;
        Assert.True(shell.OpenBatchInputCommand.CanExecute(null));
        shell.OpenBatchInputCommand.Execute(null);

        Assert.Equal("workbench", shell.SelectedNavigationItem.Key);
        Assert.True(shell.IsBatchInputVisible);
        var draft = shell.BatchInputPageViewModel.Coordinator;
        Assert.Null(draft.CurrentBatchId);
        Assert.Empty(draft.BatchInputDirectory);
        Assert.Empty(draft.BatchOutputDirectory);
        Assert.Equal(BatchInputUiStatus.Draft, draft.State.Status);
        Assert.Same(original, shell.ResultReviewPageViewModel.State.RenderJournal);
        Assert.Equal("batch_A", Assert.Single(input.Restored));
        Assert.Equal("batch_A", Assert.Single(ai.Reads));

        shell.SelectedNavigationItem = tasks;
        shell.RestoreTaskCommand.Execute("batch_A");
        Assert.Equal("batch_A", draft.CurrentBatchId);
        Assert.True(shell.IsResultOverviewVisible);
        Assert.Equal(RenderBatchPageStatus.Committed, Assert.Single(shell.ResultReviewPageViewModel.State.RenderJournal!.Pages).Status);
        Assert.Equal(2, input.Restored.Count);
        Assert.All(input.Restored, batchId => Assert.Equal("batch_A", batchId));
        Assert.Equal(0, input.LatestReads);
        Assert.Equal(0, input.Mutations);
        Assert.Equal(0, ai.Mutations);
        Assert.Equal(0, render.Mutations);
    }

    [Fact]
    public async Task AiPreflightCanReachANewDraftThroughTasksWithoutResendingAnalysis()
    {
        var input = new InputWorkflow();
        var ai = new AiWorkflow();
        var render = new RenderWorkflow(RenderBatchPageStatus.Committed);
        using var shell = CreateShell(input, ai, render);
        shell.BatchInputPageViewModel.Coordinator.CurrentBatchId = "batch_A";
        await shell.AiAnalysisPageViewModel.RestoreAiAnalysisAsync("batch_A");
        var tasks = shell.NavigationItems.Single(item => item.Key == "tasks");
        shell.SelectedNavigationItem = tasks;
        shell.SelectedNavigationItem = shell.NavigationItems.Single(item => item.Key == "workbench");
        Assert.True(shell.IsAiAnalysisVisible);
        shell.SelectedNavigationItem = tasks;

        shell.OpenBatchInputCommand.Execute(null);

        Assert.True(shell.IsBatchInputVisible);
        Assert.Null(shell.BatchInputPageViewModel.Coordinator.CurrentBatchId);
        Assert.Equal("batch_A", Assert.Single(ai.Reads));
        Assert.Empty(input.Restored);
        Assert.Empty(render.Reads);
        Assert.Equal(0, input.Mutations);
        Assert.Equal(0, ai.Mutations);
        Assert.Equal(0, render.Mutations);
    }

    [Theory]
    [InlineData(RenderBatchPageStatus.Failed)]
    [InlineData(RenderBatchPageStatus.Unknown)]
    [InlineData(RenderBatchPageStatus.Committed)]
    public void OlderBatchRemainsReachableWithoutRescanningAiRequestsOrRendering(RenderBatchPageStatus status)
    {
        var input = new InputWorkflow();
        var ai = new AiWorkflow();
        var render = new RenderWorkflow(status);
        using var shell = CreateShell(input, ai, render);

        shell.SelectedNavigationItem = shell.NavigationItems.Single(item => item.Key == "tasks");

        Assert.Collection(shell.Tasks,
            item => Assert.Equal("batch_B", item.BatchId),
            item => Assert.Equal("batch_A", item.BatchId));
        Assert.All(shell.Tasks, item => Assert.Equal("已扫描 · 查看详情", item.StatusText));
        Assert.Contains("无生成记录时显示输入阶段", shell.TaskListStatusText, StringComparison.Ordinal);
        Assert.Empty(ai.Reads);
        Assert.Empty(render.Reads);
        Assert.Empty(input.Restored);

        shell.RestoreTaskCommand.Execute("batch_A");

        Assert.Equal("batch_A", shell.BatchInputPageViewModel.Coordinator.CurrentBatchId);
        Assert.Equal("batch_A", Assert.Single(input.Restored));
        Assert.Equal("batch_A", Assert.Single(ai.Reads));
        Assert.True(shell.IsResultOverviewVisible);
        Assert.All(render.Reads, id => Assert.Equal("batch_A", id));
        Assert.Same(render.Journal, shell.ResultReviewPageViewModel.State.RenderJournal);
        Assert.Equal(status, Assert.Single(shell.ResultReviewPageViewModel.State.RenderJournal!.Pages).Status);
        Assert.Equal(0, input.Mutations);
        Assert.Equal(0, input.LatestReads);
        Assert.Equal(0, ai.Mutations);
        Assert.Equal(0, render.Mutations);
    }

    [Fact]
    public void MissingSelectionAndListFailureDoNotFallbackOrDiscardExistingRows()
    {
        var input = new InputWorkflow();
        using var shell = CreateShell(input, new AiWorkflow(), new RenderWorkflow(RenderBatchPageStatus.Unknown));
        shell.SelectedNavigationItem = shell.NavigationItems.Single(item => item.Key == "tasks");
        input.Missing = true;

        shell.RestoreTaskCommand.Execute("batch_A");

        Assert.Equal("tasks", shell.SelectedNavigationItem.Key);
        Assert.Contains("未改为恢复最新批次", shell.TaskListStatusText, StringComparison.Ordinal);
        Assert.Null(shell.BatchInputPageViewModel.Coordinator.CurrentBatchId);
        Assert.Equal(0, input.LatestReads);
        input.FailList = true;
        shell.RefreshTasksCommand.Execute(null);
        Assert.Equal(2, shell.Tasks.Count);
        Assert.Contains("暂时无法读取", shell.TaskListStatusText, StringComparison.Ordinal);
        input.FailList = false;
        input.Empty = true;
        shell.RefreshTasksCommand.Execute(null);
        Assert.Empty(shell.Tasks);
        Assert.Contains("尚无", shell.TaskListStatusText, StringComparison.Ordinal);
    }

    [Fact]
    public void ScanBusyBlocksSelectionBeforeAnyRecoveryRead()
    {
        var input = new InputWorkflow();
        var ai = new AiWorkflow();
        var render = new RenderWorkflow(RenderBatchPageStatus.Unknown);
        using var shell = CreateShell(input, ai, render);
        shell.SelectedNavigationItem = shell.NavigationItems.Single(item => item.Key == "tasks");
        shell.BatchInputPageViewModel.Coordinator.CurrentBatchId = "batch_B";
        var changes = 0;
        shell.OpenBatchInputCommand.CanExecuteChanged += (_, _) => changes++;
        shell.BatchInputPageViewModel.State.Busy = true;

        shell.RestoreTaskCommand.Execute("batch_A");

        AssertBlocked(shell, input);
        Assert.Empty(ai.Reads);
        Assert.Empty(render.Reads);
        Assert.True(changes > 0);
        shell.BatchInputPageViewModel.State.Busy = false;
        Assert.True(shell.OpenBatchInputCommand.CanExecute(null));
    }

    [Theory]
    [InlineData(AiBatchUiStatus.Analysing)]
    [InlineData(AiBatchUiStatus.Waiting)]
    public async Task AiBackgroundBusyBlocksSelectionEvenWhenNoUiCommandIsBusy(AiBatchUiStatus status)
    {
        var input = new InputWorkflow();
        var ai = new AiWorkflow { Status = status };
        using var shell = CreateShell(input, ai, new RenderWorkflow(RenderBatchPageStatus.Unknown));
        shell.BatchInputPageViewModel.Coordinator.CurrentBatchId = "batch_B";
        var changes = 0;
        shell.OpenBatchInputCommand.CanExecuteChanged += (_, _) => changes++;
        await shell.AiAnalysisPageViewModel.RestoreAiAnalysisAsync("batch_B");
        Assert.False(shell.AiAnalysisPageViewModel.IsAiBusy);
        shell.SelectedNavigationItem = shell.NavigationItems.Single(item => item.Key == "tasks");

        shell.RestoreTaskCommand.Execute("batch_A");

        AssertBlocked(shell, input);
        Assert.Equal("batch_B", Assert.Single(ai.Reads));
        Assert.True(changes > 0);
        ai.Status = AiBatchUiStatus.ReviewRequired;
        await shell.AiAnalysisPageViewModel.RestoreAiAnalysisAsync("batch_B");
        Assert.True(shell.OpenBatchInputCommand.CanExecute(null));
    }

    [Fact]
    public async Task ActiveAiCommandBlocksNewDraftBeforeItsProjectionArrives()
    {
        var gate = new TaskCompletionSource<AiBatchAnalysisProjection>(TaskCreationOptions.RunContinuationsAsynchronously);
        var input = new InputWorkflow();
        var ai = new AiWorkflow { StartGate = gate.Task };
        var render = new RenderWorkflow(RenderBatchPageStatus.Committed);
        using var shell = CreateShell(input, ai, render);
        shell.BatchInputPageViewModel.Coordinator.CurrentBatchId = "batch_B";
        var running = shell.AiAnalysisPageViewModel.StartAiAnalysisAsync("batch_B");
        try
        {
            shell.SelectedNavigationItem = shell.NavigationItems.Single(item => item.Key == "tasks");
            Assert.True(shell.AiAnalysisPageViewModel.IsAiBusy);
            Assert.False(shell.OpenBatchInputCommand.CanExecute(null));
            shell.OpenBatchInputCommand.Execute(null);
            Assert.Equal("batch_B", shell.BatchInputPageViewModel.Coordinator.CurrentBatchId);
            Assert.Equal("tasks", shell.SelectedNavigationItem.Key);
            Assert.Equal(0, input.Mutations);
            Assert.Equal(1, ai.Mutations);
            Assert.Equal(0, render.Mutations);
        }
        finally
        {
            gate.SetResult((await ai.RefreshAsync("batch_B", default))!);
            await running.WaitAsync(TimeSpan.FromSeconds(5));
        }
        Assert.True(shell.OpenBatchInputCommand.CanExecute(null));
    }

    [Fact]
    public async Task ActiveRenderingBlocksSelectionUntilItsOperationSettles()
    {
        var gate = new TaskCompletionSource<RenderBatchJournal>(TaskCreationOptions.RunContinuationsAsynchronously);
        var input = new InputWorkflow();
        var render = new RenderWorkflow(RenderBatchPageStatus.Planned) { PrepareGate = gate.Task };
        using var shell = CreateShell(input, new AiWorkflow(), render);
        shell.BatchInputPageViewModel.Coordinator.CurrentBatchId = "batch_B";
        await shell.ResultReviewPageViewModel.Coordinator.TryRestoreRenderingAsync("batch_B", null);
        var changes = 0;
        shell.OpenBatchInputCommand.CanExecuteChanged += (_, _) => changes++;
        var running = shell.ResultReviewPageViewModel.Coordinator.StartRenderingAsync();
        try
        {
            shell.SelectedNavigationItem = shell.NavigationItems.Single(item => item.Key == "tasks");
            shell.RestoreTaskCommand.Execute("batch_A");
            AssertBlocked(shell, input);
            Assert.True(changes > 0);
        }
        finally
        {
            gate.SetResult(render.Journal);
            await running.WaitAsync(TimeSpan.FromSeconds(5));
        }
        Assert.True(shell.OpenBatchInputCommand.CanExecute(null));
    }

    [Fact]
    public void ProjectionFailureKeepsAllPreviousTaskRows()
    {
        var input = new InputWorkflow();
        using var shell = CreateShell(input, new AiWorkflow(), new RenderWorkflow(RenderBatchPageStatus.Committed));
        shell.SelectedNavigationItem = shell.NavigationItems.Single(item => item.Key == "tasks");
        var before = shell.Tasks.ToArray();
        Assert.Equal(2, before.Length);
        input.InvalidSummary = true;

        shell.RefreshTasksCommand.Execute(null);

        Assert.Equal(before.Length, shell.Tasks.Count);
        for (var i = 0; i < before.Length; i++) Assert.Same(before[i], shell.Tasks[i]);
        Assert.Contains("已有列表未更新", shell.TaskListStatusText, StringComparison.Ordinal);
    }

    private static void AssertBlocked(MainWindowViewModel shell, InputWorkflow input)
    {
        Assert.False(shell.OpenBatchInputCommand.CanExecute(null));
        shell.OpenBatchInputCommand.Execute(null);
        Assert.Equal("batch_B", shell.BatchInputPageViewModel.Coordinator.CurrentBatchId);
        Assert.Equal("tasks", shell.SelectedNavigationItem.Key);
        Assert.Contains("尚未收口", shell.TaskListStatusText, StringComparison.Ordinal);
        Assert.Empty(input.Restored);
    }

    private static MainWindowViewModel CreateShell(InputWorkflow input, AiWorkflow ai, RenderWorkflow render) =>
        new(AppLaunchOptions.Default, templateWorkspace: null, batchInputWorkflow: input,
            aiBatchAnalysisWorkflow: ai, rendering: render);

    private sealed class InputWorkflow : IBatchInputWorkflow
    {
        public List<string> Restored { get; } = [];
        public int Mutations { get; private set; }
        public int LatestReads { get; private set; }
        public bool Missing { get; set; }
        public bool FailList { get; set; }
        public bool Empty { get; set; }
        public bool InvalidSummary { get; set; }
        public int ListReads { get; private set; }
        public IReadOnlyList<BatchRecoverySummary>? Summaries { get; set; }
        public Task<IReadOnlyList<BatchRecoverySummary>>? ListGate { get; init; }
        public Task<IReadOnlyList<BatchRecoverySummary>> ListRecoverableAsync(CancellationToken token)
        {
            ListReads++;
            if (ListGate is not null) return ListGate.WaitAsync(token);
            if (FailList) throw new IOException("test");
            return Task.FromResult<IReadOnlyList<BatchRecoverySummary>>(InvalidSummary ? [null!] : Empty ? [] : Summaries ??
                [new("batch_A", "group", @"C:\same-input", BatchInputUiStatus.ScanReady, DateTimeOffset.UnixEpoch),
                 new("batch_B", "group", @"C:\same-input", BatchInputUiStatus.ScanReady, DateTimeOffset.UnixEpoch.AddHours(1))]);
        }
        public Task<BatchInputProjection?> RestoreBatchAsync(string batchId, CancellationToken token)
        {
            Restored.Add(batchId);
            return Task.FromResult<BatchInputProjection?>(Missing ? null : new(batchId, "group",
                BatchInputUiStatus.ScanReady, @"C:\same-input", @"C:\output", ["page"],
                BatchSelectionMode.Automatic, false, "ready", "ready"));
        }
        public Task<BatchInputProjection?> RestoreLatestDraftAsync(CancellationToken token)
        { LatestReads++; throw new InvalidOperationException("Latest must not replace selection"); }
        public Task<BatchInputProjection> ScanAsync(BatchInputSubmission submission, CancellationToken token)
        { Mutations++; throw new InvalidOperationException("No scan on recovery"); }
        public Task<BatchInputProjection> SaveDraftAsync(BatchInputSubmission submission, CancellationToken token)
        { Mutations++; throw new InvalidOperationException("No autosave on recovery"); }
    }

    private sealed class RenderWorkflow : IRenderBatchWorkflow
    {
        public RenderWorkflow(RenderBatchPageStatus status)
        {
            var checkpoint = new TaskCheckpoint("batch_A", "style", "page", "render_output", 1,
                "sha256:" + new string('a', 64), "execution", OutputCommitState.Committed, DateTimeOffset.UnixEpoch);
            var commit = status == RenderBatchPageStatus.Committed
                ? new RenderPageCommitCheckpoint(checkpoint, "render:test", "outputs/manifest.json", 1, new string('b', 64)) : null;
            Journal = new(1, "batch_A", 2, status == RenderBatchPageStatus.Committed
                ? RenderBatchStatus.Completed : RenderBatchStatus.RecoveryRequired, false, false,
                [new("style", "page", null, 1, "{\"plan_digest\":\"sha256:test\"}", status,
                    "SAVED", "execution", commit, "READY")]);
        }
        public RenderBatchJournal Journal { get; private set; }
        public List<string> Reads { get; } = [];
        public int Mutations { get; private set; }
        public Task<RenderBatchJournal>? PrepareGate { get; init; }
        public Task<RenderBatchJournal?> LoadAsync(string batchId, CancellationToken token)
        { Reads.Add(batchId); Journal = Journal with { BatchId = batchId }; return Task.FromResult<RenderBatchJournal?>(Journal); }
        public Task<RenderBatchJournal> PrepareAsync(AiRenderPlanPreparation preparation, CancellationToken token)
        { Mutations++; return PrepareGate ?? throw new InvalidOperationException("No prepare on selection"); }
        public Task<RenderBatchJournal> RunAsync(string batchId, CancellationToken token)
        { Mutations++; return Task.FromResult(Journal); }
        public Task<RenderBatchJournal> PrepareRegenerationAsync(string batchId, string styleId, string pageId,
            string? outputVariantId, int expectedAttempt, CancellationToken token)
        { Mutations++; throw new InvalidOperationException("No regeneration on selection"); }
        public Task RequestPauseAsync(string batchId, CancellationToken token) => throw new NotSupportedException();
        public Task RequestCancelAsync(string batchId, CancellationToken token) => throw new NotSupportedException();
    }

    private sealed class AiWorkflow : IAiBatchAnalysisWorkflow
    {
        public AiBatchUiStatus Status { get; set; } = AiBatchUiStatus.ReviewRequired;
        public Task<AiBatchAnalysisProjection>? StartGate { get; init; }
        public List<string> Reads { get; } = [];
        public int Mutations { get; private set; }
        public Task<AiBatchAnalysisProjection?> RefreshAsync(string batchId, CancellationToken token)
        {
            Reads.Add(batchId);
            return Task.FromResult<AiBatchAnalysisProjection?>(new(batchId, Status, "saved", "review", 1, 1, 0, 1,
                [], AiAnalysisSchedulingControlState.Running, null));
        }
        private Task<AiBatchAnalysisProjection> Unexpected()
        { Mutations++; throw new InvalidOperationException("No AI request on selection"); }
        public Task<AiBatchAnalysisProjection> StartAsync(string batchId, CancellationToken token)
        {
            if (StartGate is null) return Unexpected();
            Mutations++; return StartGate;
        }
        public Task<AiBatchAnalysisProjection> PauseAsync(string batchId, CancellationToken token) => Unexpected();
        public Task<AiBatchAnalysisProjection> ResumeAsync(string batchId, CancellationToken token) => Unexpected();
        public Task<AiBatchAnalysisProjection> CancelAsync(string batchId, CancellationToken token) => Unexpected();
        public Task<AiBatchAnalysisProjection> WaitForNetworkAsync(string batchId, CancellationToken token) => Unexpected();
        public Task<AiBatchAnalysisProjection> LockSelectionAsync(string batchId, string styleId, string requirementId,
            string assetId, CancellationToken token) => Unexpected();
        public Task<AiBatchAnalysisProjection> SelectSizeTableSourceAsync(string batchId, string styleId, string assetId,
            CancellationToken token) => Unexpected();
        public Task<AiBatchAnalysisProjection> AnalyzeSelectedAssetAsync(string batchId, string styleId, string assetId,
            bool includeSizeTableSource, CancellationToken token) => Unexpected();
        public Task<AiRenderPlanPreparation> PrepareRenderingAsync(string batchId, CancellationToken token)
        { Mutations++; throw new InvalidOperationException("No render planning on selection"); }
        public Task<AiUploadConsentRecord?> GetUploadConsentAsync(CancellationToken token) => Task.FromResult<AiUploadConsentRecord?>(null);
        public Task<AiUploadConsentRecord> GrantUploadConsentAsync(CancellationToken token) => throw new NotSupportedException();
        public Task<AiUploadConsentRecord> RevokeUploadConsentAsync(CancellationToken token) => throw new NotSupportedException();
    }
}
