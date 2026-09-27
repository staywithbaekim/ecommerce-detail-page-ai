using Desktop.Application.Inputs;
using Desktop.Application.Tasks;
using Desktop.Domain.Inputs;
using Desktop.Domain.Tasks;
using Desktop.Domain.Templates;

namespace Desktop.App.UiTests;

public sealed class TaskListStatusTests
{
    private static readonly string[] ExpectedBatchOrder = ["batch_b", "batch_a"];
    private static readonly string[] ExpectedUpdatedOrder = ["batch_a", "batch_b"];

    [Theory]
    [InlineData("Completed", 7, 0, "已完成", "Completed")]
    [InlineData("CompletedWithIssues", 6, 1, "完成但有问题", "Recoverable")]
    [InlineData("RecoveryRequired", 1, 0, "需要恢复", "Recoverable")]
    [InlineData("Failed", 0, 2, "运行失败", "Recoverable")]
    [InlineData("SystemBlocked", 0, 0, "系统阻断", "Recoverable")]
    [InlineData("UserActionRequired", 0, 0, "需要用户处理", "Recoverable")]
    [InlineData("ReadyToRender", 7, 1, "待生成", "Paused")]
    [InlineData("Rendering", 3, 0, "生成记录待核对", "Recoverable")]
    [InlineData("Scanning", 0, 0, "扫描记录待核对", "Recoverable")]
    [InlineData("Paused", 2, 0, "已暂停", "Paused")]
    [InlineData("Cancelled", 0, 0, "已取消", "Paused")]
    [InlineData("Draft", 0, 0, "任务待准备", "Paused")]
    [InlineData("future-status", 7, 0, "任务状态需核对", "Recoverable")]
    [InlineData("Completed", 0, 0, "任务状态需核对", "Recoverable")]
    [InlineData("Completed", 7, 1, "任务状态需核对", "Recoverable")]
    [InlineData("CompletedWithIssues", 7, 0, "任务状态需核对", "Recoverable")]
    public void ProjectsPersistedStatesWithoutClaimingLiveExecutionOrFileVerification(
        string status, int outputs, int issues, string text, string state)
    {
        var row = TaskRowViewModel.FromSummary(Summary() with
        { Task = new("batch_a", status, 10, outputs, issues, true) }, "已校准模板");

        Assert.Equal(text, row.StatusText);
        Assert.Equal(state, row.StatusState);
        Assert.Equal($"已登记 {outputs} 页 · 历史问题 {issues} 项", row.StatusDetail);
        Assert.Contains("不代表已重新核验输出文件", row.StatusDescription, StringComparison.Ordinal);
        if (status is "Rendering" or "Scanning")
            Assert.Contains("不代表进程仍在运行", row.StatusDescription, StringComparison.Ordinal);
        Assert.Equal("batch_a", row.BatchId);
        Assert.Equal("查看/恢复", row.ActionText);
    }

    [Fact]
    public void NoEventsRemainInputOnlyAndUnavailableStateNeverLooksLikeAnOrdinaryScan()
    {
        var input = Summary();
        var absent = TaskRowViewModel.FromSummary(input, "模板");
        var empty = TaskRowViewModel.FromSummary(input with
        { Task = new("batch_a", "None", 0, 0, 0, false) }, "模板");
        Assert.Equal("已扫描 · 查看详情", absent.StatusText);
        Assert.Equal(absent, empty);
        Assert.Equal("暂无生成任务记录", empty.StatusDetail);

        var failed = TaskRowViewModel.FromSummary(input with { TaskStateUnavailable = true }, "模板");
        Assert.Equal("任务状态读取失败", failed.StatusText);
        Assert.Equal("Recoverable", failed.StatusState);
        var wrongBatch = TaskRowViewModel.FromSummary(input with
        { Task = new("other_batch", "Completed", 9, 7, 0, false) }, "模板");
        Assert.Equal(failed, wrongBatch);
    }

    [Fact]
    public async Task AdapterReplaysExactEventsAndIsolatesCorruptionWithoutWritesOrScan()
    {
        var ports = new ReadOnlyPorts();
        ports.Events["batch_a"] = CompletedEvents("batch_a");
        ports.Events["batch_b"] = [TaskEvent.Create("batch_b", 2, TaskEventType.TaskCreated)];
        var adapter = CreateAdapter(ports);

        var summaries = await adapter.ListRecoverableAsync(default);

        Assert.Equal(ExpectedUpdatedOrder, summaries.Select(row => row.BatchId));
        Assert.True(summaries[1].TaskStateUnavailable);
        Assert.Null(summaries[1].Task);
        Assert.False(summaries[0].TaskStateUnavailable);
        Assert.Equal("Completed", summaries[0].Task!.Status);
        Assert.Equal(2, summaries[0].Task!.OutputCount);
        Assert.Equal(ports.Events["batch_a"].Max(item => item.OccurredAt), summaries[0].LatestUpdatedAt);
        Assert.Equal(ExpectedBatchOrder, ports.Reads);
        Assert.Equal("已完成", TaskRowViewModel.FromSummary(summaries[0], "模板").StatusText);
    }

