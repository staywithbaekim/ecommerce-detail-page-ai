using Desktop.Application.Ai;
using Desktop.Application.SizeTables;
using Desktop.Application.Rendering;

namespace Desktop.App.UiTests;

public sealed class SizeTableEditorViewModelTests
{
    [Fact]
    public async Task NavigationInvalidatesLateSizeEditorLoad()
    {
        var deferred = new TaskCompletionSource<SizeTableEditorLoadResult>();
        var workflow = new EditingWorkflow { LoadOverride = () => deferred.Task };
        using var editor = new SizeTableEditorViewModel(workflow, new EditorHost());
        var open = typeof(SizeTableEditorViewModel).GetMethod("OpenSizeTableEditorAsync",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var pending = Assert.IsAssignableFrom<Task>(open.Invoke(editor, [Page()]));
        Assert.True(editor.IsOpening);
        editor.ClosePageDialogs();
        deferred.SetResult(new(SizeTableEditorLoadStatus.Loaded, EditingWorkflow.Document, null));
        await pending;
        Assert.False(editor.IsOpening);
        Assert.False(editor.IsSizeTableEditorVisible);
        Assert.Null(editor.SizeTableEditor.SourceDocument);
        Assert.Null(workflow.LastSave);
    }

    [Fact]
    public void NavigationDismissesBusyEditorWithoutInterruptingSaveOrClearingDraft()
    {
        var workflow = new EditingWorkflow();
        using var editor = new SizeTableEditorViewModel(workflow, new EditorHost());
        editor.OpenSizeTableEditorCommand.Execute(Page());
        editor.SizeTableEditor.Rows[0].Cells[1].SourceText = "62";
        editor.SizeTableEditor.IsSaving = true;
        editor.ClosePageDialogs();
        Assert.False(editor.IsSizeTableEditorVisible);
        Assert.True(editor.SizeTableEditor.IsSaving);
        Assert.Equal("62", editor.SizeTableEditor.Rows[0].Cells[1].SourceText);
        Assert.Null(workflow.LastSave);
    }
    [Fact]
    public void PublicOutputFrozenPlanExplainsEditingBoundaryWithoutOpeningEditor()
    {
        var workflow = new EditingWorkflow();
        var host = new EditorHost { AllowRenderedEditing = false };
        using var editor = new SizeTableEditorViewModel(workflow, host,
            new FrozenRendering { Journal = Journal(RenderBatchPageStatus.Committed) });
        editor.OpenSizeTableEditorCommand.Execute(Page());
        Assert.False(editor.IsSizeTableEditorVisible);
        Assert.Contains("按款输出暂不支持生成后修改", host.AnalysisStatus, StringComparison.Ordinal);
        Assert.Null(workflow.LastSave);
    }

    [Theory]
    [InlineData(RenderBatchPageStatus.Committed)]
    [InlineData(RenderBatchPageStatus.Failed)]
    [InlineData(RenderBatchPageStatus.Unknown)]
    [InlineData(RenderBatchPageStatus.Planned)]
    public void FrozenRenderPlanCannotBeEditedAsIfRegenerationWouldUseNewSizeData(RenderBatchPageStatus status)
    {
        var workflow = new EditingWorkflow();
        var host = new EditorHost();
        var rendering = new FrozenRendering { Journal = Journal(status) };
        using var editor = new SizeTableEditorViewModel(workflow, host, rendering);
        editor.OpenSizeTableEditorCommand.Execute(Page());
        Assert.False(editor.IsSizeTableEditorVisible);
        Assert.Contains("重新生成仅使用原计划", host.AnalysisStatus, StringComparison.Ordinal);
        Assert.Null(workflow.LastSave);
        Assert.Null(host.RefreshedDocument);
    }

    [Fact]
    public void SaveRechecksAPlanFrozenAfterTheEditorOpenedWithoutChangingTheTable()
    {
        var workflow = new EditingWorkflow();
        var host = new EditorHost();
        var rendering = new FrozenRendering();
        using var editor = new SizeTableEditorViewModel(workflow, host, rendering);
        editor.OpenSizeTableEditorCommand.Execute(Page());
        editor.SizeTableEditor.Rows[0].Cells[1].SourceText = "62";
        rendering.Journal = Journal(RenderBatchPageStatus.Planned);
        editor.SaveSizeTableCommand.Execute(null);
        Assert.Null(workflow.LastSave); Assert.Null(host.RefreshedDocument);
        Assert.Equal(3, editor.SizeTableEditor.SourceDocument!.Revision);
        Assert.Equal("62", editor.SizeTableEditor.Rows[0].Cells[1].SourceText);
        Assert.Contains("冻结", editor.SizeTableEditor.StatusMessage, StringComparison.Ordinal);
        Assert.False(editor.SizeTableEditor.IsSaving);
    }

    private static RenderBatchJournal Journal(RenderBatchPageStatus status) => new(1, "batch_size", 1,
        RenderBatchStatus.Prepared, false, false,
        [new("style_size", "page_size", null, 1, null, status, "test")]);

    [Fact]
    public void UnreadableRenderJournalDoesNotOpenAnUnsafeEditor()
    {
        var workflow = new EditingWorkflow(); var host = new EditorHost();
        using var editor = new SizeTableEditorViewModel(workflow, host, new FrozenRendering { FailLoad = true });
        editor.OpenSizeTableEditorCommand.Execute(Page());
        Assert.False(editor.IsSizeTableEditorVisible); Assert.Null(workflow.LastSave);
        Assert.Contains("无法核对", host.AnalysisStatus, StringComparison.Ordinal);
    }

    private sealed class FrozenRendering : IRenderBatchWorkflow
    {
        public RenderBatchJournal? Journal { get; set; }
        public bool FailLoad { get; init; }
        public Task<RenderBatchJournal?> LoadAsync(string batchId, CancellationToken cancellationToken)
        {
            if (FailLoad) throw new System.IO.IOException("test");
            Assert.Equal("batch_size", batchId); return Task.FromResult(Journal);
        }
        public Task<RenderBatchJournal> PrepareAsync(AiRenderPlanPreparation preparation, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<RenderBatchJournal> PrepareRegenerationAsync(string batchId, string styleId, string pageId,
            string? outputVariantId, int expectedAttempt, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<RenderBatchJournal> RunAsync(string batchId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task RequestPauseAsync(string batchId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task RequestCancelAsync(string batchId, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    [Theory]
    [InlineData(SizeTableEditorLoadStatus.NotFound, "尚无可编辑")]
    [InlineData(SizeTableEditorLoadStatus.RecoveryRequired, "记录损坏")]
    public void MissingOrCorruptSourceReportsStatusWithoutOpeningEditor(
        SizeTableEditorLoadStatus status, string message)
    {
        var workflow = new EditingWorkflow { LoadStatus = status };
        var host = new EditorHost();
        using var editor = new SizeTableEditorViewModel(workflow, host);

        editor.OpenSizeTableEditorCommand.Execute(Page());

        Assert.False(editor.IsSizeTableEditorVisible);
        Assert.Null(editor.SizeTableEditor.SourceDocument);
        Assert.Contains(message, host.AnalysisStatus, StringComparison.Ordinal);
    }

    [Fact]
    public void SaveUsesLoadedIdentityAndRevisionBeforeRefreshingOnlyTheSavedPage()
    {
        var workflow = new EditingWorkflow();
        var host = new EditorHost();
        using var editor = new SizeTableEditorViewModel(workflow, host);
        editor.OpenSizeTableEditorCommand.Execute(Page());
        Assert.True(editor.IsSizeTableEditorVisible);
        Assert.Equal("source-preview.png", editor.SizeTableEditor.SourcePreviewPath);
        editor.SizeTableEditor.Rows[0].Cells[1].SourceText = "62";
        host.BatchId = "another_batch";
        host.OnRefresh = saved => Assert.Same(saved, editor.SizeTableEditor.SourceDocument);

        editor.SaveSizeTableCommand.Execute(null);

        Assert.NotNull(workflow.LastSave);
        Assert.Equal(("batch_size", "style_size", "page_size", 3L),
            (workflow.LastSave.BatchId, workflow.LastSave.StyleId,
                workflow.LastSave.PageId, workflow.LastSave.ExpectedRevision));
        Assert.Equal("62", workflow.LastSave.Draft.Rows[0].Cells[1]);
        Assert.NotNull(host.RefreshedDocument);
        Assert.Equal(4, host.RefreshedDocument.Revision);
        Assert.Equal("page_size", host.RefreshedDocument.PageId);
        Assert.False(editor.SizeTableEditor.IsSaving);
        Assert.False(editor.SaveSizeTableCommand.CanExecute(null));
        editor.CloseSizeTableEditorCommand.Execute(null);
        Assert.False(editor.IsSizeTableEditorVisible);
    }

    [Fact]
    public void RevisionConflictKeepsEditsAndDoesNotRefreshOrAdvanceRevision()
    {
        var workflow = new EditingWorkflow { SaveStatus = SizeTableEditorSaveStatus.RevisionConflict };
        var host = new EditorHost();
        using var editor = new SizeTableEditorViewModel(workflow, host);
        editor.OpenSizeTableEditorCommand.Execute(Page());
        editor.SizeTableEditor.Rows[0].Cells[1].SourceText = "62";

        editor.SaveSizeTableCommand.Execute(null);

        Assert.Equal(3, editor.SizeTableEditor.SourceDocument!.Revision);
        Assert.Equal("62", editor.SizeTableEditor.Rows[0].Cells[1].SourceText);
        Assert.Null(host.RefreshedDocument);
        Assert.Contains("数据已在其他操作中更新", editor.SizeTableEditor.StatusMessage, StringComparison.Ordinal);
        Assert.False(editor.SizeTableEditor.IsSaving);
        Assert.True(editor.SaveSizeTableCommand.CanExecute(null));
    }

    [Fact]
    public void RefreshFailureKeepsSuccessfullySavedRevisionAndClearsBusyState()
    {
        var workflow = new EditingWorkflow();
        var host = new EditorHost { OnRefresh = _ => throw new InvalidOperationException("TEST_RENDER_FAILURE") };
        using var editor = new SizeTableEditorViewModel(workflow, host);
        editor.OpenSizeTableEditorCommand.Execute(Page());
        editor.SizeTableEditor.Rows[0].Cells[1].SourceText = "62";

        editor.SaveSizeTableCommand.Execute(null);

        Assert.Equal(4, editor.SizeTableEditor.SourceDocument!.Revision);
        Assert.Contains("已保存的数据可恢复", editor.SizeTableEditor.StatusMessage, StringComparison.Ordinal);
        Assert.False(editor.SizeTableEditor.IsSaving);
        Assert.False(editor.SaveSizeTableCommand.CanExecute(null));
    }

    private static ResultPageCardViewModel Page() => ResultPageCardViewModel.Create(
        "款式", null,
        new AiRenderPagePreparation("style_size", "page_size",
            AiRenderPagePreparationStatus.ReadyToRender, "AI_SIZE_TABLE_VALID", null, null),
        AiRenderPreparationStatus.WaitingForPhotoshop);

    private sealed class EditingWorkflow : ISizeTableEditingWorkflow
    {
        public Func<Task<SizeTableEditorLoadResult>>? LoadOverride { get; init; }
        public static SizeTableEditorDocument Document => new(
            "table_size", "batch_size", "style_size", "page_size", "asset_size",
            "sha256:" + new string('a', 64), 3, SizeTableEditorStatus.Valid,
            [new("col_1", "尺码"), new("col_2", "衣长")],
            [new("row_1", ["L", "61"])]);

        public SizeTableEditorLoadStatus LoadStatus { get; init; } = SizeTableEditorLoadStatus.Loaded;
        public SizeTableEditorSaveStatus SaveStatus { get; init; } = SizeTableEditorSaveStatus.Saved;
        public SizeTableEditorSaveCommand? LastSave { get; private set; }

        public Task<SizeTableEditorLoadResult> LoadAsync(
            string batchId, string styleId, string pageId, CancellationToken cancellationToken)
        {
            Assert.Equal(("batch_size", "style_size", "page_size"), (batchId, styleId, pageId));
            if (LoadOverride is not null) return LoadOverride();
            return Task.FromResult(new SizeTableEditorLoadResult(
                LoadStatus, LoadStatus == SizeTableEditorLoadStatus.Loaded ? Document : null, null));
        }

        public Task<SizeTableEditorSaveResult> SaveAsync(
            SizeTableEditorSaveCommand command, CancellationToken cancellationToken)
        {
            LastSave = command;
            return Task.FromResult(new SizeTableEditorSaveResult(SaveStatus,
                SaveStatus == SizeTableEditorSaveStatus.Saved
                    ? Document with { Revision = 4, Columns = command.Draft.Columns, Rows = command.Draft.Rows }
                    : null, null));
        }
    }

    private sealed class EditorHost : ISizeTableEditorHost
    {
        public bool AllowRenderedEditing { get; init; } = true;
        public bool CanEditRenderedBatch(string batchId) => AllowRenderedEditing;
        public string? BatchId { get; set; } = "batch_size";
        public string AnalysisStatus { get; private set; } = "";
        public SizeTableEditorDocument? RefreshedDocument { get; private set; }
        public Action<SizeTableEditorDocument>? OnRefresh { get; set; }

        public string? GetSourcePreviewPath(string styleId, string sourceAssetId)
        {
            Assert.Equal(("style_size", "asset_size"), (styleId, sourceAssetId));
            return "source-preview.png";
        }

        public void ShowAnalysisStatus(string message) => AnalysisStatus = message;

        public Task RefreshSavedPageAsync(SizeTableEditorDocument saved)
        {
            RefreshedDocument = saved;
            OnRefresh?.Invoke(saved);
            return Task.CompletedTask;
        }
    }
}
