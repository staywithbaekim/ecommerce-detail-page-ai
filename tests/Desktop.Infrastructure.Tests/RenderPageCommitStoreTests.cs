using Desktop.Application.Rendering;
using Desktop.Application.Tasks;
using Desktop.Domain.Tasks;
using Desktop.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;

namespace Desktop.Infrastructure.Tests;

public sealed class RenderPageCommitStoreTests
{
    [Fact]
    public async Task PreparedJournalSurvivesReopenAndCompletionIsExactlyOnce()
    {
        using var directory = new TestDirectory();
        var options = new DesktopDataOptions(directory.Path);
        var expected = Checkpoint();
        await using (var store = new SqliteDesktopStore(options))
        {
            await Start(store);
            await store.PrepareRenderCommitAsync(expected, default);
            await store.PrepareRenderCommitAsync(expected, default);
            Assert.Equal(OutputCommitState.Verified, await store.ReadRenderCommitAsync(expected, default));
            Assert.Equal(0, (await new TaskCoordinator(store).LoadAsync("task_01")).OutputCount);
        }
        await using var reopened = new SqliteDesktopStore(options);
        await reopened.InitializeAsync();
        await reopened.CompleteRenderCommitAsync(expected, default);
        await reopened.CompleteRenderCommitAsync(expected, default);
        await reopened.PrepareRenderCommitAsync(expected, default);
        Assert.Equal(OutputCommitState.Committed, await reopened.ReadRenderCommitAsync(expected, default));
        Assert.Equal(1, (await new TaskCoordinator(reopened).LoadAsync("task_01")).OutputCount);
        Assert.Single(await reopened.LoadEventsAsync("task_01", default), e => e.Type == TaskEventType.PageCommitted);
    }

    [Theory]
    [InlineData("attempt")]
    [InlineData("variant")]
    [InlineData("execution")]
    [InlineData("digest")]
    [InlineData("manifest")]
    [InlineData("time")]
    [InlineData("path")]
    public async Task DifferentIdentityCannotAdoptOrReplacePreparedPage(string mutation)
    {
        using var directory = new TestDirectory();
        await using var store = new SqliteDesktopStore(new(directory.Path));
        await Start(store);
        var expected = Checkpoint();
        await store.PrepareRenderCommitAsync(expected, default);
        var changed = mutation switch
        {
            "attempt" => expected with { Page = expected.Page with { Attempt = 2 } },
            "variant" => expected with { Page = expected.Page with { OutputVariantId = "color_a" } },
            "execution" => expected with { Page = expected.Page with { BridgeExecutionId = "execution_02" } },
            "digest" => expected with { Page = expected.Page with { PlanDigest = "sha256:" + new string('c', 64) }, ManifestId = "render:" + new string('c', 64) },
            "manifest" => expected with { ManifestSha256 = new string('c', 64) },
            "time" => expected with { Page = expected.Page with { UpdatedAt = expected.Page.UpdatedAt.AddTicks(1) } },
            _ => expected with { ManifestRelativePath = "outputs/other/manifest.json" }
        };
        await Assert.ThrowsAsync<InvalidDataException>(() => store.ReadRenderCommitAsync(changed, default));
        await Assert.ThrowsAsync<InvalidDataException>(() => store.PrepareRenderCommitAsync(changed, default));
        await Assert.ThrowsAsync<InvalidDataException>(() => store.CompleteRenderCommitAsync(changed, default));
        Assert.Equal(OutputCommitState.Verified, await store.ReadRenderCommitAsync(expected, default));
    }

