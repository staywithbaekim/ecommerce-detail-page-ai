using System.Reflection;
using Desktop.Application.Rendering;
using Desktop.Application.Tasks;
using Desktop.Domain.Tasks;
using Desktop.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;

namespace Desktop.Infrastructure.Tests;

public sealed class TemplateInstanceProductionMigrationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task E3RealV23UpgradePreservesLegacyReceiptsAndRollsBackAllAppendedMigrations(bool fail)
    {
        using var directory = new TestDirectory();
        var options = new DesktopDataOptions(directory.Path);
        var checkpoint = new RenderPageCommitCheckpoint(new("task", "style", "page", "render_output", 1,
            "sha256:" + new string('a', 64), "execution", OutputCommitState.Verified, DateTimeOffset.UnixEpoch),
            "render:" + new string('a', 64), "outputs/legacy/manifest.json", 100, new string('b', 64));
        await using (var store = new SqliteDesktopStore(options))
        {
            await store.InitializeAsync(); var tasks = new TaskCoordinator(store);
            await tasks.RecordAsync(TaskEvent.Create("task", 1, TaskEventType.TaskCreated));
            await tasks.RecordAsync(TaskEvent.Create("task", 2, TaskEventType.ReadyToRender));
            await tasks.RecordAsync(TaskEvent.Create("task", 3, TaskEventType.RenderingStarted));
            await store.PrepareRenderCommitAsync(checkpoint, default); await store.CompleteRenderCommitAsync(checkpoint, default);
        }
        // Reconstitute the actual v15/v23 checkpoint table in an isolated test DB, not a user DB.
        var migrations = (Array)typeof(SqliteDesktopStore).GetField("Migrations", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        var migration15 = migrations.Cast<object>().Single(value => (int)value.GetType().GetProperty("Version")!.GetValue(value)! == 15);
        var sql15 = (string)migration15.GetType().GetProperty("Sql")!.GetValue(migration15)!;
        await Sql("ALTER TABLE render_page_checkpoints RENAME TO isolated_v25;" + sql15 + """
            INSERT INTO render_page_checkpoints(task_id,style_id,page_id,output_variant_id,stage,attempt,plan_digest,bridge_execution_id,output_commit_state,updated_at)
            SELECT task_id,style_id,page_id,output_variant_id,stage,attempt,plan_digest,bridge_execution_id,output_commit_state,updated_at FROM isolated_v25;
            DROP TABLE isolated_v25;
            DROP TABLE template_instance_production_facts;
            DROP TABLE template_instance_manual_selections;
            ALTER TABLE batch_template_snapshots DROP COLUMN context_version;
            ALTER TABLE batch_template_snapshots DROP COLUMN business_rule_digest;
            DELETE FROM schema_migrations WHERE version>23;
            """);
        var history = await Scalar("SELECT group_concat(version||checksum||applied_at,'|') FROM (SELECT * FROM schema_migrations ORDER BY version);");
        var receipts = await Scalar("SELECT group_concat(manifest_id||relative_path||sha256||state,'|') FROM manifests;");
        if (fail) await Sql("CREATE TRIGGER e3_fail BEFORE INSERT ON schema_migrations WHEN NEW.version=25 BEGIN SELECT RAISE(ABORT,'E3_UPGRADE_TEST'); END;");
        await using (var upgraded = new SqliteDesktopStore(options))
        {
            if (fail) await Assert.ThrowsAsync<SqliteException>(() => upgraded.InitializeAsync());
            else
            {
                await upgraded.InitializeAsync();
                Assert.Equal(OutputCommitState.Committed, await upgraded.ReadRenderCommitAsync(checkpoint, default));
                Assert.Null(Assert.Single(await upgraded.LoadAsync("task", default)).Instance);
            }
        }
        Assert.Equal(fail ? "23" : "28", await Scalar("SELECT max(version) FROM schema_migrations;"));
        Assert.Equal(history, await Scalar("SELECT group_concat(version||checksum||applied_at,'|') FROM (SELECT * FROM schema_migrations WHERE version<=23 ORDER BY version);"));
        Assert.Equal(receipts, await Scalar("SELECT group_concat(manifest_id||relative_path||sha256||state,'|') FROM manifests;"));
        Assert.Equal(fail ? "0" : "2", await Scalar("SELECT count(*) FROM pragma_table_info('render_page_checkpoints') WHERE name IN ('page_instance_id','instance_json');"));
        if (fail) await Sql("DROP TRIGGER e3_fail;");
        await using var reopened = new SqliteDesktopStore(options); await reopened.InitializeAsync();
        Assert.Equal(OutputCommitState.Committed, await reopened.ReadRenderCommitAsync(checkpoint, default));
        Assert.Equal(1, (await new TaskCoordinator(reopened).LoadAsync("task")).OutputCount);

        async Task<string> Scalar(string sql)
        {
            await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = options.DatabasePath, Pooling = false }.ToString());
            await connection.OpenAsync(); await using var command = connection.CreateCommand(); command.CommandText = sql;
            return Convert.ToString(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture)!;
        }
        async Task Sql(string sql) { _ = await Scalar(sql); }
    }
}
