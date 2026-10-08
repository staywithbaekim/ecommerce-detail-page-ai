using Desktop.Application.Psd;
using Desktop.Application.Rendering;
using Desktop.Application.Tasks;
using Desktop.Contracts.Bridge;
using Desktop.Domain.Tasks;
using Desktop.Infrastructure.Bridge;
using Desktop.Infrastructure.Files;
using Desktop.Infrastructure.Persistence;

namespace Desktop.Infrastructure.Tests;

public sealed partial class BridgeExecutionBrokerTests
{
    [Fact]
    public void FailedAtomicMetadataPublishRemovesOnlyItsPendingFile()
    {
        using var root = new TestDirectory();
        var target = Path.Combine(root.Path, "manifest.json");
        var unrelated = Path.Combine(root.Path, "unrelated.pending");
        File.WriteAllText(target, "existing metadata");
        File.WriteAllText(unrelated, "unrelated evidence");

        Assert.Throws<IOException>(() => RenderPageCommitter.WriteNew(target, [1, 2, 3, 4]));

        Assert.Equal("existing metadata", File.ReadAllText(target));
        Assert.Equal("unrelated evidence", File.ReadAllText(unrelated));
        Assert.DoesNotContain(Directory.GetFiles(root.Path), file =>
            Path.GetFileName(file).StartsWith("manifest.json.", StringComparison.Ordinal)
            && file.EndsWith(".pending", StringComparison.Ordinal));
    }

    [Fact]
    public async Task OutputPreviewRequiresCommittedIdentityAndKeepsVerifiedJpegLocked()
    {
        using var broker = Create(new MemoryJournal());
        var (fixture, execution) = await ValidatedPage(broker);
        await using var store = await RenderStore(fixture.Root);
        var result = await broker.CommitOutputAsync(execution, 1, store, default);
        var paths = new LocalAppPathPolicy(fixture.Root);
        await Assert.ThrowsAsync<InvalidDataException>(() => RenderPageCommitter.OpenCommittedJpegAsync(paths, store,
            result.Checkpoint with { ManifestSha256 = new string('0', 64) }, default));
        var jpeg = Path.Combine(fixture.Root, "outputs/artifact_page_01/01-detail.jpg");
        await using (var preview = await RenderPageCommitter.OpenCommittedJpegAsync(paths, store, result.Checkpoint, default))
        {
            Assert.True(preview.CanRead); Assert.False(preview.CanWrite);
            Assert.Throws<IOException>(() => File.Open(jpeg, FileMode.Open, FileAccess.Write, FileShare.Read));
        }
        await File.WriteAllTextAsync(jpeg, "corrupted fixture output");
        await Assert.ThrowsAsync<InvalidDataException>(() => RenderPageCommitter.OpenCommittedJpegAsync(paths, store, result.Checkpoint, default));
        Assert.Equal(1, (await new TaskCoordinator(store).LoadAsync("batch_01JEXAMPLE")).OutputCount);
    }

    [Fact]
    public async Task VerifiedPageCommitsNamedFilesAndDatabaseBeforeReportingCompleted()
    {
        var journal = new MemoryJournal();
        using var broker = Create(journal);
        var (fixture, claim) = await ValidatedPage(broker);
        await using var store = await RenderStore(fixture.Root);
        var sentinel = Path.Combine(fixture.Root, "outputs", "keep.txt");
        await File.WriteAllTextAsync(sentinel, "existing output");
        var sources = fixture.Hashes();
        var result = await broker.CommitOutputAsync(claim, 1, store, default);
        Assert.Equal(OutputCommitState.Committed, await store.ReadRenderCommitAsync(result.Checkpoint, default));
        Assert.Equal("completed", (await broker.QueryAsync(claim, default))!.State);
        Assert.Equal(1, (await new TaskCoordinator(store).LoadAsync("batch_01JEXAMPLE")).OutputCount);
        var final = Path.Combine(fixture.Root, "outputs", "artifact_page_01");
        Assert.Equal("01-detail.jpg|01-detail.psd|committed.marker|manifest.json",
            string.Join('|', Directory.GetFiles(final).Select(Path.GetFileName).Order()));
        Assert.Equal("existing output", await File.ReadAllTextAsync(sentinel));
        Assert.Equal(sources, fixture.Hashes());
        Assert.Empty(Directory.GetFiles(Path.Combine(fixture.Root, "outputs"), "*.part", SearchOption.AllDirectories));
        var recovered = await RenderPageCommitter.RecoverAsync(new LocalAppPathPolicy(fixture.Root), store,
            "outputs/artifact_page_01/manifest.json", default);
        Assert.True(recovered.Recovered);
        Assert.Equal(1, (await new TaskCoordinator(store).LoadAsync("batch_01JEXAMPLE")).OutputCount);
        using var restarted = Create(journal);
        await restarted.InitializeAsync(default);
        Assert.Equal("completed", (await restarted.QueryAsync(claim, default))!.State);
    }

