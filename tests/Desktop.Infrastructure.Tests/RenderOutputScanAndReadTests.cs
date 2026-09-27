using System.Text.Json;
using Desktop.Application.Rendering;
using Desktop.Infrastructure.Bridge;
using Desktop.Infrastructure.Inputs;

namespace Desktop.Infrastructure.Tests;

public sealed class RenderOutputScanAndReadTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ScanExcludesOnlyRegisteredStageAndPublishedIdentitiesAndReaderUsesExactStyle(bool unified)
    {
        using var fixture = new RenderStyleOutputRegistryTests.Fixture();
        await using var store = await fixture.Open();
        var journal = await RenderStyleOutputPublicationTests.Prepare(fixture, store, unified);
        var registration = (await store.LoadRenderOutputRegistrationAsync("batch_test", default))!;
        var inventory = new ReadOnlyFileInventory(store);
        var reader = new RenderBatchOutputReader(store, store, store, store);
        foreach (var style in registration.Styles)
        {
            Assert.Null(await reader.GetStyleOutputDirectoryAsync("batch_test", style.Target.StyleId, default));
            using (var exclusions = await OwnedOutputScanExclusions.OpenAsync(store, style.Target.ParentDirectory, default))
            {
                Assert.Contains(style.Target.StagingDirectory, exclusions.Paths);
                Assert.Throws<IOException>(() => Directory.Move(style.Target.StagingDirectory, style.Target.StagingDirectory + "-swapped"));
            }
            var scan = await inventory.ReadApprovedLevelsAsync(style.Target.ParentDirectory, default);
            Assert.True(scan.IsSuccess);
            Assert.DoesNotContain(scan.Inventory!.FirstLevelDirectories, dir => dir.Name == Path.GetFileName(style.Target.StagingDirectory));
            Assert.False((await inventory.ReadApprovedLevelsAsync(style.Target.StagingDirectory, default)).IsSuccess);
        }
        await new RenderStyleOutputPublisher(store, store).PublishAsync("batch_test", journal, default);
        var before = await fixture.Scalar("SELECT group_concat(payload_json,'|') FROM render_output_publications;");
        Assert.Null(await reader.GetOutputDirectoryAsync("batch_test", default)); // Never choose one of multiple styles or private evidence.
        foreach (var style in registration.Styles)
        {
            Assert.Equal(style.Target.FinalDirectory, await reader.GetStyleOutputDirectoryAsync("batch_test", style.Target.StyleId, default));
            var scan = await inventory.ReadApprovedLevelsAsync(style.Target.ParentDirectory, default);
            Assert.True(scan.IsSuccess);
            Assert.DoesNotContain(scan.Inventory!.FirstLevelDirectories, dir => dir.Name == style.Target.DirectoryName);
            Assert.False((await inventory.ReadApprovedLevelsAsync(style.Target.FinalDirectory, default)).IsSuccess);
        }
        Assert.Equal(before, await fixture.Scalar("SELECT group_concat(payload_json,'|') FROM render_output_publications;"));
    }

    [Theory]
    [InlineData("no-registration")]
    [InlineData("created-before-stage-fact")]
    [InlineData("replaced-stage")]
    [InlineData("replaced-final")]
    [InlineData("missing-stage-fact")]
    [InlineData("replaced-source")]
    public async Task SameNamesMarkersAndReplacedDirectoriesAreNotAutomaticallyExcluded(string state)
    {
        using var fixture = new RenderStyleOutputRegistryTests.Fixture();
        await using var store = await fixture.Open();
        var batch = await fixture.Seed(store, unified: true);
        var targets = RenderStyleOutputPlanner.Plan(batch);
        var target = targets[0];
        string candidate;
        if (state == "no-registration") candidate = Directory.CreateDirectory(target.FinalDirectory).FullName;
        else
        {
            using (await new RenderStyleOutputRegistry(store).PrepareAsync(batch.BatchId, default)) { }
            candidate = target.StagingDirectory;
            if (state is "created-before-stage-fact" or "missing-stage-fact")
                await fixture.Sql("DELETE FROM render_output_stages WHERE style_id='style_a';");
            if (state == "replaced-stage")
            {
                Directory.Move(candidate, candidate + "-original"); Directory.CreateDirectory(candidate);
            }
            if (state == "replaced-final")
            {
                // A convincing final name/marker without any publication fact is still user input.
                candidate = Directory.CreateDirectory(target.FinalDirectory).FullName;
            }
            if (state == "replaced-source")
            {
                Directory.Move(target.SourceDirectory, target.SourceDirectory + "-original"); Directory.CreateDirectory(target.SourceDirectory);
            }
        }
        File.WriteAllText(Path.Combine(candidate, "source.jpg"), "user material");
        File.WriteAllText(Path.Combine(candidate, "committed.marker"), "render-style-output/v1\nplausible-marker\n");
        var scan = await new ReadOnlyFileInventory(store).ReadApprovedLevelsAsync(target.ParentDirectory, default);
        Assert.True(scan.IsSuccess);
        Assert.Contains(scan.Inventory!.FirstLevelDirectories, dir => dir.Name == Path.GetFileName(candidate));
        Assert.Equal("user material", File.ReadAllText(Path.Combine(candidate, "source.jpg")));
    }

    [Theory]
    [InlineData("Verified")]
    [InlineData("Corrupt")]
    [InlineData("ChangedMedia")]
    public async Task ReadDoesNotRecoverPendingOrDamagedPublication(string state)
    {
        using var fixture = new RenderStyleOutputRegistryTests.Fixture();
        await using var store = await fixture.Open();
        var journal = await RenderStyleOutputPublicationTests.Prepare(fixture, store);
        var publisher = new RenderStyleOutputPublisher(store, store);
        if (state == "Verified")
        {
            await fixture.Sql("CREATE TRIGGER fail BEFORE UPDATE ON render_output_publications WHEN NEW.state='Committed' BEGIN SELECT RAISE(ABORT,'TEST_FAIL'); END;");
            await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() => publisher.PublishAsync("batch_test", journal, default));
            await fixture.Sql("DROP TRIGGER fail;");
        }
        else await publisher.PublishAsync("batch_test", journal, default);
        var target = (await store.LoadRenderOutputRegistrationAsync("batch_test", default))!.Styles[0].Target;
        if (state == "Corrupt") await fixture.Sql("UPDATE render_output_publications SET payload_sha256='bad' WHERE style_id='style_a';");
        if (state == "ChangedMedia") File.WriteAllText(Path.Combine(target.FinalDirectory, "01-detail.jpg"), "changed bytes");
        var before = await fixture.Scalar("SELECT group_concat(payload_json||payload_sha256,'|') FROM render_output_publications;");
        var reader = new RenderBatchOutputReader(store, store, store, store);
        if (state == "Verified") Assert.Null(await reader.GetStyleOutputDirectoryAsync("batch_test", "style_a", default));
        else await Assert.ThrowsAsync<InvalidDataException>(() => reader.GetStyleOutputDirectoryAsync("batch_test", "style_a", default));
        var scan = await new ReadOnlyFileInventory(store).ReadApprovedLevelsAsync(target.ParentDirectory, default);
        if (state == "Corrupt") Assert.False(scan.IsSuccess);
        else
        {
            // Ownership, not editable bytes, governs scan exclusion. Corrupt output is never a new source photo.
            Assert.True(scan.IsSuccess);
            Assert.DoesNotContain(scan.Inventory!.FirstLevelDirectories, dir => dir.Name == target.DirectoryName);
        }
        Assert.Equal(before, await fixture.Scalar("SELECT group_concat(payload_json||payload_sha256,'|') FROM render_output_publications;"));
    }

    [Fact]
    public async Task PublishedStyleRemainsReadableWhenAnotherStyleLaterBecomesUnknownWithoutAnyNewPublication()
    {
        using var fixture = new RenderStyleOutputRegistryTests.Fixture();
        await using var store = await fixture.Open();
        var journal = await RenderStyleOutputPublicationTests.Prepare(fixture, store, skipFirstCommit: true);
        await new RenderStyleOutputPublisher(store, store).PublishAsync("batch_test", journal, default);
        journal = journal with { Revision = journal.Revision + 1, Status = RenderBatchStatus.RecoveryRequired,
            Pages = journal.Pages.Select(page => page.StyleId == "style_a" && page.PlanJson is not null
                ? page with { Status = RenderBatchPageStatus.Unknown, ExecutionId = "execution_unknown", ReasonCode = "RENDER_RECOVERY_REQUIRED" } : page).ToArray() };
        await store.SaveRenderBatchAsync(journal, journal.Revision - 1, default);
        var before = JsonSerializer.Serialize(journal);
        var reader = new RenderBatchOutputReader(store, store, store, store);
        Assert.NotNull(await reader.GetStyleOutputDirectoryAsync("batch_test", "style_b", default));
        Assert.Null(await reader.GetStyleOutputDirectoryAsync("batch_test", "style_a", default));
        Assert.Equal(before, JsonSerializer.Serialize(await store.LoadRenderBatchAsync("batch_test", default)));
    }
}
