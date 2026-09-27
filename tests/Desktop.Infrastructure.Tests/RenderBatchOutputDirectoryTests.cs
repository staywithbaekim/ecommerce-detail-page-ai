using Desktop.Application.Inputs;
using Desktop.Application.Rendering;
using Desktop.Application.Tasks;
using Desktop.Domain.Inputs;
using Desktop.Domain.Templates;
using Desktop.Infrastructure.Bridge;
using Desktop.Infrastructure.Persistence;
using System.Text.Json;

namespace Desktop.Infrastructure.Tests;

public sealed class RenderBatchOutputDirectoryTests
{
    [Fact]
    public async Task ReadsExactPersistedRootWithoutRecoveryOrOutputWritesIncludingOlderSuccess()
    {
        using var directory = new TestDirectory();
        await using var store = new SqliteDesktopStore(new(directory.Path));
        await store.InitializeAsync();
        var rootA = Directory.CreateDirectory(Path.Combine(directory.Path, "输出 A & 原位")).FullName;
        var rootB = Directory.CreateDirectory(Path.Combine(directory.Path, "output-b")).FullName;
        await SeedBatch(store, "batch_a", rootA);
        await SeedBatch(store, "batch_b", rootB);
        var sentinel = Path.Combine(rootA, "existing.txt");
        await File.WriteAllTextAsync(sentinel, "preserve existing output");
        var before = JsonSerializer.Serialize(await store.ListRecoverableAsync(default));
        var journals = new ReadOnlyJournals();
        var committed = Committed("batch_a");
        journals.Journal = new(1, "batch_a", 3, RenderBatchStatus.RecoveryRequired, false, false,
            [committed, committed with { Attempt = 2, Status = RenderBatchPageStatus.Unknown, Commit = null }]);
        var reader = new RenderBatchOutputReader(new ForbiddenRecoveryRepository(), journals, store, store);

        Assert.Equal(rootA, await reader.GetOutputDirectoryAsync("batch_a", default));
        journals.Journal = new(1, "batch_b", 2, RenderBatchStatus.Completed, false, false, [Committed("batch_b")]);
        Assert.Equal(rootB, await reader.GetOutputDirectoryAsync("batch_b", default));

        Assert.Equal("batch_a", journals.Requested[0]);
        Assert.Equal("batch_b", journals.Requested[1]);
        Assert.Equal(before, JsonSerializer.Serialize(await store.ListRecoverableAsync(default)));
        Assert.Equal("preserve existing output", await File.ReadAllTextAsync(sentinel));
        Assert.Single(Directory.GetFiles(rootA));
        Assert.Empty(Directory.GetFileSystemEntries(rootB));
    }

    [Theory]
    [InlineData(RenderBatchPageStatus.Planned)]
    [InlineData(RenderBatchPageStatus.Blocked)]
    [InlineData(RenderBatchPageStatus.Failed)]
    [InlineData(RenderBatchPageStatus.Cancelled)]
    [InlineData(RenderBatchPageStatus.Unknown)]
    [InlineData(RenderBatchPageStatus.Committed)]
    public async Task NoCommittedReceiptMeansNoOutputEvenWhenInputIsScanReady(RenderBatchPageStatus status)
    {
        using var directory = new TestDirectory();
        await using var store = new SqliteDesktopStore(new(directory.Path));
        await store.InitializeAsync();
        await SeedBatch(store, "batch", Path.Combine(directory.Path, "not-generated"));
        var journals = new ReadOnlyJournals
        {
            Journal = new(1, "batch", 1, RenderBatchStatus.Prepared, false, false,
                [Committed("batch") with { Status = status, Commit = null }])
        };
        var reader = new RenderBatchOutputReader(new ForbiddenRecoveryRepository(), journals, store, store);
        Assert.Null(await reader.GetOutputDirectoryAsync("batch", default));
        journals.Journal = null;
        Assert.Null(await reader.GetOutputDirectoryAsync("batch", default));
        Assert.False(Directory.Exists(Path.Combine(directory.Path, "not-generated")));
    }

    [Theory]
    [InlineData("relative")]
    [InlineData(@"C:relative")]
    [InlineData(@"\\server\share")]
    [InlineData(@"\\?\C:\output")]
    [InlineData("shell:Downloads")]
    [InlineData(@"C:\output:stream")]
    [InlineData("C:\\output\" & calc.exe")]
    [InlineData(@"C:\output\*")]
    public async Task RejectsUnsafePersistedRootsInsteadOfInferringFromArtifacts(string root)
    {
        using var directory = new TestDirectory();
        await using var store = new SqliteDesktopStore(new(directory.Path));
        await store.InitializeAsync();
        await SeedBatch(store, "batch", root);
        var reader = new RenderBatchOutputReader(new ForbiddenRecoveryRepository(), CommittedJournal("batch"), store, store);
        await Assert.ThrowsAsync<InvalidDataException>(() => reader.GetOutputDirectoryAsync("batch", default));
    }