    [Fact]
    public void SummaryUsesNewerInputTimeAndNeverUsesUnavailableOrWrongBatchTime()
    {
        var input = Summary() with { UpdatedAt = DateTimeOffset.UnixEpoch.AddHours(3) };
        var task = new TaskProjection("batch_a", "Completed", 10, 7, 0, false)
        { UpdatedAt = DateTimeOffset.UnixEpoch.AddHours(2) };
        Assert.Equal(input.UpdatedAt, (input with { Task = task }).LatestUpdatedAt);
        task = task with { UpdatedAt = DateTimeOffset.UnixEpoch.AddHours(4) };
        Assert.Equal(task.UpdatedAt, (input with { Task = task }).LatestUpdatedAt);
        Assert.Equal(input.UpdatedAt, (input with { Task = task, TaskStateUnavailable = true }).LatestUpdatedAt);
        Assert.Equal(input.UpdatedAt, (input with { Task = task with { TaskId = "other" } }).LatestUpdatedAt);
        Assert.Equal(input.UpdatedAt, (input with { Task = task with { UpdatedAt = null } }).LatestUpdatedAt);
    }

    [Fact]
    public async Task AdapterSerializesReadsAndPropagatesCancellationInsteadOfShowingFailureRows()
    {
        using var cancellation = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<IReadOnlyList<TaskEvent>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var ports = new ReadOnlyPorts
        {
            ReadOverride = async (_, token) =>
            {
                entered.TrySetResult();
                return await release.Task.WaitAsync(token);
            }
        };
        var listing = CreateAdapter(ports).ListRecoverableAsync(cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("batch_b", Assert.Single(ports.Reads));
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => listing);
        Assert.Single(ports.Reads);
    }

    private static BatchRecoverySummary Summary() => new("batch_a", "group", @"C:\款号6015",
        BatchInputUiStatus.ScanReady, DateTimeOffset.UnixEpoch);

    private static BatchInputWorkflowAdapter CreateAdapter(ReadOnlyPorts ports) => new(
        new BatchInputWorkflowFacade(new BatchInputService(ports, ports, ports, ports, TimeProvider.System)),
        new TaskCoordinator(ports));

    private static TaskEvent[] CompletedEvents(string batchId) =>
    [
        TaskEvent.Create(batchId, 1, TaskEventType.TaskCreated),
        TaskEvent.Create(batchId, 2, TaskEventType.ReadyToRender),
        TaskEvent.Create(batchId, 3, TaskEventType.RenderingStarted),
        TaskEvent.Create(batchId, 4, TaskEventType.PageCommitted, "page_1"),
        TaskEvent.Create(batchId, 5, TaskEventType.PageCommitted, "page_1"),
        TaskEvent.Create(batchId, 6, TaskEventType.PageCommitted, "page_2"),
        TaskEvent.Create(batchId, 7, TaskEventType.Completed)
    ];

    private sealed class ReadOnlyPorts : IBatchRepository, ITaskEventStore, IFileInventory,
        IAssetMetadataReader, IBatchScanCommitter
    {
        public Dictionary<string, IReadOnlyList<TaskEvent>> Events { get; } = new(StringComparer.Ordinal);
        public List<string> Reads { get; } = [];
        public Func<string, CancellationToken, Task<IReadOnlyList<TaskEvent>>>? ReadOverride { get; init; }
        public Task<IReadOnlyList<TaskEvent>> LoadEventsAsync(string taskId, CancellationToken cancellationToken)
        {
            Reads.Add(taskId);
            return ReadOverride?.Invoke(taskId, cancellationToken)
                ?? Task.FromResult(Events.GetValueOrDefault(taskId) ?? []);
        }
        public Task<IReadOnlyList<BatchDraft>> ListRecoverableAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<BatchDraft>>([Draft("batch_a", 0), Draft("batch_b", 1)]);
        private static BatchDraft Draft(string batchId, int hours)
        {
            var now = DateTimeOffset.UnixEpoch.AddHours(hours);
            var snapshot = BatchTemplateSnapshot.Restore("snapshot_" + batchId, batchId, "revision",
                [new("page", 0, TemplatePageContentType.NormalComposition, TemplateGenerationMode.OncePerProduct,
                    null, "resource", [])], "sha256:" + new string('a', 64), now);
            return BatchDraft.Create(batchId, @"C:\source", @"C:\output", snapshot,
                Desktop.Domain.Inputs.BatchSelectionMode.Automatic, false, now);
        }
        public Task<AppendEventOutcome> AppendAsync(TaskEvent taskEvent, TaskState resultingState, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Task listing must not write events");
        public Task SaveAsync(BatchDraft draft, CancellationToken cancellationToken) => throw new InvalidOperationException();
        public Task<BatchDraft?> GetAsync(string batchId, CancellationToken cancellationToken) => throw new InvalidOperationException();
        public Task DeleteDraftAsync(string batchId, CancellationToken cancellationToken) => throw new InvalidOperationException();
        public Task<FileInventoryReadResult> ReadApprovedLevelsAsync(string inputRoot, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Task listing must not scan");
        public Task<AssetMetadataReadResult> ReadAsync(string inputRoot, string sourceRelativePath, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Task listing must not open photos");
        public Task<BatchScanCommitResult> CommitAsync(BatchScanCommitRequest request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Task listing must not commit input");
    }
}