    [Fact]
    public async Task SameTemplatePageInTwoProductsCountsTwoOutputs()
    {
        using var directory = new TestDirectory();
        await using var store = new SqliteDesktopStore(new(directory.Path));
        await Start(store);
        var a = Checkpoint();
        var b = a with { Page = a.Page with { StyleId = "style_02", PlanDigest = "sha256:" + new string('c', 64) },
            ManifestId = "render:" + new string('c', 64), ManifestRelativePath = "outputs/second/manifest.json" };
        foreach (var checkpoint in new[] { a, b })
        {
            await store.PrepareRenderCommitAsync(checkpoint, default);
            await store.CompleteRenderCommitAsync(checkpoint, default);
        }
        Assert.Equal(2, (await new TaskCoordinator(store).LoadAsync("task_01")).OutputCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DatabaseFailureRollsBackManifestCheckpointAndTaskTogether(bool completing)
    {
        using var directory = new TestDirectory();
        var options = new DesktopDataOptions(directory.Path);
        await using var store = new SqliteDesktopStore(options);
        await Start(store);
        var expected = Checkpoint();
        if (completing) await store.PrepareRenderCommitAsync(expected, default);
        await Sql(options, completing
            ? "CREATE TRIGGER fail_render BEFORE UPDATE ON render_page_checkpoints BEGIN SELECT RAISE(ABORT, 'injected'); END;"
            : "CREATE TRIGGER fail_render BEFORE INSERT ON render_page_checkpoints BEGIN SELECT RAISE(ABORT, 'injected'); END;");
        await Assert.ThrowsAsync<SqliteException>(() => completing
            ? store.CompleteRenderCommitAsync(expected, default) : store.PrepareRenderCommitAsync(expected, default));
        Assert.Equal(completing ? OutputCommitState.Verified : null, await store.ReadRenderCommitAsync(expected, default));
        Assert.Equal(0, (await new TaskCoordinator(store).LoadAsync("task_01")).OutputCount);
        Assert.Equal(3, (await store.LoadEventsAsync("task_01", default)).Count);
    }

    [Theory]
    [InlineData("DELETE FROM manifests;")]
    [InlineData("DELETE FROM render_page_checkpoints;")]
    [InlineData("UPDATE manifests SET state='render_committed';")]
    public async Task DamagedJournalFailsClosedWithoutRecreatingMissingHalf(string damage)
    {
        using var directory = new TestDirectory();
        var options = new DesktopDataOptions(directory.Path);
        await using var store = new SqliteDesktopStore(options);
        await Start(store);
        var expected = Checkpoint();
        await store.PrepareRenderCommitAsync(expected, default);
        await Sql(options, damage);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.ReadRenderCommitAsync(expected, default));
        await Assert.ThrowsAsync<InvalidDataException>(() => store.PrepareRenderCommitAsync(expected, default));
        await Assert.ThrowsAsync<InvalidDataException>(() => store.CompleteRenderCommitAsync(expected, default));
    }

    [Fact]
    public async Task VariantsAndNewAttemptsRetainHistoryWithoutDoubleCountingRegeneration()
    {
        using var directory = new TestDirectory();
        var options = new DesktopDataOptions(directory.Path);
        var baseline = Checkpoint();
        var pages = new[]
        {
            baseline with { Page = baseline.Page with { OutputVariantId = "color_a" } },
            baseline with { Page = baseline.Page with { OutputVariantId = "color_b", PlanDigest = "sha256:" + new string('c',64) },
                ManifestId = "render:" + new string('c',64), ManifestRelativePath = "outputs/second/manifest.json" },
            baseline with { Page = baseline.Page with { OutputVariantId = "color_a", Attempt = 2, PlanDigest = "sha256:" + new string('d',64) },
                ManifestId = "render:" + new string('d',64), ManifestRelativePath = "outputs/regenerated/manifest.json" }
        };
        await using (var store = new SqliteDesktopStore(options))
        {
            await Start(store);
            foreach (var page in pages)
            {
                await store.PrepareRenderCommitAsync(page, default);
                await store.CompleteRenderCommitAsync(page, default);
            }
        }
        await using var reopened = new SqliteDesktopStore(options);
        await reopened.InitializeAsync();
        foreach (var page in pages)
        {
            await reopened.CompleteRenderCommitAsync(page, default);
            Assert.Equal(OutputCommitState.Committed, await reopened.ReadRenderCommitAsync(page, default));
        }
        Assert.Equal(3, (await reopened.LoadAsync("task_01", default)).Count);
        Assert.Equal(2, (await new TaskCoordinator(reopened).LoadAsync("task_01")).OutputCount);
        await Assert.ThrowsAsync<InvalidDataException>(() => reopened.SaveAsync(baseline.Page with { Stage = "other" }, default));
    }

    [Fact]
    public async Task V14CommitMigratesWithoutChangingOriginalRowsManifestsOrOutputCount()
    {
        using var directory = new TestDirectory();
        var options = new DesktopDataOptions(directory.Path);
        var checkpoint = Checkpoint();
        await using (var old = new SqliteDesktopStore(options))
        {
            await Start(old);
            await old.PrepareRenderCommitAsync(checkpoint, default);
            await old.CompleteRenderCommitAsync(checkpoint, default);
        }
        // Construct the actual pre-v15 shape in an isolated fixture database.
        await Sql(options, """
            INSERT INTO checkpoints(task_id,style_id,page_id,stage,attempt,plan_digest,bridge_execution_id,output_commit_state,updated_at)
            SELECT task_id,style_id,page_id,stage,attempt,plan_digest,bridge_execution_id,output_commit_state,updated_at FROM render_page_checkpoints;
            DROP TABLE render_output_publication_files; DROP TABLE render_output_publications; DROP TABLE render_output_stages; DROP TABLE render_output_registrations;
            DROP TABLE input_output_selections; DROP TABLE render_page_repairs; DROP TABLE render_batch_attempt_origins; DROP TABLE render_batch_journals; DROP TABLE render_page_checkpoints;
            DROP TABLE template_rule_references; DROP TABLE template_rule_commands; DROP TABLE template_rule_workspaces;
            DROP TABLE template_instance_production_facts; DROP TABLE template_instance_manual_selections;
            ALTER TABLE batch_template_snapshots DROP COLUMN context_version;
            ALTER TABLE batch_template_snapshots DROP COLUMN business_rule_digest;
            DELETE FROM schema_migrations WHERE version>=15;
            """);
        await using var upgraded = new SqliteDesktopStore(options);
        await upgraded.InitializeAsync();
        await upgraded.InitializeAsync();
        Assert.Equal(OutputCommitState.Committed, await upgraded.ReadRenderCommitAsync(checkpoint, default));
        await upgraded.CompleteRenderCommitAsync(checkpoint, default);
        var restored = Assert.Single(await upgraded.LoadAsync("task_01", default));
        Assert.Null(restored.OutputVariantId);
        Assert.Equal(checkpoint.Page.PlanDigest, restored.PlanDigest);
        Assert.Equal(checkpoint.Page.UpdatedAt, restored.UpdatedAt);
        Assert.Equal(1, (await new TaskCoordinator(upgraded).LoadAsync("task_01")).OutputCount);
        Assert.Single(await upgraded.LoadEventsAsync("task_01", default), item => item.Type == TaskEventType.PageCommitted);
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = options.DatabasePath, Pooling = false }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT plan_digest FROM checkpoints;";
        Assert.Equal(checkpoint.Page.PlanDigest, await command.ExecuteScalarAsync());
        Assert.DoesNotContain("output_variant_id", System.Text.Json.JsonSerializer.Serialize(checkpoint.Page,
            Desktop.Contracts.Bridge.BridgeProtocol.JsonOptions), StringComparison.Ordinal);
    }

