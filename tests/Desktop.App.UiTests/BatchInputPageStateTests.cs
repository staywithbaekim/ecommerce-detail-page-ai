namespace Desktop.App.UiTests;

public sealed class BatchInputPageStateTests
{
    [Fact]
    public void InitialStateRequiresTemplateDirectoriesAndPage()
    {
        var state = new Desktop.App.BatchInputPageState();

        Assert.Equal(Desktop.App.BatchInputUiStatus.Draft, state.Status);
        Assert.False(state.CanScanBatch);
        Assert.Equal("尚未选择商品目录", state.BatchInputDirectoryDisplay);
        Assert.Equal("尚未选择正式输出目录", state.BatchOutputDirectoryDisplay);
        Assert.Equal("已选择 0/0 个页面", state.BatchPageSelectionSummary);
    }

    [Fact]
    public void PageSelectionDialogCanCancelWithoutChangingCommittedSelection()
    {
        var state = new Desktop.App.BatchInputPageState();
        state.BatchPages.Add(new Desktop.App.BatchPageOptionViewModel("front", "正面", "图片", true));
        state.BatchPages.Add(new Desktop.App.BatchPageOptionViewModel("back", "背面", "图片", false));

        state.OpenPageSelection();
        state.BatchPages[0].IsSelected = false;
        state.BatchPages[1].IsSelected = true;
        state.CancelPageSelection();

        Assert.True(state.BatchPages[0].IsSelected);
        Assert.False(state.BatchPages[1].IsSelected);
        Assert.False(state.IsPageSelectionVisible);

        state.OpenPageSelection();
        state.BatchPages[1].IsSelected = true;
        state.ApplyPageSelection();
        Assert.Equal(2, state.SelectedBatchPageCount);

        state.OpenPageSelection();
        state.BatchPages[0].IsSelected = false;
        state.ClosePageSelection();
        Assert.False(state.BatchPages[0].IsSelected);
        Assert.False(state.IsPageSelectionVisible);
    }

    [Fact]
    public void ProjectionUpdatesOnlyPagePresentationFacts()
    {
        var state = new Desktop.App.BatchInputPageState
        {
            SelectedTemplate = new Desktop.App.TemplateLibraryCardViewModel(
                "template-01", "通用模板", "2 个页面", "今天", "Committed", true)
        };
        state.BatchPages.Add(new Desktop.App.BatchPageOptionViewModel("front", "正面", "图片", true));
        state.BatchPages.Add(new Desktop.App.BatchPageOptionViewModel("back", "背面", "图片", true));

        state.ApplyProjection(new Desktop.App.BatchInputProjection(
            "batch-01",
            "template-01",
            Desktop.App.BatchInputUiStatus.ScanReady,
            "D:\\input",
            "D:\\output",
            ["back"],
            Desktop.App.BatchSelectionMode.Manual,
            true,
            "已识别 1 款",
            "扫描完成"));

        Assert.Equal("batch-01", state.CurrentBatchId);
        Assert.Equal("已选择：input", state.BatchInputDirectoryDisplay);
        Assert.Equal("已选择：output", state.BatchOutputDirectoryDisplay);
        Assert.False(state.AutomaticSelection);
        Assert.True(state.IncludeEditablePsd);
        Assert.False(state.BatchPages[0].IsSelected);
        Assert.True(state.BatchPages[1].IsSelected);
        Assert.Equal(Desktop.App.BatchInputUiStatus.ScanReady, state.Status);
        Assert.False(state.CanScanBatch);
    }
}