    [Theory]
    [InlineData("prepared", true, false)]
    [InlineData("moved", true, false)]
    [InlineData("committed", true, false)]
    [InlineData("prepared", false, false)]
    [InlineData("moved", false, false)]
    [InlineData("committed", false, false)]
    [InlineData("prepared", true, true)]
    [InlineData("moved", true, true)]
    [InlineData("committed", true, true)]
    [InlineData("prepared", false, true)]
    [InlineData("moved", false, true)]
    [InlineData("committed", false, true)]
    public async Task SizeTableEvidenceSurvivesInterruptedCommitWithoutRerender(string phase, bool includePsd, bool sizeInsertion)
    {
        var journal = new MemoryJournal();
        using var broker = Create(journal);
        var (fixture, execution) = await ValidatedPage(broker, includePsd, sizeTable: true, sizeInsertion: sizeInsertion);
        var sourceHashes = fixture.Hashes();
        await using var store = await RenderStore(fixture.Root);
        await Assert.ThrowsAsync<IOException>(() => broker.CommitOutputAsync(execution, 1, new FaultStore(store, phase), default));
        var manifest = Assert.Single(Directory.GetFiles(Path.Combine(fixture.Root, "outputs"), "manifest.json", SearchOption.AllDirectories));
        var before = await File.ReadAllBytesAsync(manifest);
        using var restarted = Create(journal);
        await restarted.InitializeAsync(default);
        var recovered = await restarted.RecoverOutputAsync(execution, Path.GetRelativePath(fixture.Root, manifest),
            new LocalAppPathPolicy(fixture.Root), store, default);
        Assert.True(recovered.Recovered);
        Assert.Equal("completed", (await restarted.QueryAsync(execution, default))!.State);
        Assert.Null((await restarted.ClaimAsync("session_new", default)).Value!.Claim);
        var after = await File.ReadAllBytesAsync(Path.Combine(fixture.Root, recovered.Checkpoint.ManifestRelativePath));
        Assert.Equal(before, after);
        var media = System.Text.Json.Nodes.JsonNode.Parse(after)!["media"]!;
        if (!includePsd)
        {
            // ADR-014 2026-09-24/P0: new JPG-only executions do not produce PSD structural receipts.
            // Size facts remain bound by the persisted plan digest; do not fabricate an embedded-layer proof.
            Assert.Null(media["size_table"]);
            Assert.Equal(fixture.Plan["plan_digest"]!.GetValue<string>(), recovered.Checkpoint.Page.PlanDigest);
            Assert.Equal("size_commit_01", fixture.Plan["size_table"]!["size_table_id"]!.GetValue<string>());
            Assert.Equal(sourceHashes, fixture.Hashes());
            Assert.Equal(1, (await new TaskCoordinator(store).LoadAsync("batch_01JEXAMPLE")).OutputCount);
            return;
        }
        var size = media["size_table"]!;
        Assert.Equal("size_commit_01", size["size_table_id"]!.GetValue<string>());
        Assert.Equal("sha256:" + new string('c', 64), size["structured_data_digest"]!.GetValue<string>());
        Assert.Equal(fixture.Plan["resources"]![1]!["sha256"]!.GetValue<string>(), size["raster_sha256"]!.GetValue<string>());
        Assert.Equal(fixture.Plan["resources"]![0]!["sha256"]!.GetValue<string>(), size["template_sha256"]!.GetValue<string>());
        Assert.Equal("page_01", size["page_id"]!.GetValue<string>());
        Assert.Equal(sizeInsertion ? 258 : 257, size["embedded_layer_id"]!.GetValue<int>());
        Assert.Equal(sourceHashes, fixture.Hashes());
        Assert.Equal(1, (await new TaskCoordinator(store).LoadAsync("batch_01JEXAMPLE")).OutputCount);
    }

