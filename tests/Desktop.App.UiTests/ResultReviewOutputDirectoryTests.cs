using Desktop.Application.Ai;
using Desktop.Application.Rendering;
using Desktop.Application.Tasks;

namespace Desktop.App.UiTests;

public sealed class ResultReviewOutputDirectoryTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MultiStyleLookupUsesSelectedStyleAndCannotOpenItAfterSelectionChanges(bool switchWhilePending)
    {
        var pending = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var reader = new OutputReader { Locate = _ => pending.Task };
        var opener = new RecordingOpener();
        using var view = new ResultReviewViewModel(new ResultHost(), null, reader, opener);
        view.State.ApplyRenderPreparation(new("batch", AiRenderPreparationStatus.ReadyToRender, "READY",
            [new("style_a", "page_a", AiRenderPagePreparationStatus.ReadyToRender, "READY"),
                new("style_b", "page_b", AiRenderPagePreparationStatus.ReadyToRender, "READY")]), null);
        view.SelectedResultPage = view.State.AllResultPages.Single(page => page.StyleId == "style_b");
        var opening = view.OpenOutputDirectoryAsync();
        Assert.Equal("style_b", Assert.Single(reader.StyleRequests));
        if (switchWhilePending) view.SelectedResultPage = view.State.AllResultPages.Single(page => page.StyleId == "style_a");
        pending.SetResult(@"C:\confirmed\style_b");
        await opening;
        if (switchWhilePending) { Assert.Empty(opener.Opened); Assert.Empty(view.OutputDirectoryMessage); }
        else Assert.Equal(@"C:\confirmed\style_b", Assert.Single(opener.Opened));
        Assert.Equal(0, reader.PreviewReads);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OverviewAndDetailCommandUseCurrentBatchWithoutStartingWork(bool detail)
    {
        var workflow = new ReadOnlyWorkflow();
        var reader = new OutputReader();
        var opener = new RecordingOpener();
        using var view = new ResultReviewViewModel(new ResultHost(), workflow, reader, opener);
        view.State.ApplyRenderPreparation(Preparation("old"), null);
        view.State.ApplyRenderPreparation(Preparation("selected"), null);
        if (detail) view.OpenResultPageCommand.Execute(view.State.AllResultPages[0]);
        var loadsBeforeOpen = workflow.Loads;

        view.OpenOutputDirectoryCommand.Execute(null);

        Assert.Equal("selected", Assert.Single(reader.Requested));
        Assert.Equal(@"C:\confirmed\selected", Assert.Single(opener.Opened));
        Assert.Equal(loadsBeforeOpen, workflow.Loads);
        Assert.Equal(0, workflow.Writes);
        Assert.Equal(0, reader.PreviewReads);
        Assert.Contains("文件保持原位", view.OutputDirectoryMessage, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("no-batch", "尚未选择")]
    [InlineData("no-service", "服务不可用")]
    [InlineData("no-output", "尚无已登记")]
    [InlineData("offline", "离线")]
    [InlineData("denied", "没有权限")]
    [InlineData("unsafe", "无法安全定位")]
    [InlineData("shell-failure", "暂时无法打开")]
    public async Task UnavailableOutputHasClearMessageAndDoesNotRender(string failure, string message)
    {
        var workflow = new ReadOnlyWorkflow();
        var reader = new OutputReader
        {
            Locate = _ => failure switch
            {
                "no-output" => Task.FromResult<string?>(null),
                "offline" => throw new DirectoryNotFoundException("private path"),
                "denied" => throw new UnauthorizedAccessException("private path"),
                "unsafe" => throw new InvalidDataException("private path"),
                _ => Task.FromResult<string?>(@"C:\confirmed\selected")
            }
        };
        var opener = new RecordingOpener { Fail = failure == "shell-failure" };
        using var view = new ResultReviewViewModel(new ResultHost(), workflow, failure == "no-service" ? null : reader, opener);
        if (failure != "no-batch") view.State.ApplyRenderPreparation(Preparation("selected"), null);
        view.ReportEditorStatus("保留当前编辑提示");

        await view.OpenOutputDirectoryAsync();

        Assert.Contains(message, view.OutputDirectoryMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("private path", view.OutputDirectoryMessage, StringComparison.Ordinal);
        Assert.Equal("保留当前编辑提示", view.RenderControlMessage);
        Assert.Empty(opener.Opened);
        Assert.Equal(0, workflow.Writes);
        Assert.Equal(0, workflow.Loads);
        Assert.Equal(0, reader.PreviewReads);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SwitchingBatchDuringLookupDoesNotOpenOldDirectoryOrLeakFailure(bool fail)
    {
        var pending = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var reader = new OutputReader { Locate = _ => pending.Task };
        var opener = new RecordingOpener();
        using var view = new ResultReviewViewModel(new ResultHost(), null, reader, opener);
        view.State.ApplyRenderPreparation(Preparation("old"), null);
        var opening = view.OpenOutputDirectoryAsync();
        view.State.ApplyRenderPreparation(Preparation("new"), null);
        if (fail) pending.SetException(new IOException("old lookup failed"));
        else pending.SetResult(@"C:\confirmed\old");
        await opening;

        Assert.Empty(opener.Opened);
        Assert.Empty(view.OutputDirectoryMessage);
        Assert.Equal("old", Assert.Single(reader.Requested));
    }

    [Fact]
    public async Task ConfirmedNewBatchClearsPreviousPreviewAndEditorMessages()
    {
        var workflow = new ReadOnlyWorkflow();
        var reader = new OutputReader();
        using var view = new ResultReviewViewModel(new ResultHost(), workflow, reader, new RecordingOpener());
        workflow.Journal = Journal("old", committed: true);
        Assert.True(await view.TryRestoreRenderingAsync("old", null));
        Assert.Contains("无法核验或预览", view.RenderControlMessage, StringComparison.Ordinal);
        workflow.Journal = Journal("new");
        Assert.True(await view.TryRestoreRenderingAsync("new", null));
        Assert.Empty(view.RenderControlMessage);

        view.ReportEditorStatus("旧批次编辑失败");
        workflow.Journal = Journal("third");
        Assert.True(await view.TryRestoreRenderingAsync("third", null));
        Assert.Empty(view.RenderControlMessage);
        Assert.Equal(0, workflow.Writes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NewBatchPreviewOrReadErrorIsNotCleared(bool readFailure)
    {
        var workflow = new ReadOnlyWorkflow { Journal = Journal("old") };
        using var view = new ResultReviewViewModel(new ResultHost(), workflow, new OutputReader(), new RecordingOpener());
        await view.TryRestoreRenderingAsync("old", null);
        view.ReportEditorStatus("旧编辑失败");
        workflow.Journal = Journal("new", committed: true);
        if (readFailure) workflow.FailLoadAt = workflow.Loads + 2;

        Assert.True(await view.TryRestoreRenderingAsync("new", null));

        Assert.Contains(readFailure ? "页面记录暂不可读取" : "无法核验或预览", view.RenderControlMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("旧编辑失败", view.RenderControlMessage, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("mismatch")]
    [InlineData("read-failure")]
    public async Task UnconfirmedBatchDoesNotClearPreviousMessagesOrSelection(string failure)
    {
        var workflow = new ReadOnlyWorkflow { Journal = Journal("old") };
        using var view = new ResultReviewViewModel(new ResultHost(), workflow, null, new RecordingOpener());
        await view.TryRestoreRenderingAsync("old", null);
        view.ReportEditorStatus("旧批次需处理");
        workflow.Journal = failure == "missing" ? null : Journal("other");
        if (failure == "read-failure")
        {
            workflow.FailLoadAt = workflow.Loads + 1;
            await Assert.ThrowsAsync<IOException>(() => view.TryRestoreRenderingAsync("new", null));
        }
        else Assert.False(await view.TryRestoreRenderingAsync("new", null));
        Assert.Equal("old", view.State.RenderPreparation!.BatchId);
        Assert.Equal("旧批次需处理", view.RenderControlMessage);
    }

    [Fact]
    public async Task SameBatchRestorePreservesEditorMessage()
    {
        var workflow = new ReadOnlyWorkflow { Journal = Journal("same") };
        using var view = new ResultReviewViewModel(new ResultHost(), workflow, null, new RecordingOpener());
        await view.TryRestoreRenderingAsync("same", null);
        view.ReportEditorStatus("当前编辑需处理");
        Assert.True(await view.TryRestoreRenderingAsync("same", null));
        Assert.Equal("当前编辑需处理", view.RenderControlMessage);
    }

    [Fact]
    public async Task LateOldPreviewFailureCannotReintroduceMessageAfterRestore()
    {
        var workflow = new ReadOnlyWorkflow { Journal = Journal("old", committed: true) };
        var reader = new OutputReader();
        using var view = new ResultReviewViewModel(new ResultHost(), workflow, reader, new RecordingOpener());
        await view.TryRestoreRenderingAsync("old", null);
        var pending = new TaskCompletionSource<Stream>(TaskCreationOptions.RunContinuationsAsynchronously);
        reader.Preview = () => pending.Task;
        var refreshing = view.RefreshRenderingAsync();
        workflow.Journal = Journal("new");
        Assert.True(await view.TryRestoreRenderingAsync("new", null));
        pending.SetException(new IOException("old preview failed"));
        await refreshing;
        Assert.Empty(view.RenderControlMessage);
        Assert.Equal("new", view.State.RenderJournal!.BatchId);
    }

    [Theory]
    [InlineData("relative")]
    [InlineData(@"C:relative")]
    [InlineData(@"\root-relative")]
    [InlineData(@"\\server\share")]
    [InlineData(@"\\?\C:\output")]
    [InlineData(@"\\.\C:\output")]
    [InlineData("https://example.invalid")]
    [InlineData("shell:Downloads")]
    [InlineData("cmd.exe /c start output")]
    [InlineData(@"C:\output:stream")]
    [InlineData("C:\\output\" & calc.exe")]
    [InlineData("C:\\output\nnext")]
    [InlineData(@"C:\output\*")]
    public void WindowsOpenerRejectsNonLocalOrCommandPathsBeforeLaunching(string path) =>
        Assert.Throws<InvalidDataException>(() => new WindowsOutputDirectoryOpener().Open(path));

    [Fact]
    public void WindowsOpenerRejectsMissingDirectoryAndRegularFileWithoutLaunching()
    {
        var missing = Path.Combine(Path.GetTempPath(), "result-output-missing-" + Guid.NewGuid().ToString("N"));
        Assert.Throws<DirectoryNotFoundException>(() => new WindowsOutputDirectoryOpener().Open(missing));
        var file = Path.GetTempFileName();
        try { Assert.Throws<DirectoryNotFoundException>(() => new WindowsOutputDirectoryOpener().Open(file)); }
        finally { File.Delete(file); }
    }

    private static AiRenderPlanPreparation Preparation(string batchId) => new(batchId,
        AiRenderPreparationStatus.ReadyToRender, "READY",
        [new("style", "page", AiRenderPagePreparationStatus.ReadyToRender, "READY")]);

    private static RenderBatchJournal Journal(string batchId, bool committed = false)
    {
        var checkpoint = new TaskCheckpoint(batchId, "style", "page", "render_output", 1,
            "sha256:" + new string('a', 64), "execution", OutputCommitState.Committed, DateTimeOffset.UtcNow);
        return new(1, batchId, 1, committed ? RenderBatchStatus.Completed : RenderBatchStatus.Prepared, false, false,
            [new("style", "page", null, 1, "{\"plan_digest\":\"sha256:test\"}",
                committed ? RenderBatchPageStatus.Committed : RenderBatchPageStatus.Planned, "READY",
                Commit: committed ? new(checkpoint, "render:test", "outputs/manifest.json", 1, new string('b', 64)) : null)]);
    }

    private sealed class OutputReader : IRenderBatchOutputReader
    {
        public List<string> Requested { get; } = [];
        public List<string> StyleRequests { get; } = [];
        public int PreviewReads { get; private set; }
        public Func<string, Task<string?>> Locate { get; init; } = batch => Task.FromResult<string?>(@"C:\confirmed\" + batch);
        public Func<Task<Stream>> Preview { get; set; } = () => throw new IOException("preview failed");
        public Task<string?> GetOutputDirectoryAsync(string batchId, CancellationToken cancellationToken)
        { Requested.Add(batchId); return Locate(batchId); }
        public Task<string?> GetStyleOutputDirectoryAsync(string batchId, string styleId, CancellationToken cancellationToken)
        { StyleRequests.Add(styleId); return GetOutputDirectoryAsync(batchId, cancellationToken); }
        public Task<Stream> OpenJpegAsync(string batchId, RenderBatchPage page, CancellationToken cancellationToken)
        { PreviewReads++; return Preview(); }
    }

    private sealed class RecordingOpener : IOutputDirectoryOpener
    {
        public List<string> Opened { get; } = [];
        public bool Fail { get; init; }
        public void Open(string directory)
        {
            if (Fail) throw new System.ComponentModel.Win32Exception("private path");
            Opened.Add(directory);
        }
    }

    private sealed class ReadOnlyWorkflow : IRenderBatchWorkflow
    {
        public RenderBatchJournal? Journal { get; set; }
        public int Loads { get; private set; }
        public int Writes { get; private set; }
        public int FailLoadAt { get; set; }
        public Task<RenderBatchJournal?> LoadAsync(string batchId, CancellationToken cancellationToken)
        {
            Loads++;
            if (Loads == FailLoadAt) throw new IOException("read failed");
            return Task.FromResult(Journal);
        }
        private Task<RenderBatchJournal> RejectWrite() { Writes++; throw new InvalidOperationException("Unexpected render execution"); }
        public Task<RenderBatchJournal> PrepareAsync(AiRenderPlanPreparation preparation, CancellationToken cancellationToken) => RejectWrite();
        public Task<RenderBatchJournal> PrepareRegenerationAsync(string batchId, string styleId, string pageId, string? outputVariantId, int expectedAttempt, CancellationToken cancellationToken) => RejectWrite();
        public Task<RenderBatchJournal> RunAsync(string batchId, CancellationToken cancellationToken) => RejectWrite();
        public Task RequestPauseAsync(string batchId, CancellationToken cancellationToken) => RejectWrite();
        public Task RequestCancelAsync(string batchId, CancellationToken cancellationToken) => RejectWrite();
    }

    private sealed class ResultHost : IResultReviewHost
    {
        public void ShowDetails() { }
        public void ShowOverview() { }
        public void ShowPreflight() { }
        public void NotifyDetailSelectionChanged() { }
        public void RefreshGenerationCommand() { }
    }
}