    [Fact]
    public async Task GenericCheckpointWritesCannotBypassOrOverwriteRenderJournal()
    {
        using var directory = new TestDirectory();
        await using var store = new SqliteDesktopStore(new(directory.Path));
        await Start(store);
        var expected = Checkpoint();
        await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveAsync(expected.Page, default));
        await store.PrepareRenderCommitAsync(expected, default);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveAsync(expected.Page with { Stage = "other" }, default));
        Assert.Equal(OutputCommitState.Verified, await store.ReadRenderCommitAsync(expected, default));
    }

    internal static async Task Start(SqliteDesktopStore store)
    {
        await store.InitializeAsync();
        var coordinator = new TaskCoordinator(store);
        var sequence = 0;
        foreach (var type in new[] { TaskEventType.TaskCreated, TaskEventType.ReadyToRender, TaskEventType.RenderingStarted })
            await coordinator.RecordAsync(TaskEvent.Create("task_01", ++sequence, type));
    }

    private static RenderPageCommitCheckpoint Checkpoint() => new(
        new("task_01", "style_01", "page_01", "render_output", 1, "sha256:" + new string('a', 64),
            "execution_01", OutputCommitState.Verified, DateTimeOffset.UtcNow),
        "render:" + new string('a', 64), "outputs/artifact/manifest.json", 100, new string('b', 64));

    private static async Task Sql(DesktopDataOptions options, string sql)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = options.DatabasePath, Pooling = false }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }
}
