using System.IO;
using Desktop.Application.Ai;
using Desktop.Application.Rendering;
using Desktop.Application.SizeTables;
using Desktop.Domain.SizeTables;

namespace Desktop.App.UiTests;

public sealed class SizeTableRepairViewModelTests
{
    [Fact]
    public void FrozenPageUsesExactRepairBaselineAndNeverSavesMutableCurrentOrPreparesWholeBatch()
    {
        var workflow = new Workflow(); var host = new Host();
        using var editor = new SizeTableEditorViewModel(workflow, host, workflow);
        editor.OpenSizeTableEditorCommand.Execute(Page("blue"));
        Assert.True(editor.IsSizeTableEditorVisible);
        Assert.True(editor.SizeTableEditor.IsRepair);
        Assert.Equal("blue", workflow.LoadedVariant);
        Assert.Equal("保存修复并生成当前页", editor.SizeTableEditor.SaveButtonText);
        editor.SizeTableEditor.Rows[0].Cells[1].SourceText = "66";
        editor.SaveSizeTableCommand.Execute(null);
        var command = Assert.Single(workflow.Commands);
        Assert.Equal(workflow.Baseline.Target, command.Target);
        Assert.Equal("66", command.Draft.Rows[0].Cells[1]);
        Assert.Equal("blue", command.Target.OutputVariantId);
        Assert.Same(command, host.Continued);
        Assert.False(editor.IsSizeTableEditorVisible);
        Assert.Equal(1, editor.SizeTableEditor.SourceDocument!.Revision);
        Assert.False(editor.IsBusy);
    }