    [Theory]
    [InlineData("prepared", true)]
    [InlineData("moved", true)]
    [InlineData("committed", true)]
    [InlineData("prepared", false)]
    [InlineData("moved", false)]
    [InlineData("committed", false)]
    public async Task RecoveryClosesExactlyTheSamePageAtEachInterruptedBoundary(string phase, bool includePsd)
    {
        var journal = new MemoryJournal();
        using var broker = Create(journal);
        var (fixture, claim) = await ValidatedPage(broker, includePsd);
        await using var store = await RenderStore(fixture.Root);
        var fault = new FaultStore(store, phase);
        await Assert.ThrowsAsync<IOException>(() => broker.CommitOutputAsync(claim, 1, fault, default));
        Assert.Equal("unknown", (await broker.QueryAsync(claim, default))!.State);
        Assert.Equal(includePsd ? 2 : 1,
            Directory.GetFiles(Path.Combine(fixture.Root, "outputs"), "*.part", SearchOption.AllDirectories).Length);
        var manifest = Assert.Single(Directory.GetFiles(Path.Combine(fixture.Root, "outputs"), "manifest.json", SearchOption.AllDirectories));
        using var restarted = Create(journal);
        await restarted.InitializeAsync(default);
        var result = await restarted.RecoverOutputAsync(claim, Path.GetRelativePath(fixture.Root, manifest),
            new LocalAppPathPolicy(fixture.Root), store, default);
        Assert.True(result.Recovered);
        Assert.Equal(OutputCommitState.Committed, await store.ReadRenderCommitAsync(result.Checkpoint, default));
        Assert.Equal(1, (await new TaskCoordinator(store).LoadAsync("batch_01JEXAMPLE")).OutputCount);
        Assert.Single(await store.LoadEventsAsync("batch_01JEXAMPLE", default), e => e.Type == TaskEventType.PageCommitted);
        Assert.Equal("completed", (await restarted.QueryAsync(claim, default))!.State);
        Assert.Equal(includePsd, File.Exists(Path.Combine(fixture.Root, "outputs", "artifact_page_01", "01-detail.psd")));
    }

    [Theory]
    [InlineData("unprepared", true)]
    [InlineData("jpg", true)]
    [InlineData("marker", true)]
    [InlineData("manifest", true)]
    [InlineData("extra", true)]
    [InlineData("missing_marker", true)]
    [InlineData("unprepared", false)]
    [InlineData("jpg", false)]
    [InlineData("marker", false)]
    [InlineData("manifest", false)]
    [InlineData("extra", false)]
    [InlineData("missing_marker", false)]
    public async Task RecoveryNeverPublishesUnjournalledOrDamagedOutput(string mutation, bool includePsd)
    {
        using var broker = Create(new MemoryJournal());
        var (fixture, claim) = await ValidatedPage(broker, includePsd);
        await using var store = await RenderStore(fixture.Root);
        await Assert.ThrowsAsync<IOException>(() => broker.CommitOutputAsync(claim, 1,
            new FaultStore(store, mutation == "unprepared" ? mutation : "moved"), default));
        var manifest = Assert.Single(Directory.GetFiles(Path.Combine(fixture.Root, "outputs"), "manifest.json", SearchOption.AllDirectories));
        var directory = Path.GetDirectoryName(manifest)!;
        if (mutation == "jpg") await File.AppendAllTextAsync(Path.Combine(directory, "01-detail.jpg"), "changed");
        if (mutation == "marker") await File.WriteAllTextAsync(Path.Combine(directory, "committed.marker"), "invalid");
        if (mutation == "missing_marker") File.Move(Path.Combine(directory, "committed.marker"), Path.Combine(fixture.Root, "retained-marker"));
        if (mutation == "manifest") await File.AppendAllTextAsync(manifest, " ");
        if (mutation == "extra") await File.WriteAllTextAsync(Path.Combine(directory, "unrelated.txt"), "keep");
        await Assert.ThrowsAsync<InvalidDataException>(() => RenderPageCommitter.RecoverAsync(new LocalAppPathPolicy(fixture.Root),
            store, Path.GetRelativePath(fixture.Root, manifest), default));
        Assert.Equal(0, (await new TaskCoordinator(store).LoadAsync("batch_01JEXAMPLE")).OutputCount);
        Assert.True(File.Exists(manifest));
    }

