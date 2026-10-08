using Desktop.Application.Ai;
using Desktop.Application.Rendering;
using Desktop.Application.Tasks;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Desktop.App.UiTests;

public sealed class ResultReviewViewModelTests
{
    [Fact]
    public async Task E3InstancesKeepSelectionCountsAndFailedRetryCommandSeparateAcrossRefresh()
    {
        var original = CreateCommittedPage(0, 10, 10);
        RenderInstanceIdentity Identity(char id) => new(1, "batch", "style", "instance_" + new string(id, 64), original.PageId,
            "snapshot", "revision", "sha256:" + new string('a', 64), "sha256:" + new string('b', 64), "sha256:" + new string('c', 64));
        var first = original with { Instance = Identity('a'), Commit = original.Commit! with { Page = original.Commit.Page with { Instance = Identity('a') } } };
        var second = original with { Instance = Identity('b'), Status = RenderBatchPageStatus.Failed, Commit = null };
        var blocked = original with { Instance = Identity('c'), Status = RenderBatchPageStatus.Blocked, Commit = null,
            ExecutionId = null, PlanJson = null, ReasonCode = "TPL_INSTANCE_SELECTION_MISSING", PreparationReasonCode = "TPL_INSTANCE_SELECTION_MISSING" };
        var workflow = new ReadOnlyWorkflow(new(4, "batch", 8, RenderBatchStatus.CompletedWithIssues, false, false, [first, second, blocked]));
        using var view = new ResultReviewViewModel(new ResultHost { AllowFailedPageRecovery = true, AllowRenderedEditing = false }, workflow);
        await view.TryRestoreRenderingAsync("batch", null);
        Assert.Equal(3, view.State.AllResultPages.Length);
        Assert.Contains("已登记 1 页", view.ResultFormalOutputText, StringComparison.Ordinal);
        var old = view.State.AllResultPages[1]; view.OpenResultPageCommand.Execute(old);
        await view.RefreshRenderingAsync(); view.OpenResultPageCommand.Execute(old);
        Assert.Equal(second.Instance!.PageInstanceId, view.SelectedResultPage!.PageInstanceId);
        Assert.True(view.RegenerateResultPageCommand.CanExecute(null));
        await view.RegenerateSelectedPageAsync();
        Assert.Equal(second.Instance.PageInstanceId, workflow.RequestedInstance);
        Assert.Null(workflow.Requested); Assert.Equal(1, workflow.Runs);
        Assert.Equal(3, view.State.AllResultPages.Length);
        Assert.Contains("已登记 1 页", view.ResultFormalOutputText, StringComparison.Ordinal);
        var red = view.State.AllResultPages.Single(page => page.PageInstanceId == blocked.Instance!.PageInstanceId);
        Assert.Contains("补图", red.DetailText, StringComparison.Ordinal); Assert.False(red.HasPreview);
    }

    [Fact]
    public async Task PublicOutputBatchDoesNotOfferOrDispatchPostGenerationEditing()
    {
        var workflow = new ReadOnlyWorkflow(Journal([CreateCommittedPage(0, 10, 10)]));
        using var view = new ResultReviewViewModel(new ResultHost { AllowRenderedEditing = false }, workflow);
        await view.TryRestoreRenderingAsync("batch", null);
        view.OpenResultPageCommand.Execute(view.State.AllResultPages[0]);
        Assert.False(view.RegenerateResultPageCommand.CanExecute(null));
        Assert.Contains("按款输出暂不支持生成后修改", view.RenderedEditingMessage, StringComparison.Ordinal);
        await view.RegenerateSelectedPageAsync();
        Assert.Null(workflow.Requested);
        Assert.Equal(0, workflow.Runs);
    }

