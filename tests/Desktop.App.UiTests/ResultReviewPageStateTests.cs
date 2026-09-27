using Desktop.Application.Ai;
using Desktop.Application.Rendering;
using Desktop.Application.Tasks;

namespace Desktop.App.UiTests;

public sealed class ResultReviewPageStateTests
{
    [Fact]
    public void PreparationIsNotPresentedAsCompletedOutputAndTitleUpdatesAfterCommit()
    {
        var state = new ResultReviewPageState();
        var notifications = new List<string?>();
        state.PropertyChanged += (_, args) => notifications.Add(args.PropertyName);
        state.ApplyRenderPreparation(new("batch-state-test", AiRenderPreparationStatus.WaitingForPhotoshop,
            "PHOTOSHOP_UNAVAILABLE", [new("style", "page", AiRenderPagePreparationStatus.ReadyToRender, "READY"),
                new("style", "blocked", AiRenderPagePreparationStatus.BusinessBlocked, "MISSING")]), null);

        Assert.Equal("生成准备", state.ResultTitleText);
        Assert.Contains("不是套版效果", state.ResultFormalOutputText, StringComparison.Ordinal);
        Assert.Equal("待处理", state.ResultFilters.Single(filter => filter.Filter == ResultPageFilter.CompletedWithProblems).Label);
        Assert.DoesNotContain("Photoshop 暂不可用", state.ResultStatusText, StringComparison.Ordinal);

        state.ApplyRenderJournal(new(1, "batch-state-test", 2, RenderBatchStatus.CompletedWithIssues, false, false,
            [Committed("page", null), new("style", "blocked", null, 1, null, RenderBatchPageStatus.Blocked, "MISSING")]));
        Assert.Equal("结果检查", state.ResultTitleText);
        Assert.Equal("完成但有问题", state.ResultFilters.Single(filter => filter.Filter == ResultPageFilter.CompletedWithProblems).Label);
        Assert.Contains("已登记 1 页正式输出", state.ResultFormalOutputText, StringComparison.Ordinal);
        Assert.Equal(2, notifications.Count(name => name == nameof(state.ResultTitleText)));

        state.ApplyRenderPreparation(new("another-batch", AiRenderPreparationStatus.ReadyToRender, "READY", []), null);
        Assert.Equal("生成准备", state.ResultTitleText);
    }