    [Fact]
    public async Task SavingLocksCloseStructureAndDuplicateSubmissionUntilPersistenceReturns()
    {
        var workflow = new Workflow { Gate = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        var host = new Host();
        using var editor = new SizeTableEditorViewModel(workflow, host, workflow);
        editor.OpenSizeTableEditorCommand.Execute(Page("blue"));
        editor.SizeTableEditor.Rows[0].Cells[1].SourceText = "66";
        editor.SaveSizeTableCommand.Execute(null);
        Assert.True(editor.IsBusy); Assert.False(editor.SizeTableEditor.CanEdit);
        Assert.False(editor.CloseSizeTableEditorCommand.CanExecute(null));
        Assert.False(editor.AddSizeTableRowCommand.CanExecute(null));
        Assert.False(editor.SaveSizeTableCommand.CanExecute(null));
        editor.CloseSizeTableEditorCommand.Execute(null);
        editor.AddSizeTableRowCommand.Execute(null);
        editor.SaveSizeTableCommand.Execute(null);
        Assert.Single(editor.SizeTableEditor.Rows);
        Assert.True(editor.IsSizeTableEditorVisible);
        Assert.Single(workflow.Commands);
        workflow.Gate.SetResult();
        await Until(() => !editor.IsBusy);
        Assert.NotNull(host.Continued);
        Assert.False(editor.IsSizeTableEditorVisible);
    }

    [Fact]
    public async Task LostResponseKeepsExactRequestAcrossCloseReopenAndDoesNotResubmitChangedDraft()
    {
        var workflow = new Workflow { FailOnce = true }; var host = new Host();
        using var editor = new SizeTableEditorViewModel(workflow, host, workflow);
        editor.OpenSizeTableEditorCommand.Execute(Page("blue"));
        editor.SizeTableEditor.Rows[0].Cells[1].SourceText = "66";
        editor.SaveSizeTableCommand.Execute(null);
        await Until(() => !editor.IsBusy);
        Assert.True(editor.SizeTableEditor.IsRepairPending);
        Assert.False(editor.SizeTableEditor.CanEdit);
        Assert.Equal("确认同一修复请求", editor.SizeTableEditor.SaveButtonText);
        var original = Assert.Single(workflow.Commands);
        editor.CloseSizeTableEditorCommand.Execute(null);
        editor.OpenSizeTableEditorCommand.Execute(Page("red"));
        Assert.False(editor.IsSizeTableEditorVisible);
        Assert.Contains("上一尺码修复请求", host.Message, StringComparison.Ordinal);
        editor.OpenSizeTableEditorCommand.Execute(Page("blue"));
        Assert.True(editor.IsSizeTableEditorVisible);
        // Even programmatic mutation cannot change the already submitted immutable command.
        editor.SizeTableEditor.Rows[0].Cells[1].SourceText = "999";
        editor.SaveSizeTableCommand.Execute(null);
        await Until(() => !editor.IsBusy);
        Assert.Equal(2, workflow.Commands.Count);
        Assert.Same(original, workflow.Commands[1]);
        Assert.Equal("66", host.Continued!.Draft.Rows[0].Cells[1]);
        Assert.False(editor.IsSizeTableEditorVisible);
    }

    [Fact]
    public void SwitchingBatchBeforeRepairSaveCannotSubmitToTheOldOrNewBatch()
    {
        var workflow = new Workflow(); var host = new Host();
        using var editor = new SizeTableEditorViewModel(workflow, host, workflow);
        editor.OpenSizeTableEditorCommand.Execute(Page("blue"));
        editor.SizeTableEditor.Rows[0].Cells[1].SourceText = "66";
        host.BatchId = "other";
        editor.SaveSizeTableCommand.Execute(null);
        Assert.Empty(workflow.Commands);
        Assert.Contains("当前批次已切换", editor.SizeTableEditor.StatusMessage, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("SIZE_REPAIR_AI_NOT_SETTLED")]
    [InlineData("SIZE_REPAIR_UNSUPPORTED_PAGE")]
    [InlineData("SIZE_REPAIR_NOT_ALLOWED")]
    public void UnsupportedOrUnknownFrozenPageCannotFallBackToCurrentEditing(string reason)
    {
        var workflow = new Workflow { LoadFailure = reason }; var host = new Host();
        using var editor = new SizeTableEditorViewModel(workflow, host, workflow);
        editor.OpenSizeTableEditorCommand.Execute(Page("blue"));
        Assert.False(editor.IsSizeTableEditorVisible);
        Assert.Contains("安全修复基线", host.Message, StringComparison.Ordinal);
        Assert.Empty(workflow.Commands);
    }

    private static async Task Until(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(10, timeout.Token);
    }

    private static ResultPageCardViewModel Page(string variant) => ResultPageCardViewModel.Create("style", null,
        new("style", "page", AiRenderPagePreparationStatus.ReadyToRender, "AI_SIZE_TABLE_VALID", null, null, variant),
        AiRenderPreparationStatus.ReadyToRender);

    private sealed class Host : ISizeTableEditorHost
    {
        public string? BatchId { get; set; } = "batch";
        public string Message { get; private set; } = "";
        public SizeTablePageRepairCommand? Continued { get; private set; }
        public string? GetSourcePreviewPath(string styleId, string sourceAssetId) => null;
        public void ShowAnalysisStatus(string message) => Message = message;
        public Task RefreshSavedPageAsync(SizeTableEditorDocument saved) => throw new InvalidOperationException("Must not prepare whole batch");
        public Task ContinueSizeTableRepairAsync(RenderBatchJournal journal, SizeTablePageRepairCommand command)
        { Continued = command; return Task.CompletedTask; }
    }

    private sealed class Workflow : IRenderBatchWorkflow, ISizeTablePageRepairWorkflow, ISizeTableEditingWorkflow
    {
        private static readonly DateTimeOffset Now = new(2026, 9, 24, 12, 0, 0, TimeSpan.Zero);
        private readonly SizeTable _table = SizeTable.FromCandidate("table", "batch", "style", "page",
            SizeTableCandidate.Create("candidate", SizeTableSource.Create("source", "sha256:" + new string('d', 64)),
                1m, ["尺码", "衣长"], [["L", "65"]]), Now);
        public List<SizeTablePageRepairCommand> Commands { get; } = [];
        public string? LoadedVariant { get; private set; }
        public SizeTableRepairBaseline Baseline { get; private set; } = null!;
        public TaskCompletionSource? Gate { get; init; }
        public bool FailOnce { get; init; }
        public string? LoadFailure { get; init; }
        public Task<RenderBatchJournal?> LoadAsync(string batchId, CancellationToken cancellationToken) =>
            Task.FromResult<RenderBatchJournal?>(new(1, batchId, 8, RenderBatchStatus.Completed, false, false, []));
        public Task<SizeTableRepairBaseline> LoadSizeTableRepairAsync(string batchId, string styleId, string pageId,
            string? outputVariantId, CancellationToken cancellationToken)
        {
            if (LoadFailure is not null) throw new InvalidOperationException(LoadFailure);
            LoadedVariant = outputVariantId;
            Baseline = new(new(batchId, styleId, pageId, outputVariantId, 8, 1, "sha256:" + new string('a', 64),
                _table.Revision, _table.DataDigest), SizeTableRepairData.ToEditorDocument(_table));
            return Task.FromResult(Baseline);
        }
        public async Task<RenderBatchJournal> PrepareSizeTableRepairAsync(SizeTablePageRepairCommand command, CancellationToken cancellationToken)
        {
            Commands.Add(command);
            if (Gate is not null) await Gate.Task;
            if (FailOnce && Commands.Count == 1) throw new IOException("lost response");
            var table = SizeTableRepairData.ApplyDraft(_table, command.Draft, Now.AddMinutes(1));
            var origin = new RenderSizeTableRepairOrigin(command.RequestId, SizeTableRepairData.RequestDigest(command),
                8, 1, command.Target.BasePlanDigest, SizeTableRepairData.Serialize(_table), SizeTableRepairData.Serialize(table));
            return new(3, "batch", 9, RenderBatchStatus.Prepared, false, false,
                [new("style", "page", command.Target.OutputVariantId, 2, null, RenderBatchPageStatus.Planned, "READY", SizeTableRepair: origin)], 0);
        }
        public Task<SizeTableEditorLoadResult> LoadAsync(string batchId, string styleId, string pageId, CancellationToken cancellationToken) => throw new InvalidOperationException("Must not load current");
        public Task<SizeTableEditorSaveResult> SaveAsync(SizeTableEditorSaveCommand command, CancellationToken cancellationToken) => throw new InvalidOperationException("Must not save current");
        public Task<RenderBatchJournal> PrepareAsync(AiRenderPlanPreparation preparation, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<RenderBatchJournal> PrepareRegenerationAsync(string batchId, string styleId, string pageId, string? outputVariantId, int expectedAttempt, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<RenderBatchJournal> RunAsync(string batchId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task RequestPauseAsync(string batchId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task RequestCancelAsync(string batchId, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
