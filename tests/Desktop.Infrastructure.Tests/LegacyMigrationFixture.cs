using System.Reflection;
using Desktop.Infrastructure.Persistence;

namespace Desktop.Infrastructure.Tests;

// Test-only reconstruction of the pre-TPL02 schema. Removing migration rows
// alone leaves v22-v26 tables/columns behind and does not model an old database.
internal static class LegacyMigrationFixture
{
    internal static string RemoveTpl02Schema
    {
        get
        {
            var migrations = (Array)typeof(SqliteDesktopStore).GetField(
                "Migrations", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
            var migration = migrations.Cast<object>().Single(value =>
                (int)value.GetType().GetProperty("Version")!.GetValue(value)! == 15);
            var checkpointSql = (string)migration.GetType().GetProperty("Sql")!.GetValue(migration)!;
            return "ALTER TABLE render_page_checkpoints RENAME TO isolated_v25;" + checkpointSql + """
                INSERT INTO render_page_checkpoints(task_id,style_id,page_id,output_variant_id,stage,attempt,
                    plan_digest,bridge_execution_id,output_commit_state,updated_at)
                SELECT task_id,style_id,page_id,output_variant_id,stage,attempt,
                    plan_digest,bridge_execution_id,output_commit_state,updated_at FROM isolated_v25;
                DROP TABLE isolated_v25;
                DROP TABLE template_instance_manual_selections;
                DROP TABLE template_instance_production_facts;
                DROP TABLE template_instance_projection_members;
                DROP TABLE template_instance_projection_commands;
                DROP TABLE template_instance_projection_snapshots;
                DROP TABLE template_rule_references;
                DROP TABLE template_rule_commands;
                DROP TABLE template_rule_workspaces;
                ALTER TABLE batch_template_snapshots DROP COLUMN context_version;
                ALTER TABLE batch_template_snapshots DROP COLUMN business_rule_digest;
                DELETE FROM schema_migrations WHERE version>21;
                """;
        }
    }
}