    [Theory]
    [InlineData(RenderBatchPageStatus.Planned, true)]
    [InlineData(RenderBatchPageStatus.Submitting, false)]
    [InlineData(RenderBatchPageStatus.Running, false)]
    [InlineData(RenderBatchPageStatus.Committed, false)]
    [InlineData(RenderBatchPageStatus.Failed, false)]
    [InlineData(RenderBatchPageStatus.Unknown, false)]
    public void WaitingForHostUsesCurrentRegenerationScopeNotOtherPlannedPages(RenderBatchPageStatus status, bool waiting)
    {
        var state = new ResultReviewPageState();
        state.ApplyRenderPreparation(new("batch-state-test", AiRenderPreparationStatus.ReadyToRender, "READY",
            [new("style", "page", AiRenderPagePreparationStatus.ReadyToRender, "READY", OutputVariantId: "blue"),
             new("style", "page", AiRenderPagePreparationStatus.ReadyToRender, "READY", OutputVariantId: "red")]), null);
        var original = Committed("page", "blue");
        var other = original with { OutputVariantId = "red", Status = RenderBatchPageStatus.Planned, ExecutionId = null, Commit = null };
        var next = original with { Attempt = 2, Status = status, Commit = status == RenderBatchPageStatus.Committed ? original.Commit : null };

        state.ApplyRenderJournal(new(2, "batch-state-test", 3, RenderBatchStatus.Running, false, false, [original, other, next], 2));

        Assert.Equal(waiting, state.IsWaitingForRenderHost);
        Assert.Equal(waiting, state.ResultStatusText.Contains("等待 Photoshop", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false, "完成当前款式后暂停")]
    [InlineData(true, "正在安全取消")]
    public void ControlRequestsTakePriorityOverWaitingForConnection(bool cancel, string message)
    {
        var state = new ResultReviewPageState();
        state.ApplyRenderPreparation(new("batch-state-test", AiRenderPreparationStatus.ReadyToRender, "READY",
            [new("style", "page", AiRenderPagePreparationStatus.ReadyToRender, "READY")]), null);
        var planned = Committed("page", null) with { Status = RenderBatchPageStatus.Planned, ExecutionId = null, Commit = null };
        state.ApplyRenderJournal(new(1, "batch-state-test", 2, RenderBatchStatus.Running, !cancel, cancel, [planned]));

        Assert.False(state.IsWaitingForRenderHost);
        Assert.Contains(message, state.ResultStatusText, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(RenderBatchPageStatus.Planned, "等待重新生成")]
    [InlineData(RenderBatchPageStatus.Failed, "运行失败")]
    [InlineData(RenderBatchPageStatus.Unknown, "需要恢复")]
    public void NewAttemptRetainsOldOutputAndDoesNotInflatePageCount(RenderBatchPageStatus status, string label)
    {
        var state = new ResultReviewPageState();
        state.ApplyRenderPreparation(new("batch-state-test", AiRenderPreparationStatus.ReadyToRender, "READY",
            [new("style", "page", AiRenderPagePreparationStatus.ReadyToRender, "READY")]), null);
        var original = Committed("page", null);
        var next = original with { Attempt = 2, Status = status, Commit = null };
        state.ApplyRenderJournal(new(2, "batch-state-test", 3, RenderBatchStatus.CompletedWithIssues, false, false, [original, next], 1));
        var card = Assert.Single(state.AllResultPages);
        Assert.True(card.HasFormalOutput);
        Assert.True(card.ShowsPreviousOutput);
        Assert.Equal(label, card.StatusText);
        Assert.StartsWith("保留上次正式输出", card.OutputBoundaryText);
        state.ApplyRenderJournal(new(2, "batch-state-test", 4, RenderBatchStatus.Completed, false, false,
            [original, original with { Attempt = 2 }], 1));
        Assert.StartsWith("已登记 1 页", state.ResultFormalOutputText);
    }

    [Theory]
    [InlineData(AiRenderPagePreparationStatus.BusinessBlocked, "TPL_RENDER_LAYOUT_REVIEW_REQUIRED", false)]
    [InlineData(AiRenderPagePreparationStatus.ReadyToRender, "AI_RENDER_PLAN_VALID_LAYOUT_FALLBACK", false)]
    [InlineData(AiRenderPagePreparationStatus.ReadyToRender, "AI_RENDER_PLAN_VALID_LAYOUT_FALLBACK", true)]
    public void LayoutConflictsAndUnlocatedSubjectsRemainInReviewFilter(
        AiRenderPagePreparationStatus status, string reason, bool hasOutput)
    {
        var card = ResultPageCardViewModel.Create("款式", null, new("style", "page", status, reason),
            AiRenderPreparationStatus.ReadyToRender, hasOutput ? Committed("page", null) : null);
        Assert.Equal(ResultPageFilter.NeedsReview, card.Filter);
        Assert.Equal(hasOutput, card.HasFormalOutput);
        Assert.NotEqual("已完成", card.StatusText);
    }

    [Fact]
    public void BatchOutputFlagCannotMarkUncommittedVariantCompleteOrShowSourceAsOutput()
    {
        var state = new ResultReviewPageState();
        var preparation = new AiRenderPlanPreparation("batch-state-test", AiRenderPreparationStatus.ReadyToRender, "READY",
            [new("style", "page", AiRenderPagePreparationStatus.ReadyToRender, "READY", OutputVariantId: "a"),
             new("style", "page", AiRenderPagePreparationStatus.ReadyToRender, "READY", OutputVariantId: "b")], HasFormalOutput: true);
        state.ApplyRenderPreparation(preparation, null);
        Assert.All(state.AllResultPages, page => Assert.False(page.HasFormalOutput));
        var a = Committed("page", "a");
        state.ApplyRenderJournal(new(1, preparation.BatchId, 2, RenderBatchStatus.RecoveryRequired, false, false,
            [a, new("style", "page", "b", 1, "{}", RenderBatchPageStatus.Unknown, "RENDER_RECOVERY_REQUIRED")]));
        Assert.True(state.AllResultPages[0].HasFormalOutput);
        Assert.False(state.AllResultPages[0].HasSourcePreview);
        Assert.False(state.AllResultPages[0].HasPreview); // Unavailable JPG does not fall back to a source image.
        Assert.False(state.AllResultPages[1].HasFormalOutput);
        Assert.Equal("需要恢复", state.AllResultPages[1].StatusText);
    }

    private static RenderBatchPage Committed(string pageId, string? variant)
    {
        var page = new TaskCheckpoint("batch-state-test", "style", pageId, "render_output", 1,
            "sha256:" + new string('a', 64), "execution_test", OutputCommitState.Verified, DateTimeOffset.UtcNow, variant);
        return new("style", pageId, variant, 1, "{}", RenderBatchPageStatus.Committed, "RENDER_PAGE_COMMITTED",
            "execution_test", new(page, "render:test", "outputs/manifest.json", 1, new string('b', 64)));
    }

    [Fact]
    public void ApplyingPreparationBuildsPageGroupsAndReviewFilters()
    {
        var state = new ResultReviewPageState();
        state.ApplyRenderPreparation(
            new AiRenderPlanPreparation(
                "batch-state-test",
                AiRenderPreparationStatus.WaitingForPhotoshop,
                "PHOTOSHOP_UNAVAILABLE",
                [
                    new AiRenderPagePreparation(
                        "style-a",
                        "page-ready",
                        AiRenderPagePreparationStatus.ReadyToRender,
                        "READY"),
                    new AiRenderPagePreparation(
                        "style-a",
                        "page-blocked",
                        AiRenderPagePreparationStatus.BusinessBlocked,
                        "MISSING_SELECTION")
                ]),
            projection: null);

        Assert.Single(state.ResultStyleGroups);
        Assert.Equal(2, state.ResultStyleGroups[0].Pages.Count);
        Assert.Equal(2, state.VisibleResultStyleGroups[0].Pages.Count);
        Assert.Equal(2, state.ResultFilters.Single(item => item.Filter == ResultPageFilter.All).Count);
        Assert.Equal(1, state.ResultFilters.Single(item => item.Filter == ResultPageFilter.NeedsReview).Count);
        Assert.Equal(1, state.ResultFilters.Single(item => item.Filter == ResultPageFilter.CompletedWithProblems).Count);
        Assert.Equal("待渲染", state.ResultStyleGroups[0].Pages[0].StatusText);
    }

    [Fact]
    public void SelectingFilterAndResizingStayInsidePageState()
    {
        var state = new ResultReviewPageState();
        state.ApplyRenderPreparation(
            new AiRenderPlanPreparation(
                "batch-state-test",
                AiRenderPreparationStatus.ReadyToRender,
                "READY",
                [
                    new AiRenderPagePreparation(
                        "style-a",
                        "page-ready",
                        AiRenderPagePreparationStatus.ReadyToRender,
                        "READY"),
                    new AiRenderPagePreparation(
                        "style-a",
                        "page-blocked",
                        AiRenderPagePreparationStatus.BusinessBlocked,
                        "MISSING_SELECTION")
                ]),
            projection: null);

        state.SelectResultFilter(ResultPageFilter.CompletedWithProblems);
        Assert.Single(state.VisibleResultStyleGroups);
        Assert.Single(state.VisibleResultStyleGroups[0].Pages);
        Assert.Equal(ResultPageFilter.CompletedWithProblems, state.SelectedResultFilter);

        state.SetWindowWidth(1180);
        Assert.True(state.IsResultDetailNarrowLayout);
        Assert.Equal(172, state.ResultCandidateColumnWidth.Value);
        Assert.Equal(216, state.ResultAdjustmentColumnWidth.Value);
    }
}