    [Fact]
    public async Task OfflineDirectoryIsReportedAndNeverCreated()
    {
        using var directory = new TestDirectory();
        await using var store = new SqliteDesktopStore(new(directory.Path));
        await store.InitializeAsync();
        var root = Path.Combine(directory.Path, "offline");
        await SeedBatch(store, "batch", root);
        var reader = new RenderBatchOutputReader(new ForbiddenRecoveryRepository(), CommittedJournal("batch"), store, store);
        await Assert.ThrowsAsync<DirectoryNotFoundException>(() => reader.GetOutputDirectoryAsync("batch", default));
        Assert.False(Directory.Exists(root));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("unconfirmed")]
    [InlineData("journal-mismatch")]
    public async Task MissingUnconfirmedOrMismatchedBatchFailsClosed(string failure)
    {
        using var directory = new TestDirectory();
        await using var store = new SqliteDesktopStore(new(directory.Path));
        await store.InitializeAsync();
        if (failure != "missing") await SeedBatch(store, "batch", directory.Path, ready: failure != "unconfirmed");
        var journals = CommittedJournal(failure == "journal-mismatch" ? "other" : "batch");
        var reader = new RenderBatchOutputReader(new ForbiddenRecoveryRepository(), journals, store, store);
        await Assert.ThrowsAsync<InvalidDataException>(() => reader.GetOutputDirectoryAsync("batch", default));
    }

    private static async Task SeedBatch(SqliteDesktopStore store, string batchId, string outputRoot, bool ready = true)
    {
        var now = DateTimeOffset.UtcNow;
        var group = TemplateGroup.Create("group_" + batchId, "output lookup fixture", now);
        var revisionId = "revision_" + batchId;
        var page = TemplatePage.Create("page", revisionId, 0, "page", TemplatePageContentType.NormalComposition,
            TemplateGenerationMode.OncePerProduct, null, true, "resource");
        await store.SaveGroupAsync(group, default);
        await store.SaveDraftRevisionAsync(TemplateRevision.CreateDraft(revisionId, group.GroupId, 1, now, [page]), default);
        var snapshot = BatchTemplateSnapshot.Restore("snapshot_" + batchId, batchId, revisionId,
            [new("page", 0, TemplatePageContentType.NormalComposition, TemplateGenerationMode.OncePerProduct, null, "resource", [])],
            "sha256:" + new string('a', 64), now);
        var batch = BatchDraft.Create(batchId, @"C:\source-not-read", outputRoot, snapshot, BatchSelectionMode.Automatic, false, now);
        if (ready)
        {
            var styleId = "style_" + batchId;
            var assetId = "asset_" + batchId;
            batch = batch.BeginScanning(now).MarkScanReady(BatchInputType.SingleStyle,
                [StyleDraft.Create(styleId, "style", "", 0, [assetId])],
                [SourceAssetMetadata.CreateReadable(assetId, styleId, "source.jpg", "source.jpg", 128,
                    new string('b', 64), 100, 100, SourceImageFormat.Jpeg, "image/jpeg")],
                "batches/" + batchId + "/manifest.json", "sha256:" + new string('c', 64), now);
        }
        await store.SaveAsync(batch, default);
    }

    private static RenderBatchPage Committed(string batchId)
    {
        var checkpoint = new TaskCheckpoint(batchId, "style_" + batchId, "page", "render_output", 1,
            "sha256:" + new string('a', 64), "execution", OutputCommitState.Committed, DateTimeOffset.UtcNow);
        return new(checkpoint.StyleId, "page", null, 1, null, RenderBatchPageStatus.Committed, "COMMITTED",
            Commit: new(checkpoint, "render:test", "outputs/unrelated-artifact/manifest.json", 1, new string('b', 64)));
    }

    private static ReadOnlyJournals CommittedJournal(string batchId) => new()
    { Journal = new(1, batchId, 1, RenderBatchStatus.Completed, false, false, [Committed(batchId)]) };

    private sealed class ReadOnlyJournals : IRenderBatchStore
    {
        public RenderBatchJournal? Journal { get; set; }
        public List<string> Requested { get; } = [];
        public Task<RenderBatchJournal?> LoadRenderBatchAsync(string batchId, CancellationToken cancellationToken)
        { Requested.Add(batchId); return Task.FromResult(Journal); }
        public Task SaveRenderBatchAsync(RenderBatchJournal journal, long expectedRevision, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Directory lookup must not write render state");
    }

    private sealed class ForbiddenRecoveryRepository : IBatchRepository
    {
        public Task<BatchDraft?> GetAsync(string batchId, CancellationToken cancellationToken) => throw new InvalidOperationException("Recovery is not a read-only lookup");
        public Task SaveAsync(BatchDraft draft, CancellationToken cancellationToken) => throw new InvalidOperationException();
        public Task<IReadOnlyList<BatchDraft>> ListRecoverableAsync(CancellationToken cancellationToken) => throw new InvalidOperationException();
        public Task DeleteDraftAsync(string batchId, CancellationToken cancellationToken) => throw new InvalidOperationException();
    }
}
