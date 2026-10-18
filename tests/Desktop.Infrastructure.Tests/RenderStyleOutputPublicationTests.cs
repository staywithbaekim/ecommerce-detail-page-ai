using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Desktop.Application.Rendering;
using Desktop.Application.Tasks;
using Desktop.Domain.Tasks;
using Desktop.Infrastructure.Bridge;
using Desktop.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;

namespace Desktop.Infrastructure.Tests;

public sealed class RenderStyleOutputPublicationTests
{
    private static readonly JsonSerializerOptions PageJson = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NativeDirectoryRenameKeepsIdentityAndNeverReplacesAnExistingDestination(bool conflict)
    {
        using var fixture = new TestDirectory();
        var stagePath = Directory.CreateDirectory(Path.Combine(fixture.Path, "stage")).FullName;
        var final = Path.Combine(fixture.Path, "公开成品");
        File.WriteAllText(Path.Combine(stagePath, "a.jpg"), "stage sentinel");
        using var parent = RenderOutputDirectoryLease.Open(fixture.Path, true);
        using var stage = RenderOutputDirectoryLease.Open(stagePath, requireRenameAccess: true);
        var identity = stage.Identity;
        if (conflict)
        {
            Directory.CreateDirectory(final);
            File.WriteAllText(Path.Combine(final, "old.jpg"), "old sentinel");
            Assert.Throws<RenderOutputTargetException>(() => stage.RenameToChild(parent, "公开成品"));
            Assert.Equal(stagePath, stage.Path);
            Assert.Equal("old sentinel", File.ReadAllText(Path.Combine(final, "old.jpg")));
            Assert.Equal("stage sentinel", File.ReadAllText(Path.Combine(stagePath, "a.jpg")));
        }
        else
        {
            stage.RenameToChild(parent, "公开成品");
            Assert.Equal(final, stage.Path);
            stage.Verify();
            Assert.Equal(identity, stage.Identity);
            Assert.False(Directory.Exists(stagePath));
            Assert.Equal("stage sentinel", File.ReadAllText(Path.Combine(final, "a.jpg")));
            Assert.Throws<IOException>(() => Directory.Move(final, final + "-swapped"));
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task CommittedPageSetsPublishOnceToTheirExactStylesAndRecoverWithoutRewriting(bool unified, bool psd)
    {
        using var fixture = new RenderStyleOutputRegistryTests.Fixture();
        string snapshot;
        await using (var store = await fixture.Open())
        {
            var journal = await Prepare(fixture, store, unified, psd);
            var before = Files(fixture.Evidence);
            var publisher = new RenderStyleOutputPublisher(store, store);
            await publisher.PublishAsync(journal.BatchId, journal, default);
            await VerifyAll(fixture, store, psd);
            snapshot = JsonSerializer.Serialize(Files(fixture.Input).Concat(Files(fixture.Public)));
            var facts = await fixture.Scalar("SELECT group_concat(payload_json,'|') FROM render_output_publications;");
            await publisher.PublishAsync(journal.BatchId, journal, default);
            Assert.Equal(facts, await fixture.Scalar("SELECT group_concat(payload_json,'|') FROM render_output_publications;"));
            Assert.Equal(before, Files(fixture.Evidence));
        }
        await using var reopened = await fixture.Open();
        var restored = (await reopened.LoadRenderBatchAsync("batch_test", default))!;
        await new RenderStyleOutputPublisher(reopened, reopened).PublishAsync("batch_test", restored, default);
        Assert.Equal(snapshot, JsonSerializer.Serialize(Files(fixture.Input).Concat(Files(fixture.Public))));
        await VerifyAll(fixture, reopened, psd);
    }

    [Theory]
    [InlineData("before-intent")]
    [InlineData("file-creation")]
    [InlineData("file-written")]
    [InlineData("before-verified")]
    [InlineData("after-rename")]
    public async Task InterruptedPublicationUsesOnlyDurableOwnershipAndNeverRenders(string phase)
    {
        using var fixture = new RenderStyleOutputRegistryTests.Fixture();
        await using var store = await fixture.Open();
        var journal = await Prepare(fixture, store);
        var registration = (await store.LoadRenderOutputRegistrationAsync("batch_test", default))!;
        var target = registration.Styles[0].Target;
        var sql = phase switch
        {
            "before-intent" => "CREATE TRIGGER fail BEFORE INSERT ON render_output_publications BEGIN SELECT RAISE(ABORT,'TEST_FAIL'); END;",
            "file-creation" => "CREATE TRIGGER fail BEFORE INSERT ON render_output_publication_files BEGIN SELECT RAISE(ABORT,'TEST_FAIL'); END;",
            "file-written" => "CREATE TRIGGER fail BEFORE UPDATE ON render_output_publication_files BEGIN SELECT RAISE(ABORT,'TEST_FAIL'); END;",
            "before-verified" => "CREATE TRIGGER fail BEFORE UPDATE ON render_output_publications WHEN NEW.state='Verified' BEGIN SELECT RAISE(ABORT,'TEST_FAIL'); END;",
            _ => "CREATE TRIGGER fail BEFORE UPDATE ON render_output_publications WHEN NEW.state='Committed' BEGIN SELECT RAISE(ABORT,'TEST_FAIL'); END;"
        };
        await fixture.Sql(sql);
        var publisher = new RenderStyleOutputPublisher(store, store);
        await Assert.ThrowsAsync<SqliteException>(() => publisher.PublishAsync("batch_test", journal, default));
        if (phase == "after-rename")
        {
            Assert.True(Directory.Exists(target.FinalDirectory));
            Assert.False(Directory.Exists(target.StagingDirectory));
            Assert.Equal("Verified", (await store.LoadRenderOutputPublicationAsync("batch_test", target.StyleId, default))!.State);
        }
        else Assert.False(Directory.Exists(target.FinalDirectory));
        var internalBefore = Files(fixture.Evidence);
        var journalBefore = await fixture.Scalar("SELECT group_concat(payload_json,'|') FROM render_batch_journals;");
        await fixture.Sql("DROP TRIGGER fail;");
        if (phase == "file-creation")
        {
            Assert.Single(Directory.EnumerateFiles(target.StagingDirectory));
            Assert.Empty(await store.LoadRenderPublicFilesAsync("batch_test", target.StyleId, default));
            var before = Files(target.StagingDirectory);
            await Assert.ThrowsAsync<InvalidDataException>(() => publisher.PublishAsync("batch_test", journal, default));
            Assert.Equal(before, Files(target.StagingDirectory));
        }
        else
        {
            if (phase == "file-written")
            {
                var unfinished = Assert.Single(await store.LoadRenderPublicFilesAsync("batch_test", target.StyleId, default));
                Assert.False(unfinished.Verified);
                File.WriteAllText(Path.Combine(target.StagingDirectory, unfinished.FileName), "simulate interrupted bytes");
            }
            await publisher.PublishAsync("batch_test", journal, default);
            await VerifyAll(fixture, store, false);
        }
        Assert.Equal(internalBefore, Files(fixture.Evidence));
        Assert.Equal(journalBefore, await fixture.Scalar("SELECT group_concat(payload_json,'|') FROM render_batch_journals;"));
    }

    [Theory]
    [InlineData("jpg")]
    [InlineData("marker")]
    [InlineData("manifest")]
    [InlineData("checkpoint")]
    [InlineData("extra")]
    public async Task InternalPageEvidenceMustBeReverifiedBeforeAnyPublicFileIsCreated(string corruption)
    {
        using var fixture = new RenderStyleOutputRegistryTests.Fixture();
        await using var store = await fixture.Open();
        var journal = await Prepare(fixture, store);
        var page = journal.Pages.First(item => item.PlanJson is not null);
        var manifest = Path.Combine(fixture.Evidence, page.Commit!.ManifestRelativePath);
        var directory = Path.GetDirectoryName(manifest)!;
        if (corruption == "jpg") File.AppendAllText(Path.Combine(directory, "01-detail.jpg"), "changed");
        if (corruption == "marker") File.WriteAllText(Path.Combine(directory, "committed.marker"), "bad");
        if (corruption == "manifest") File.AppendAllText(manifest, " ");
        if (corruption == "extra") File.WriteAllText(Path.Combine(directory, "other.txt"), "not-owned");
        if (corruption == "checkpoint") await fixture.Sql("UPDATE render_page_checkpoints SET output_commit_state='Verified';");
        var before = Files(fixture.Evidence);
        await Assert.ThrowsAsync<InvalidDataException>(() => new RenderStyleOutputPublisher(store, store).PublishAsync("batch_test", journal, default));
        Assert.Equal("0", await fixture.Scalar("SELECT count(*) FROM render_output_publications;"));
        Assert.Equal(before, Files(fixture.Evidence));
        var registration = (await store.LoadRenderOutputRegistrationAsync("batch_test", default))!;
        Assert.All(registration.Styles, style => Assert.Empty(Directory.EnumerateFileSystemEntries(style.Target.StagingDirectory)));
    }

    [Theory]
    [InlineData("Unknown")]
    [InlineData("Running")]
    [InlineData("Submitting")]
    [InlineData("Planned")]
    public async Task UncertainOrIncompleteStyleDoesNotPublishEvenIfTheCallerHasOldCompletedProjection(string status)
    {
        using var fixture = new RenderStyleOutputRegistryTests.Fixture();
        await using var store = await fixture.Open();
        var journal = await Prepare(fixture, store, skipFirstCommit: true);
        var pages = journal.Pages.ToArray();
        pages[0] = pages[0] with { Status = Enum.Parse<RenderBatchPageStatus>(status), ExecutionId = status is "Running" or "Unknown" ? "execution_wait" : null };
        journal = journal with { Revision = journal.Revision + 1, Pages = pages };
        await store.SaveRenderBatchAsync(journal, journal.Revision - 1, default);
        var publisher = new RenderStyleOutputPublisher(store, store);
        if (status != "Planned") await Assert.ThrowsAsync<InvalidDataException>(() => publisher.PublishAsync("batch_test", journal, default));
        else await publisher.PublishAsync("batch_test", journal, default); // Independent complete second style can publish.
        Assert.Null(await store.LoadRenderOutputPublicationAsync("batch_test", "style_a", default));
        if (status != "Planned") Assert.Null(await store.LoadRenderOutputPublicationAsync("batch_test", "style_b", default));
    }

    [Theory]
    [InlineData("source-replaced")]
    [InlineData("stage-replaced")]
    [InlineData("file-replaced")]
    [InlineData("verified-file-changed")]
    [InlineData("unregistered-extra")]
    [InlineData("final-conflict")]
    public async Task ConflictsOrReplacedIdentitiesAreNeverAdoptedOrOverwritten(string mutation)
    {
        using var fixture = new RenderStyleOutputRegistryTests.Fixture();
        await using var store = await fixture.Open();
        var journal = await Prepare(fixture, store);
        var target = (await store.LoadRenderOutputRegistrationAsync("batch_test", default))!.Styles[0].Target;
        await fixture.Sql("CREATE TRIGGER fail BEFORE UPDATE ON render_output_publications WHEN NEW.state='Verified' BEGIN SELECT RAISE(ABORT,'TEST_FAIL'); END;");
        var publisher = new RenderStyleOutputPublisher(store, store);
        await Assert.ThrowsAsync<SqliteException>(() => publisher.PublishAsync("batch_test", journal, default));
        await fixture.Sql("DROP TRIGGER fail;");
        var media = Path.Combine(target.StagingDirectory, "01-detail.jpg");
        if (mutation is "source-replaced" or "stage-replaced")
        {
            var path = mutation == "source-replaced" ? target.SourceDirectory : target.StagingDirectory;
            Directory.Move(path, path + "-original"); Directory.CreateDirectory(path);
        }
        if (mutation == "file-replaced") { File.Move(media, media + ".original"); File.WriteAllBytes(media, File.ReadAllBytes(media + ".original")); }
        if (mutation == "verified-file-changed") File.WriteAllText(media, "changed");
        if (mutation == "unregistered-extra") File.WriteAllText(Path.Combine(target.StagingDirectory, "keep.txt"), "user data");
        if (mutation == "final-conflict") { Directory.CreateDirectory(target.FinalDirectory); File.WriteAllText(Path.Combine(target.FinalDirectory, "keep.txt"), "user data"); }
        var before = Files(fixture.Input);
        await Assert.ThrowsAsync<InvalidDataException>(() => publisher.PublishAsync("batch_test", journal, default));
        Assert.Equal(before, Files(fixture.Input));
    }

    [Theory]
    [InlineData("missing-file-fact")]
    [InlineData("bad-file-hash")]
    [InlineData("missing-stage-fact")]
    [InlineData("bad-publication-hash")]
    [InlineData("bad-state")]
    [InlineData("public-file-replaced")]
    [InlineData("public-directory-replaced")]
    public async Task CompletedPublicationRejectsCorruptionWithoutRepairingOrRewritingIt(string mutation)
    {
        using var fixture = new RenderStyleOutputRegistryTests.Fixture();
        await using var store = await fixture.Open();
        var journal = await Prepare(fixture, store);
        var publisher = new RenderStyleOutputPublisher(store, store);
        await publisher.PublishAsync("batch_test", journal, default);
        var publication = (await store.LoadRenderOutputPublicationAsync("batch_test", "style_a", default))!;
        if (mutation == "missing-file-fact") await fixture.Sql("DELETE FROM render_output_publication_files WHERE style_id='style_a';");
        if (mutation == "bad-file-hash") await fixture.Sql("UPDATE render_output_publication_files SET payload_sha256='bad' WHERE style_id='style_a';");
        if (mutation == "missing-stage-fact") await fixture.Sql("PRAGMA foreign_keys=OFF; DELETE FROM render_output_stages WHERE style_id='style_a';");
        if (mutation == "bad-publication-hash") await fixture.Sql("UPDATE render_output_publications SET payload_sha256='bad' WHERE style_id='style_a';");
        if (mutation == "bad-state") await fixture.Sql("UPDATE render_output_publications SET state='Verified' WHERE style_id='style_a';");
        if (mutation == "public-file-replaced")
        {
            var path = Path.Combine(publication.FinalDirectory, "01-detail.jpg");
            File.Move(path, path + ".original"); File.Copy(path + ".original", path);
        }
        if (mutation == "public-directory-replaced")
        {
            Directory.Move(publication.FinalDirectory, publication.FinalDirectory + ".original");
            Directory.CreateDirectory(publication.FinalDirectory);
            File.WriteAllText(Path.Combine(publication.FinalDirectory, "keep.txt"), "user data");
        }
        var before = Files(fixture.Input);
        await Assert.ThrowsAsync<InvalidDataException>(() => publisher.PublishAsync("batch_test", journal, default));
        Assert.Equal(before, Files(fixture.Input));
    }

    [Fact]
    public async Task StoredPublicationAndFileOwnershipAreImmutableAndCannotSkipOrDowngradeStates()
    {
        using var fixture = new RenderStyleOutputRegistryTests.Fixture();
        await using var store = await fixture.Open();
        var journal = await Prepare(fixture, store);
        await fixture.Sql("CREATE TRIGGER fail BEFORE UPDATE ON render_output_publications WHEN NEW.state='Verified' BEGIN SELECT RAISE(ABORT,'TEST_FAIL'); END;");
        var publisher = new RenderStyleOutputPublisher(store, store);
        await Assert.ThrowsAsync<SqliteException>(() => publisher.PublishAsync("batch_test", journal, default));
        await fixture.Sql("DROP TRIGGER fail;");
        var publication = (await store.LoadRenderOutputPublicationAsync("batch_test", "style_a", default))!;
        Assert.Equal("Copying", publication.State);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveRenderOutputPublicationAsync(publication with
        { State = "Committed", FinalIdentity = publication.StageIdentity }, default));
        var file = (await store.LoadRenderPublicFilesAsync("batch_test", "style_a", default))[0];
        await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveRenderPublicFileAsync(file with { Verified = false }, default));
        await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveRenderPublicFileAsync(file with
        { Identity = file.Identity with { Created = file.Identity.Created + 1 } }, default));
        await publisher.PublishAsync("batch_test", journal, default);
        var completed = (await store.LoadRenderOutputPublicationAsync("batch_test", "style_a", default))!;
        await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveRenderOutputPublicationAsync(completed with
        { State = "Verified", FinalIdentity = null }, default));
        await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveRenderOutputPublicationAsync(completed with
        { FinalDirectory = completed.FinalDirectory + "other" }, default));
        await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveRenderPublicFileAsync(file, default));
        await VerifyAll(fixture, store, false);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task V21MigrationPreservesV20RegistrationAndAllEarlierFactsWithTransactionalRollback(bool fail)
    {
        using var fixture = new RenderStyleOutputRegistryTests.Fixture();
        string input, registration, stages, journal;
        await using (var store = await fixture.Open())
        {
            var batch = await fixture.Seed(store);
            using (await new RenderStyleOutputRegistry(store).PrepareAsync(batch.BatchId, default)) { }
            input = JsonSerializer.Serialize(await store.GetAsync(batch.BatchId, default));
            registration = (await fixture.Scalar("SELECT payload_json||payload_sha256 FROM render_output_registrations;"));
            stages = await fixture.Scalar("SELECT group_concat(payload_json||payload_sha256,'|') FROM render_output_stages;");
            journal = await fixture.Scalar("SELECT group_concat(payload_json||payload_sha256,'|') FROM render_batch_journals;");
        }
        await fixture.Sql(LegacyMigrationFixture.RemoveTpl02Schema + "DROP TABLE render_output_publication_files; DROP TABLE render_output_publications; DELETE FROM schema_migrations WHERE version=21;"
            + (fail ? "CREATE TRIGGER fail_v21 BEFORE INSERT ON schema_migrations WHEN NEW.version=21 BEGIN SELECT RAISE(ABORT,'TEST_FAIL'); END;" : ""));
        var migrations = await fixture.Scalar("SELECT group_concat(version||checksum||applied_at,'|') FROM (SELECT * FROM schema_migrations ORDER BY version);");
        await using var reopened = new SqliteDesktopStore(fixture.Options);
        if (fail) await Assert.ThrowsAsync<SqliteException>(() => reopened.InitializeAsync());
        else
        {
            await reopened.InitializeAsync();
            Assert.Equal(input, JsonSerializer.Serialize(await reopened.GetAsync("batch_test", default)));
        }
        Assert.Equal(fail ? "20" : "29", await fixture.Scalar("SELECT max(version) FROM schema_migrations;"));
        Assert.Equal(fail ? "0" : "2", await fixture.Scalar("SELECT count(*) FROM sqlite_master WHERE type='table' AND name IN ('render_output_publications','render_output_publication_files');"));
        Assert.Equal(migrations, await fixture.Scalar("SELECT group_concat(version||checksum||applied_at,'|') FROM (SELECT * FROM schema_migrations WHERE version<=20 ORDER BY version);"));
        Assert.Equal(registration, await fixture.Scalar("SELECT payload_json||payload_sha256 FROM render_output_registrations;"));
        Assert.Equal(stages, await fixture.Scalar("SELECT group_concat(payload_json||payload_sha256,'|') FROM render_output_stages;"));
        Assert.Equal(journal, await fixture.Scalar("SELECT group_concat(payload_json||payload_sha256,'|') FROM render_batch_journals;"));
    }

    internal static async Task<RenderBatchJournal> Prepare(RenderStyleOutputRegistryTests.Fixture fixture, SqliteDesktopStore store,
        bool unified = false, bool psd = false, bool skipFirstCommit = false)
    {
        var batch = await fixture.Seed(store, unified, psd);
        using (await new RenderStyleOutputRegistry(store).PrepareAsync(batch.BatchId, default)) { }
        var tasks = new TaskCoordinator(store);
        await tasks.RecordAsync(TaskEvent.Create(batch.BatchId, 1, TaskEventType.TaskCreated));
        await tasks.RecordAsync(TaskEvent.Create(batch.BatchId, 2, TaskEventType.ReadyToRender));
        await tasks.RecordAsync(TaskEvent.Create(batch.BatchId, 3, TaskEventType.RenderingStarted));
        var initial = (await store.LoadRenderBatchAsync(batch.BatchId, default))!;
        var pages = initial.Pages.ToArray();
        for (var i = 0; i < pages.Length; i++)
        {
            var page = pages[i];
            if (page.PlanJson is null || i == 0 && skipFirstCommit) continue;
            using var json = JsonDocument.Parse(page.PlanJson);
            var plan = json.RootElement;
            var digest = plan.GetProperty("plan_digest").GetString()!;
            var execution = "execution_" + i;
            var checkpoint = new TaskCheckpoint(batch.BatchId, page.StyleId, page.PageId, "render_output", 1, digest,
                execution, OutputCommitState.Verified, DateTimeOffset.UnixEpoch);
            var jpg = RenderJpegValidatorTests.Jpeg();
            var files = new List<RenderCommittedFile> { new("jpg", "01-detail.jpg", jpg.Length, Hash(jpg)) };
            var psdBytes = Encoding.UTF8.GetBytes("fixture-only PSD receipt; not Photoshop acceptance");
            if (psd) files.Add(new("editable_psd", "01-detail.psd", psdBytes.Length, Hash(psdBytes)));
            var media = new RenderMediaEvidence(digest, new(3, 2, RenderJpegValidator.SrgbProfileSha256, 3144), 0, 0,
                files.Select(file => new RenderMediaFile(file.Kind, file.ByteLength, file.Sha256)).ToArray());
            var artifact = plan.GetProperty("output").GetProperty("artifact_set_id").GetString()!;
            var manifest = new RenderPageManifest("render-page/v1", "outputs", artifact, checkpoint, media, files);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(manifest, PageJson);
            var relative = "outputs/" + artifact + "/manifest.json";
            var commit = new RenderPageCommitCheckpoint(checkpoint, "render:" + digest[7..], relative, bytes.Length, Hash(bytes));
            var directory = Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(fixture.Evidence, relative))!).FullName;
            await File.WriteAllBytesAsync(Path.Combine(directory, "manifest.json"), bytes);
            await File.WriteAllBytesAsync(Path.Combine(directory, "01-detail.jpg"), jpg);
            if (psd) await File.WriteAllBytesAsync(Path.Combine(directory, "01-detail.psd"), psdBytes);
            await File.WriteAllTextAsync(Path.Combine(directory, "committed.marker"), "render-page-commit/v1\n" + commit.ManifestSha256 + "\n");
            await store.PrepareRenderCommitAsync(commit, default);
            await store.CompleteRenderCommitAsync(commit, default);
            pages[i] = page with { Status = RenderBatchPageStatus.Committed, ReasonCode = "RENDER_PAGE_COMMITTED", ExecutionId = execution, Commit = commit };
        }
        var journal = initial with { Revision = 2, Status = RenderBatchStatus.Running, Pages = pages };
        await store.SaveRenderBatchAsync(journal, 1, default);
        return journal;
    }

    private static async Task VerifyAll(RenderStyleOutputRegistryTests.Fixture fixture, SqliteDesktopStore store, bool psd)
    {
        var registration = (await store.LoadRenderOutputRegistrationAsync("batch_test", default))!;
        foreach (var style in registration.Styles)
        {
            var published = (await store.LoadRenderOutputPublicationAsync("batch_test", style.Target.StyleId, default))!;
            Assert.Equal(RenderStyleOutputPublicationData.Committed, published.State);
            Assert.Equal(published.StageIdentity, published.FinalIdentity);
            Assert.False(Directory.Exists(style.Target.StagingDirectory));
            Assert.Equal(psd ? 4 : 3, Directory.GetFiles(style.Target.FinalDirectory).Length);
            Assert.Equal("source sentinel", File.ReadAllText(Path.Combine(style.Target.SourceDirectory, "source.jpg")));
            Assert.All(await store.LoadRenderPublicFilesAsync("batch_test", style.Target.StyleId, default), file => Assert.True(file.Verified));
            await new RenderStyleOutputPublisher(store, store).VerifyFileSetAsync(published, style.Target.FinalDirectory, default);
        }
        Assert.Equal("2", await fixture.Scalar("SELECT count(*) FROM render_output_publications WHERE state='Committed';"));
    }
    private static string[] Files(string root) => Directory.GetFiles(root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
        .Select(path => path + "|" + Hash(File.ReadAllBytes(path)) + "|" + File.GetLastWriteTimeUtc(path).Ticks).ToArray();
    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}