    [Fact]
    public async Task PublicOutputBatchOffersRecoveryOnlyForAConfirmedFailedPage()
    {
        var failed = CreateCommittedPage(0, 10, 10) with
        {
            Status = RenderBatchPageStatus.Failed,
            ReasonCode = "BRIDGE_PLAN_INVALID",
            Commit = null,
            ExecutionId = "execution-failed"
        };
        var workflow = new ReadOnlyWorkflow(new(1, "batch", 10, RenderBatchStatus.CompletedWithIssues, false, false, [failed]));
        using var view = new ResultReviewViewModel(new ResultHost
        {
            AllowRenderedEditing = false,
            AllowFailedPageRecovery = true
        }, workflow);
        await view.TryRestoreRenderingAsync("batch", null);
        view.OpenResultPageCommand.Execute(view.State.AllResultPages[0]);

        Assert.True(view.RegenerateResultPageCommand.CanExecute(null));
        await view.RegenerateSelectedPageAsync();
        Assert.Equal(("style", "page-1", null, 1), workflow.Requested);
        Assert.Equal(1, workflow.Runs);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RegisteredSizeRepairReturnsToOverviewAndNeverAutoRunsAlreadyCompletedIntent(bool alreadyCompleted)
    {
        var page = new RenderBatchPage("style", "page", "blue", 1, "{\"plan_digest\":\"sha256:test\"}",
            RenderBatchPageStatus.Failed, "FAILED", "execution", PreparationReasonCode: "AI_SIZE_TABLE_VALID");
        var workflow = new ReadOnlyWorkflow(new(1, "batch", 8, RenderBatchStatus.CompletedWithIssues, false, false,
            [page with { OutputVariantId = "red" }, page]));
        var host = new ResultHost();
        using var view = new ResultReviewViewModel(host, workflow);
        await view.TryRestoreRenderingAsync("batch", null);
        view.OpenResultPageCommand.Execute(view.State.AllResultPages[1]);
        var command = new SizeTablePageRepairCommand("repair", new("batch", "style", "page", "blue", 8, 1,
            "sha256:" + new string('a', 64), 0, "sha256:" + new string('b', 64)),
            new([new("size", "尺码"), new("length", "衣长")], [new("row", ["L", "66"])]));
        var origin = new RenderSizeTableRepairOrigin(command.RequestId, SizeTableRepairData.RequestDigest(command),
            8, 1, command.Target.BasePlanDigest, "unused by projection", "unused by projection");
        var newPage = page with { Attempt = 2, Status = RenderBatchPageStatus.Planned, ExecutionId = null, SizeTableRepair = origin };
        workflow.Journal = workflow.Journal with { Version = 3, Revision = 9,
            Status = alreadyCompleted ? RenderBatchStatus.CompletedWithIssues : RenderBatchStatus.Prepared,
            RegenerationPageIndex = 2, Pages = [.. workflow.Journal.Pages, newPage] };
        await view.ContinueSizeTableRepairAsync(workflow.Journal, command);
        Assert.Equal("overview", host.Screen);
        Assert.Equal("blue", view.SelectedResultPage!.OutputVariantId);
        Assert.Equal(2, view.SelectedResultPage.Attempt);
        Assert.Equal("尺码修复待生成", view.SelectedResultPage.StatusText);
        Assert.Equal(alreadyCompleted ? 0 : 1, workflow.Runs);
        Assert.Null(workflow.Requested);
        Assert.Equal(page with { OutputVariantId = "red" }, workflow.Journal.Pages[0]);
    }

    [Fact]
    public async Task InvalidRenderDataShowsRecoveryMessageAndReleasesBusyCommands()
    {
        var page = new RenderBatchPage("style", "page", null, 1, "{\"plan_digest\":\"sha256:test\"}",
            RenderBatchPageStatus.Planned, "READY", PreparationReasonCode: "READY");
        var workflow = new ReadOnlyWorkflow(new(1, "batch", 1, RenderBatchStatus.Prepared, false, false, [page]))
        { FailRun = true };
        using var view = new ResultReviewViewModel(new ResultHost(), workflow);
        await view.TryRestoreRenderingAsync("batch", null);
        await view.StartRenderingAsync();
        Assert.Contains("当前无法继续渲染", view.RenderControlMessage, StringComparison.Ordinal);
        Assert.True(view.StartRenderingCommand.CanExecute(null));
        Assert.False(view.CancelRenderingCommand.CanExecute(null));
        Assert.Equal(1, workflow.Runs);
    }

    [Fact]
    public async Task RestoringCommittedOutputsPreservesReviewFactsNamesAndExactSelection()
    {
        var checkpoint = new TaskCheckpoint("batch", "style", "page", "render_output", 1,
            "sha256:" + new string('a', 64), "execution", OutputCommitState.Committed, DateTimeOffset.UtcNow, "a");
        var committed = new RenderBatchPage("style", "page", "a", 1, "{\"plan_digest\":\"sha256:test\"}",
            RenderBatchPageStatus.Committed, "RENDER_PAGE_COMMITTED", "execution",
            new(checkpoint, "render:test", "outputs/manifest.json", 1, new string('b', 64)),
            "AI_RENDER_PLAN_VALID_LAYOUT_FALLBACK", "米白");
        var unknownReview = committed with { OutputVariantId = "b", PreparationReasonCode = null, OutputVariantName = null };
        var failed = committed with { OutputVariantId = "c", Status = RenderBatchPageStatus.Failed, Commit = null, OutputVariantName = null };
        var workflow = new ReadOnlyWorkflow(new(1, "batch", 2, RenderBatchStatus.CompletedWithIssues, false, false,
            [committed, unknownReview, failed]));
        using var view = new ResultReviewViewModel(new ResultHost(), workflow);
        Assert.True(await view.TryRestoreRenderingAsync("batch", null));
        var cards = view.State.AllResultPages;
        Assert.Equal("页面 · 米白", cards[0].PageName);
        Assert.Equal("待检查构图", cards[0].StatusText);
        Assert.Equal("页面 · 输出 2", cards[1].PageName);
        Assert.Equal("待检查", cards[1].StatusText);
        Assert.Equal(2, view.ResultFilters.Single(item => item.Filter == ResultPageFilter.NeedsReview).Count);
        Assert.Equal("本页尚无正式输出", cards[2].OutputBoundaryText);
        view.OpenResultPageCommand.Execute(cards[1]);
        await view.RefreshRenderingAsync();
        Assert.Equal("b", view.SelectedResultPage!.OutputVariantId);
        Assert.False(view.StartRenderingCommand.CanExecute(null));
        Assert.True(view.RegenerateResultPageCommand.CanExecute(null));
    }

    [Fact]
    public async Task RefreshingCommittedOutputsKeepsCachedPreviewsAndDecodesConsecutiveTallPages()
    {
        await RunInStaAsync(async () =>
        {
            var dimensions = new[]
            {
                (790, 900), (790, 1000), (790, 1100), (790, 1200),
                (790, 2425), (790, 2546), (790, 1300)
            };
            var directory = Path.Combine(Path.GetTempPath(), "result-preview-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                var pages = dimensions.Select((size, index) => CreateCommittedPage(index, size.Item1, size.Item2)).ToArray();
                var paths = pages.Select((page, index) => WriteJpeg(directory, page.PageId, dimensions[index].Item1,
                    dimensions[index].Item2)).ToDictionary(item => item.PageId, item => item.Path, StringComparer.Ordinal);
                var workflow = new ReadOnlyWorkflow(Journal(pages[..4]));
                var outputs = new FileOutputReader(paths);
                using var view = new ResultReviewViewModel(new ResultHost(), workflow, outputs);

                Assert.True(await view.TryRestoreRenderingAsync("batch", null));
                Assert.Equal(4, outputs.Opened.Count);
                Assert.All(view.State.AllResultPages, page => Assert.NotNull(page.OutputPreview));

                workflow.Journal = Journal(pages);
                view.State.ApplyRenderPreparation(Preparation(pages), null);
                await view.RefreshRenderingAsync();

                Assert.Equal(pages.Select(page => page.PageId), outputs.Opened);
                Assert.Equal(7, view.State.AllResultPages.Length);
                Assert.All(view.State.AllResultPages, page =>
                {
                    var preview = Assert.IsType<BitmapImage>(page.OutputPreview);
                    Assert.Equal(320, Math.Max(preview.PixelWidth, preview.PixelHeight));
                    Assert.True(preview.IsFrozen);
                });
                var firstTall = Assert.IsType<BitmapImage>(view.State.AllResultPages[4].OutputPreview);
                var secondTall = Assert.IsType<BitmapImage>(view.State.AllResultPages[5].OutputPreview);
                Assert.Equal((104, 320), (firstTall.PixelWidth, firstTall.PixelHeight));
                Assert.Equal((99, 320), (secondTall.PixelWidth, secondTall.PixelHeight));
                Assert.Empty(view.RenderControlMessage);
            }
            finally
            {
                Directory.Delete(directory, recursive: true);
            }
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RegenerationCommandUsesExactSelectedVariantAndBlocksUnknownResults(bool unknownSibling)
    {
        var page = new RenderBatchPage("style", "page", "blue", 1, "{\"plan_digest\":\"sha256:test\"}",
            RenderBatchPageStatus.Failed, "FAILED", "execution", PreparationReasonCode: "READY");
        var workflow = new ReadOnlyWorkflow(new(1, "batch", 2, RenderBatchStatus.CompletedWithIssues, false, false,
            [page with { OutputVariantId = "red" }, page]));
        using var view = new ResultReviewViewModel(new ResultHost(), workflow);
        await view.TryRestoreRenderingAsync("batch", null);
        view.OpenResultPageCommand.Execute(view.State.AllResultPages[1]);
        Assert.True(view.RegenerateResultPageCommand.CanExecute(null));
        await view.RegenerateSelectedPageAsync();
        Assert.Equal(("style", "page", "blue", 1), workflow.Requested);
        Assert.Equal(1, workflow.Runs);
        var unknown = new ReadOnlyWorkflow(new(1, "batch", 2, RenderBatchStatus.RecoveryRequired, false, false,
            unknownSibling ? [page, page with { OutputVariantId = "red", Status = RenderBatchPageStatus.Unknown }]
                : [page with { Status = RenderBatchPageStatus.Unknown }]));
        var blockedHost = new ResultHost();
        using var blocked = new ResultReviewViewModel(blockedHost, unknown);
        await blocked.TryRestoreRenderingAsync("batch", null);
        blocked.OpenResultPageCommand.Execute(blocked.State.AllResultPages[0]);
        Assert.False(blocked.RegenerateResultPageCommand.CanExecute(null));
        await blocked.RegenerateSelectedPageAsync();
        Assert.Null(unknown.Requested);
        Assert.Equal(0, unknown.Runs);
        Assert.Equal("detail", blockedHost.Screen);
    }

    [Fact]
    public async Task RegenerationShowsControlsAndKeepsSelectionAsConnectionExecutionAndRecoveryChange()
    {
        var page = new RenderBatchPage("style", "page", "blue", 1, "{\"plan_digest\":\"sha256:test\"}",
            RenderBatchPageStatus.Failed, "FAILED", "execution", PreparationReasonCode: "READY");
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var workflow = new ReadOnlyWorkflow(new(1, "batch", 2, RenderBatchStatus.CompletedWithIssues, false, false,
            [page with { OutputVariantId = "red" }, page])) { RunGate = gate };
        var host = new ResultHost();
        using var view = new ResultReviewViewModel(host, workflow);
        await view.TryRestoreRenderingAsync("batch", null);
        view.SelectResultFilterCommand.Execute(view.ResultFilters.Single(item => item.Filter == ResultPageFilter.RuntimeFailed));
        view.OpenResultPageCommand.Execute(view.State.AllResultPages[1]);
        var notifications = new List<string?>();
        view.PropertyChanged += (_, args) => notifications.Add(args.PropertyName);

        var running = view.RegenerateSelectedPageAsync();
        try
        {
            await workflow.RunStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await view.RefreshRenderingAsync();
            Assert.Equal("overview", host.Screen);
            Assert.Equal(("style", "page", "blue", 2), (view.SelectedResultPage!.StyleId,
                view.SelectedResultPage.PageId, view.SelectedResultPage.OutputVariantId, view.SelectedResultPage.Attempt));
            Assert.Equal(ResultPageFilter.All, view.State.SelectedResultFilter);
            Assert.Contains(view.SelectedResultPage, view.VisibleResultStyleGroups.SelectMany(group => group.Pages));
            Assert.Contains("等待 Photoshop", view.ResultStatusText, StringComparison.Ordinal);
            Assert.Contains("连接已配对设备", view.RenderControlMessage, StringComparison.Ordinal);
            Assert.True(view.PauseRenderingCommand.CanExecute(null));
            Assert.True(view.CancelRenderingCommand.CanExecute(null));
            Assert.False(view.StartRenderingCommand.CanExecute(null));
            Assert.False(view.RegenerateResultPageCommand.CanExecute(null));

            foreach (var status in new[] { RenderBatchPageStatus.Submitting, RenderBatchPageStatus.Running })
            {
                workflow.Journal = workflow.Journal with
                {
                    Pages = [.. workflow.Journal.Pages.Take(2), workflow.Journal.Pages[^1] with { Status = status }]
                };
                notifications.Clear();
                await view.RefreshRenderingAsync();
                Assert.Empty(view.RenderControlMessage);
                Assert.Contains(nameof(ResultReviewViewModel.RenderControlMessage), notifications);
                Assert.DoesNotContain("等待 Photoshop", view.ResultStatusText, StringComparison.Ordinal);
                Assert.Equal("blue", view.SelectedResultPage!.OutputVariantId);
            }

            workflow.Journal = workflow.Journal with
            {
                Status = RenderBatchStatus.RecoveryRequired,
                Pages = [.. workflow.Journal.Pages.Take(2), workflow.Journal.Pages[^1] with { Status = RenderBatchPageStatus.Unknown }]
            };
        }
        finally
        {
            gate.TrySetResult();
            await running.WaitAsync(TimeSpan.FromSeconds(5));
        }

        Assert.Equal("查询并恢复", view.RenderActionText);
        Assert.Contains("查询并恢复", view.RenderControlMessage, StringComparison.Ordinal);
        Assert.False(view.RegenerateResultPageCommand.CanExecute(null));
        await view.RegenerateSelectedPageAsync();
        Assert.Equal(("style", "page", "blue", 1), workflow.Requested);
        Assert.Equal(1, workflow.Runs);
        Assert.Equal("blue", view.SelectedResultPage!.OutputVariantId);
    }

    [Fact]
    public async Task RegenerationPreparationFailureShowsOverviewMessageWithoutLosingSelection()
    {
        var page = new RenderBatchPage("style", "page", "blue", 1, "{\"plan_digest\":\"sha256:test\"}",
            RenderBatchPageStatus.Failed, "FAILED", "execution", PreparationReasonCode: "READY");
        var workflow = new ReadOnlyWorkflow(new(1, "batch", 2, RenderBatchStatus.CompletedWithIssues, false, false, [page]))
            { FailRegeneration = true };
        var host = new ResultHost();
        using var view = new ResultReviewViewModel(host, workflow);
        await view.TryRestoreRenderingAsync("batch", null);
        view.OpenResultPageCommand.Execute(view.State.AllResultPages[0]);

        await view.RegenerateSelectedPageAsync();

        Assert.Equal("overview", host.Screen);
        Assert.Contains("尚未确认重新生成", view.RenderControlMessage, StringComparison.Ordinal);
        Assert.Equal("blue", view.SelectedResultPage!.OutputVariantId);
        Assert.True(view.RegenerateResultPageCommand.CanExecute(null));
        Assert.Equal(0, workflow.Runs);
    }

    [Fact]
    public void EditorStatusNotifiesCurrentDetailsWithoutChangingSelection()
    {
        var host = new ResultHost();
        using var view = new ResultReviewViewModel(host);
        view.ApplyRenderPreparation(Preparation(), null);
        view.OpenResultPageCommand.Execute(view.State.AllResultPages[0]);
        var selected = view.SelectedResultPage;
        var notifications = new List<string?>();
        view.PropertyChanged += (_, args) => notifications.Add(args.PropertyName);

        view.ReportEditorStatus("尺码页计划已冻结，不能编辑；旧输出已保留。");

        Assert.Equal("detail", host.Screen);
        Assert.Same(selected, view.SelectedResultPage);
        Assert.Contains("计划已冻结", view.RenderControlMessage, StringComparison.Ordinal);
        Assert.Contains(nameof(ResultReviewViewModel.RenderControlMessage), notifications);
        view.ReportEditorStatus("");
        Assert.Empty(view.RenderControlMessage);
    }

    private sealed class ReadOnlyWorkflow(RenderBatchJournal journal) : IRenderBatchWorkflow
    {
        public RenderBatchJournal Journal { get => journal; set => journal = value; }
        public (string Style, string Page, string? Variant, int Attempt)? Requested { get; private set; }
        public int Runs { get; private set; }
        public string? RequestedInstance { get; private set; }
        public bool FailRun { get; init; }
        public bool FailRegeneration { get; init; }
        public TaskCompletionSource? RunGate { get; init; }
        public TaskCompletionSource RunStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<RenderBatchJournal> PrepareInstanceRetryAsync(string batchId, string styleId, string pageInstanceId,
            int expectedAttempt, CancellationToken token)
        {
            RequestedInstance = pageInstanceId;
            var prior = journal.Pages.Single(page => page.StyleId == styleId && page.Instance?.PageInstanceId == pageInstanceId && page.Attempt == expectedAttempt);
            return Task.FromResult(journal = journal with { Status = RenderBatchStatus.Prepared, RegenerationPageIndex = journal.Pages.Count,
                Pages = [.. journal.Pages, prior with { Attempt = expectedAttempt + 1, Status = RenderBatchPageStatus.Planned, ExecutionId = null, Commit = null }] });
        }
        public Task<RenderBatchJournal> PrepareRegenerationAsync(string batchId, string styleId, string pageId,
            string? outputVariantId, int expectedAttempt, CancellationToken token)
        {
            Requested = (styleId, pageId, outputVariantId, expectedAttempt);
            if (FailRegeneration) throw new InvalidOperationException("RENDER_REGENERATION_NOT_ALLOWED");
            var prior = journal.Pages.Single(page => page.StyleId == styleId && page.PageId == pageId
                && page.OutputVariantId == outputVariantId && page.Attempt == expectedAttempt);
            return Task.FromResult(journal = journal with
            {
                Version = 2, Status = RenderBatchStatus.Prepared, RegenerationPageIndex = journal.Pages.Count,
                Pages = [.. journal.Pages, prior with
                    { Attempt = expectedAttempt + 1, Status = RenderBatchPageStatus.Planned, ExecutionId = null, Commit = null }]
            });
        }
        public Task<RenderBatchJournal?> LoadAsync(string batchId, CancellationToken token) => Task.FromResult<RenderBatchJournal?>(journal);
        public Task<RenderBatchJournal> PrepareAsync(AiRenderPlanPreparation preparation, CancellationToken token) => Task.FromResult(journal);
        public async Task<RenderBatchJournal> RunAsync(string batchId, CancellationToken token)
        {
            Runs++;
            if (FailRun) throw new System.IO.InvalidDataException("BRIDGE_RESOURCE_BINDING_INVALID");
            if (RunGate is { } gate)
            {
                journal = journal with { Status = RenderBatchStatus.Running };
                RunStarted.TrySetResult();
                await gate.Task.WaitAsync(token).ConfigureAwait(false);
                return journal;
            }
            return journal = journal with { Status = RenderBatchStatus.CompletedWithIssues };
        }
        public Task RequestPauseAsync(string batchId, CancellationToken token) => throw new NotSupportedException();
        public Task RequestCancelAsync(string batchId, CancellationToken token) => throw new NotSupportedException();
    }

    private static RenderBatchPage CreateCommittedPage(int index, int width, int height)
    {
        var pageId = $"page-{index + 1}";
        var digest = "sha256:" + new string("abcdef0"[index], 64);
        var plan = JsonSerializer.Serialize(new
        {
            plan_digest = digest,
            document = new { canvas = new { width, height } }
        });
        var checkpoint = new TaskCheckpoint("batch", "style", pageId, "render_output", 1, digest,
            $"execution-{index + 1}", OutputCommitState.Committed, DateTimeOffset.UtcNow);
        return new("style", pageId, null, 1, plan, RenderBatchPageStatus.Committed, "RENDER_PAGE_COMMITTED",
            checkpoint.BridgeExecutionId, new(checkpoint, $"render:{index + 1}", $"outputs/{pageId}/manifest.json", 1,
                new string((char)('1' + index), 64)), PreparationReasonCode: "READY");
    }

    private static RenderBatchJournal Journal(IReadOnlyList<RenderBatchPage> pages) =>
        new(1, "batch", 1, RenderBatchStatus.Completed, false, false, pages);

    private static AiRenderPlanPreparation Preparation(IReadOnlyList<RenderBatchPage> pages) => new(
        "batch", AiRenderPreparationStatus.ReadyToRender, "READY",
        pages.Select(page => new AiRenderPagePreparation(page.StyleId, page.PageId,
            AiRenderPagePreparationStatus.ReadyToRender, "READY", page.PlanJson,
            page.Commit!.Page.PlanDigest)).ToArray());

    private static (string PageId, string Path) WriteJpeg(string directory, string pageId, int width, int height)
    {
        var stride = checked(width * 3);
        var pixels = new byte[checked(stride * height)];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var offset = y * stride + x * 3;
                pixels[offset] = (byte)(x % 251);
                pixels[offset + 1] = (byte)(y % 241);
                pixels[offset + 2] = (byte)((x + y) % 239);
            }
        }
        var source = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgr24, null, pixels, stride);
        var encoder = new JpegBitmapEncoder { QualityLevel = 85 };
        encoder.Frames.Add(BitmapFrame.Create(source));
        var path = Path.Combine(directory, pageId + ".jpg");
        using (var output = File.Create(path)) encoder.Save(output);
        return (pageId, path);
    }

    private sealed class FileOutputReader(IReadOnlyDictionary<string, string> paths) : IRenderBatchOutputReader
    {
        public List<string> Opened { get; } = [];

        public Task<string?> GetOutputDirectoryAsync(string batchId, CancellationToken cancellationToken) => Task.FromResult<string?>(null);

        public Task<Stream> OpenJpegAsync(string batchId, RenderBatchPage page, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Opened.Add(page.PageId);
            Stream stream = new FileStream(paths[page.PageId], FileMode.Open, FileAccess.Read, FileShare.Read,
                64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            return Task.FromResult(stream);
        }
    }

    private static Task<bool> RunInStaAsync(Func<Task> action)
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                action().GetAwaiter().GetResult();
                completion.SetResult(true);
            }
            catch (Exception exception)
            {
                completion.SetException(exception);
            }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public void OpeningOldCardSelectsCurrentIdentityAndPagingUsesAllPages()
    {
        var host = new ResultHost();
        using var page = new ResultReviewViewModel(host);
        page.ApplyRenderPreparation(Preparation(), null);
        var oldCard = page.ResultStyleGroups[0].Pages[0];
        page.ApplyRenderPreparation(Preparation(), null);
        var currentCard = page.ResultStyleGroups[0].Pages[0];
        Assert.NotSame(oldCard, currentCard);
        var reviewFilter = page.ResultFilters.Single(item => item.Filter == ResultPageFilter.NeedsReview);
        page.SelectResultFilterCommand.Execute(reviewFilter);
        Assert.Single(page.VisibleResultStyleGroups[0].Pages);

        page.OpenResultPageCommand.Execute(oldCard);

        Assert.Same(currentCard, page.SelectedResultPage);
        Assert.Equal("detail", host.Screen);
        Assert.False(page.PreviousResultPageCommand.CanExecute(null));
        Assert.True(page.NextResultPageCommand.CanExecute(null));
        page.NextResultPageCommand.Execute(null);
        Assert.Equal("blocked", page.SelectedResultPage!.PageId);
        Assert.False(page.NextResultPageCommand.CanExecute(null));
        Assert.True(page.PreviousResultPageCommand.CanExecute(null));
        page.PreviousResultPageCommand.Execute(null);
        Assert.Same(currentCard, page.SelectedResultPage);
    }

    [Fact]
    public void OverviewPreservesSelectionAndPreflightClearsItBeforeNavigation()
    {
        var host = new ResultHost();
        using var page = new ResultReviewViewModel(host);
        page.ApplyRenderPreparation(Preparation(), null);
        var selected = page.ResultStyleGroups[0].Pages[0];
        page.OpenResultPageCommand.Execute(selected);
        page.ReturnToResultOverviewCommand.Execute(null);
        Assert.Same(selected, page.SelectedResultPage);
        Assert.Equal("overview", host.Screen);
        host.OnPreflight = () => Assert.Null(page.SelectedResultPage);

        page.ReturnToPreflightCommand.Execute(null);

        Assert.Equal("preflight", host.Screen);
        Assert.Null(page.SelectedResultPage);
        Assert.False(page.PreviousResultPageCommand.CanExecute(null));
    }

    private static AiRenderPlanPreparation Preparation() => new(
        "batch-result", AiRenderPreparationStatus.WaitingForPhotoshop, "PHOTOSHOP_UNAVAILABLE",
        [new("style", "ready", AiRenderPagePreparationStatus.ReadyToRender, "READY"),
         new("style", "blocked", AiRenderPagePreparationStatus.BusinessBlocked, "MISSING_SELECTION")]);

    [Fact]
    public void OpeningSamePageDifferentVariantKeepsItsExactIdentityAfterRefresh()
    {
        using var page = new ResultReviewViewModel(new ResultHost());
        var preparation = new AiRenderPlanPreparation("batch-variants", AiRenderPreparationStatus.ReadyToRender, "READY",
            [new("style", "page", AiRenderPagePreparationStatus.ReadyToRender, "READY", OutputVariantId: "a"),
             new("style", "page", AiRenderPagePreparationStatus.ReadyToRender, "READY", OutputVariantId: "b")]);
        page.ApplyRenderPreparation(preparation, null);
        var oldB = page.ResultStyleGroups[0].Pages[1];
        page.ApplyRenderPreparation(preparation, null);
        page.OpenResultPageCommand.Execute(oldB);
        Assert.Equal("b", page.SelectedResultPage!.OutputVariantId);
        Assert.Same(page.ResultStyleGroups[0].Pages[1], page.SelectedResultPage);
    }

    private sealed class ResultHost : IResultReviewHost
    {
        public bool AllowRenderedEditing { get; init; } = true;
        public bool AllowFailedPageRecovery { get; init; }
        public bool CanEditRenderedBatch(string batchId) => AllowRenderedEditing;
        public bool CanRecoverFailedPage(string batchId, RenderBatchPage page) =>
            AllowFailedPageRecovery && page.Status == RenderBatchPageStatus.Failed;
        public string? Screen { get; private set; }
        public Action? OnPreflight { get; set; }
        public void ShowDetails() => Screen = "detail";
        public void ShowOverview() => Screen = "overview";
        public void ShowPreflight() { OnPreflight?.Invoke(); Screen = "preflight"; }
        public void NotifyDetailSelectionChanged() { }
        public void RefreshGenerationCommand() { }
    }
}