    [Fact]
    public async Task ConcurrentExistingFinalDirectoryIsNeverOverwrittenOrAdopted()
    {
        using var broker = Create(new MemoryJournal());
        var (fixture, claim) = await ValidatedPage(broker);
        await using var store = await RenderStore(fixture.Root);
        var final = Path.Combine(fixture.Root, "outputs", "artifact_page_01");
        var fault = new FaultStore(store, "none", () =>
        {
            Directory.CreateDirectory(final);
            File.WriteAllText(Path.Combine(final, "user.txt"), "keep");
            return Task.CompletedTask;
        });
        await Assert.ThrowsAsync<InvalidDataException>(() => broker.CommitOutputAsync(claim, 1, fault, default));
        Assert.Equal("keep", await File.ReadAllTextAsync(Path.Combine(final, "user.txt")));
        Assert.Single(Directory.GetFiles(final));
        Assert.Equal(0, (await new TaskCoordinator(store).LoadAsync("batch_01JEXAMPLE")).OutputCount);
    }

    [Fact]
    public async Task CommitDoesNotBlockTransportAndDisconnectCannotDisposeItsLeases()
    {
        using var broker = Create(new MemoryJournal());
        var (fixture, claim) = await ValidatedPage(broker);
        await using var store = await RenderStore(fixture.Root);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var proceed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var commit = broker.CommitOutputAsync(claim, 1, new FaultStore(store, "none", async () =>
        {
            started.TrySetResult(); await proceed.Task;
        }), default);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            Assert.True((await broker.ClaimAsync("session_test", default).WaitAsync(TimeSpan.FromSeconds(2))).IsSuccess);
            await broker.CancelAsync(claim, default).WaitAsync(TimeSpan.FromSeconds(2));
            await broker.DisconnectAsync(default).WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal("unknown", (await broker.QueryAsync(claim, default))!.State);
        }
        finally { proceed.TrySetResult(); }
        var result = await commit;
        Assert.Equal(OutputCommitState.Committed, await store.ReadRenderCommitAsync(result.Checkpoint, default));
        Assert.Equal("completed", (await broker.QueryAsync(claim, default))!.State);
    }

    [Fact]
    public async Task JpgOnlyPublishesAndRecoversWithJpegReceiptOnly()
    {
        using var broker = Create(new MemoryJournal());
        var (fixture, execution) = await ValidatedPage(broker, includePsd: false);
        await using var store = await RenderStore(fixture.Root);
        var result = await broker.CommitOutputAsync(execution, 1, store, default);
        var final = Path.Combine(fixture.Root, "outputs", "artifact_page_01");
        Assert.Equal("01-detail.jpg|committed.marker|manifest.json",
            string.Join('|', Directory.GetFiles(final).Select(Path.GetFileName).Order()));
        var json = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(final, "manifest.json")))!;
        Assert.Single(json["files"]!.AsArray());
        Assert.False(json["media"]!.AsObject().ContainsKey("size_table"));
        Assert.Equal("jpg", Assert.Single(json["media"]!["files"]!.AsArray())!["kind"]!.GetValue<string>());
        var recovered = await RenderPageCommitter.RecoverAsync(new LocalAppPathPolicy(fixture.Root), store,
            result.Checkpoint.ManifestRelativePath, default);
        Assert.True(recovered.Recovered);
        Assert.Equal(1, (await new TaskCoordinator(store).LoadAsync("batch_01JEXAMPLE")).OutputCount);
    }

    [Fact]
    public async Task JpgOnlyValidationUsesOnlyJpegAndRejectsPsdReceipts()
    {
        using var broker = Create(new MemoryJournal());
        var (fixture, output, claim) = await PrepareOutput(broker, verificationPsd: true);
        await broker.ReportAsync("session_test", Report(claim, 2, "page_started", "running", "page_01"), default);
        var chunk = Chunk(claim, output, RenderJpegValidatorTests.Jpeg(width: 790, height: 1252));
        Assert.True((await broker.WriteOutputAsync("session_test", chunk, default)).IsSuccess);
        Assert.True((await broker.SealOutputAsync("session_test", Seal(chunk), default)).IsSuccess);
        await broker.ReportAsync("session_test", Report(claim, 3, "page_completed", "running", "page_01"), default);
        var evidence = await broker.ValidateOutputAsync(claim.ExecutionId, fixture.Sources[0].OwnedRelativePath,
            new UnexpectedInspection(), default);
        Assert.Equal("jpg", Assert.Single(evidence.Files).Kind);
        Assert.False((await broker.WriteOutputAsync("session_test", Chunk(claim, output, [56, 66, 80, 83, 0, 1, 0, 0])
            with { OutputKind = "verification_psd" }, default)).IsSuccess);
        await using var store = await RenderStore(fixture.Root);
        var committed = await broker.CommitOutputAsync(claim.ExecutionId, 1, store, default);
        Assert.Equal(OutputCommitState.Committed, await store.ReadRenderCommitAsync(committed.Checkpoint, default));
    }

    [Fact]
    public async Task ConfirmedThirdPageFailurePreservesCommittedPagesAndAllowsNextExecution()
    {
        var journal = new MemoryJournal();
        using var broker = Create(journal);
        var (first, firstExecution) = await ValidatedPage(broker);
        await using var store = await RenderStore(first.Root);
        var firstCommit = await broker.CommitOutputAsync(firstExecution, 1, store, default);
        var (second, secondExecution) = await ValidatedPage(broker, attempt: 2);
        var secondCommit = await broker.CommitOutputAsync(secondExecution, 1, store, default);
        var committedFiles = Directory.GetFiles(Path.Combine(first.Root, "outputs", "artifact_page_01"))
            .Concat(Directory.GetFiles(Path.Combine(second.Root, "outputs", "artifact_page_2")))
            .ToDictionary(path => path, path => Hash(File.ReadAllBytes(path)));

        var (failed, output, claim) = await PrepareOutput(broker, attempt: 3);
        var sourceHashes = failed.Hashes();
        var sentinel = Path.Combine(failed.Root, "outputs", "unrelated.txt");
        await File.WriteAllTextAsync(sentinel, "existing file");
        Assert.True((await broker.ReportAsync("session_test", Report(claim, 2, "page_started", "running", "page_03"), default)).IsSuccess);
        var failedEvent = new BridgeEventV1("event_confirmed_failure", 3, DateTimeOffset.UnixEpoch,
            "page_failed", claim.ExecutionId, "failed", "page_03", Error: BridgeWireErrors.Create("BRIDGE_PLAN_INVALID"));
        var report = new AdapterEventReport(claim.ClaimId, System.Text.Json.JsonSerializer.SerializeToElement(
            new BridgeWireEnvelope<BridgeEventV1>(BridgeProtocol.Version, "event", failedEvent), BridgeProtocol.JsonOptions));
        Assert.True((await broker.ReportAsync("session_test", report, default)).IsSuccess);
        Assert.True((await broker.ReportAsync("session_test", report, default)).IsSuccess);
        Assert.Equal("failed", (await broker.QueryAsync(claim.ExecutionId, default))!.State);
        Assert.False((await broker.WriteOutputAsync("session_test", Chunk(claim, output, [255, 216, 255, 217]), default)).IsSuccess);
        var handle = claim.Plan!.Value.GetProperty("body").GetProperty("payload").GetProperty("resource_bindings")[0]
            .GetProperty("session_handle").GetString()!;
        Assert.False((await broker.ReadResourceAsync("session_test", new(claim.ClaimId, claim.ExecutionId,
            claim.PlanDigest, handle, 0, 1), default)).IsSuccess);
        await Assert.ThrowsAsync<InvalidDataException>(() => broker.CommitOutputAsync(claim.ExecutionId, 1, store, default));
        Assert.False(Directory.Exists(Path.Combine(failed.Root, "outputs", "artifact_page_3")));
        Assert.Equal(sourceHashes, failed.Hashes());
        Assert.Equal("existing file", await File.ReadAllTextAsync(sentinel));
        AssertSourcesUnlocked();

        using (var restarted = Create(journal))
        {
            await restarted.InitializeAsync(default);
            Assert.Equal("failed", (await restarted.QueryAsync(claim.ExecutionId, default))!.State);
            Assert.False((await restarted.ClaimAsync("session_test", default)).Value!.HasWork);
        }
        var (_, _, next) = await PrepareOutput(broker, attempt: 4);
        Assert.NotEqual(claim.ExecutionId, next.ExecutionId);
        Assert.Equal("authorized", (await broker.QueryAsync(next.ExecutionId, default))!.State);
        Assert.Equal(OutputCommitState.Committed, await store.ReadRenderCommitAsync(firstCommit.Checkpoint, default));
        Assert.Equal(OutputCommitState.Committed, await store.ReadRenderCommitAsync(secondCommit.Checkpoint, default));
        Assert.Equal(2, (await new TaskCoordinator(store).LoadAsync("batch_01JEXAMPLE")).OutputCount);
        Assert.All(committedFiles, pair => Assert.Equal(pair.Value, Hash(File.ReadAllBytes(pair.Key))));
    }

    private async Task<(BridgeResourceLeaseTests.Fixture Fixture, string Execution)> ValidatedPage(BridgeExecutionBroker broker, bool includePsd = true, bool sizeTable = false, bool sizeInsertion = false, int attempt = 1, string? instanceTag = null)
    {
        var (fixture, output, claim) = await PrepareOutput(broker, includePsd, verificationPsd: false, sizeTable: sizeTable, sizeInsertion: sizeInsertion, attempt: attempt, instanceTag: instanceTag);
        var pageId = fixture.Plan["document"]!["page_id"]!.GetValue<string>();
        await broker.ReportAsync("session_test", Report(claim, 2, "page_started", "running", pageId), default);
        byte[] psd = [56, 66, 80, 83, 0, 1, 0, 0];
        var chunks = new List<AdapterOutputWrite> { Chunk(claim, output, RenderJpegValidatorTests.Jpeg(width: 790, height: 1252)) };
        if (includePsd) chunks.Add(Chunk(claim, output, psd) with { OutputKind = "editable_psd" });
        foreach (var chunk in chunks)
        {
            Assert.True((await broker.WriteOutputAsync("session_test", chunk, default)).IsSuccess);
            Assert.True((await broker.SealOutputAsync("session_test", Seal(chunk), default)).IsSuccess);
        }
        await broker.ReportAsync("session_test", Report(claim, 3, "page_completed", "running", pageId), default);
        IPsdInspectionPort inspector = includePsd ? new CommitInspection(fixture, psd) : new UnexpectedInspection();
        await broker.ValidateOutputAsync(claim.ExecutionId, fixture.Sources[0].OwnedRelativePath, inspector, default);
        return (fixture, claim.ExecutionId);
    }

    private static async Task<SqliteDesktopStore> RenderStore(string root)
    {
        var store = new SqliteDesktopStore(new(Path.Combine(root, "database")));
        await store.InitializeAsync();
        var coordinator = new TaskCoordinator(store);
        var seq = 0;
        foreach (var type in new[] { TaskEventType.TaskCreated, TaskEventType.ReadyToRender, TaskEventType.RenderingStarted })
            await coordinator.RecordAsync(TaskEvent.Create("batch_01JEXAMPLE", ++seq, type));
        return store;
    }

    private sealed class FaultStore(IRenderPageCommitStore inner, string phase, Func<Task>? afterPrepare = null) : IRenderPageCommitStore
    {
        public async Task PrepareRenderCommitAsync(RenderPageCommitCheckpoint checkpoint, CancellationToken token)
        {
            if (phase == "unprepared") throw new IOException("injected");
            await inner.PrepareRenderCommitAsync(checkpoint, token);
            if (afterPrepare is not null) await afterPrepare();
            if (phase == "prepared") throw new IOException("injected");
        }
        public async Task CompleteRenderCommitAsync(RenderPageCommitCheckpoint checkpoint, CancellationToken token)
        {
            if (phase == "moved") throw new IOException("injected");
            await inner.CompleteRenderCommitAsync(checkpoint, token);
            if (phase == "committed") throw new IOException("injected");
        }
        public Task<OutputCommitState?> ReadRenderCommitAsync(RenderPageCommitCheckpoint checkpoint, CancellationToken token) =>
            inner.ReadRenderCommitAsync(checkpoint, token);
    }

    private sealed class CommitInspection(BridgeResourceLeaseTests.Fixture fixture, byte[] psd, bool rejectOutput = false) : IPsdInspectionPort
    {
        public Task<PsdInspectionResult> InspectAsync(PsdInspectionRequest request, CancellationToken token)
        {
            var baseline = request.InternalPsdRelativePath == fixture.Sources[0].OwnedRelativePath;
            if (!baseline && rejectOutput)
                return Task.FromResult(PsdInspectionResult.Failure(new("TPL_SOURCE_UNREADABLE", "invalid", "repair", false)));
            var content = new PsdLayerContentEvidence(true, new string('a', 64), null, null, null, null);
            var smart = new PsdSmartObjectInspection("id", "source.jpg", "data", "jpg", "jpg", 8, false, [])
            { EmbeddedContentAvailable = true, EmbeddedContentSha256 = fixture.Plan["resources"]![1]!["sha256"]!.GetValue<string>(),
                TransformBox = fixture.Plan["size_table"] is null
                    ? [-5, 10, 795, 10, 795, 1210, -5, 1210] : [10, 20, 760, 20, 760, 320, 10, 320] };
            var layer = new PsdLayerInspection([1], 257, "product_frame", "smartobject", false, true, true,
                false, 255, 255, "norm", new(0, 0, 790, 1252), false, false, null, smart) { ContentEvidence = content };
            var insertion = fixture.Plan["size_table"]?["insertion"];
            PsdLayerInspection[] layers = [layer];
            if (insertion is not null)
            {
                var old = layer with { Kind = "pixel", Name = "old table", SmartObject = null,
                    Visible = baseline, EffectiveVisible = baseline };
                layers = baseline ? [old] : [old, layer with { Path = [2], LayerId = 258, Name = "new size table" }];
            }
            var inspection = new PsdInspectionDocument(baseline ? fixture.Plan["resources"]![0]!["sha256"]!.GetValue<string>() : Hash(psd),
                psd.Length, new(790, 1252, 8, "RGB"), layers.Length, layers.Count(item => item.SmartObject is not null), 0, 0, 0,
                (insertion is null ? fixture.Plan["targets"]![0]!["expected_layer"]!["structure_signature"]
                    : insertion["above_layer"]!["structure_signature"])!.GetValue<string>(), new(1, 1, 2), 1, null, layers);
            return Task.FromResult(PsdInspectionResult.Success(inspection));
        }
    }
}
