using Microsoft.Data.Sqlite;

namespace Desktop.Infrastructure.Persistence;

public sealed partial class SqliteDesktopStore
{
    private static readonly Migration[] Migrations =
    [
        new(1, "desktop-v1-schema-001", """
            CREATE TABLE IF NOT EXISTS app_instance (
                instance_id TEXT PRIMARY KEY,
                started_at TEXT NOT NULL,
                exited_at TEXT NULL,
                safe_exit INTEGER NOT NULL CHECK(safe_exit IN (0, 1))
            );

            CREATE TABLE IF NOT EXISTS tasks (
                task_id TEXT PRIMARY KEY,
                status TEXT NOT NULL,
                version INTEGER NOT NULL,
                output_count INTEGER NOT NULL,
                issue_count INTEGER NOT NULL,
                updated_at TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS task_events (
                event_id TEXT PRIMARY KEY,
                task_id TEXT NOT NULL,
                sequence_no INTEGER NOT NULL,
                event_type TEXT NOT NULL,
                occurred_at TEXT NOT NULL,
                page_id TEXT NULL,
                payload_digest TEXT NOT NULL,
                UNIQUE(task_id, sequence_no),
                FOREIGN KEY(task_id) REFERENCES tasks(task_id) DEFERRABLE INITIALLY DEFERRED
            );

            CREATE TABLE IF NOT EXISTS checkpoints (
                task_id TEXT NOT NULL,
                style_id TEXT NOT NULL,
                page_id TEXT NOT NULL,
                stage TEXT NOT NULL,
                attempt INTEGER NOT NULL,
                plan_digest TEXT NOT NULL,
                bridge_execution_id TEXT NULL,
                output_commit_state TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                PRIMARY KEY(task_id, style_id, page_id),
                FOREIGN KEY(task_id) REFERENCES tasks(task_id)
            );

            CREATE TABLE IF NOT EXISTS manifests (
                manifest_id TEXT PRIMARY KEY,
                relative_path TEXT NOT NULL,
                length INTEGER NOT NULL,
                sha256 TEXT NOT NULL,
                state TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );
            """),
        new(2, "desktop-v2-template-schema-001", """
            CREATE TABLE IF NOT EXISTS template_groups (
                group_id TEXT PRIMARY KEY,
                name TEXT NOT NULL,
                current_revision_id TEXT NULL,
                status TEXT NOT NULL,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS template_revisions (
                revision_id TEXT PRIMARY KEY,
                group_id TEXT NOT NULL,
                revision_no INTEGER NOT NULL CHECK(revision_no > 0),
                state TEXT NOT NULL,
                manifest_ref TEXT NULL,
                rule_digest TEXT NULL,
                created_at TEXT NOT NULL,
                UNIQUE(group_id, revision_no),
                FOREIGN KEY(group_id) REFERENCES template_groups(group_id)
            );

            CREATE TABLE IF NOT EXISTS template_pages (
                revision_id TEXT NOT NULL,
                page_id TEXT NOT NULL,
                order_index INTEGER NOT NULL CHECK(order_index >= 0),
                name TEXT NOT NULL,
                content_type TEXT NOT NULL,
                generation_mode TEXT NOT NULL,
                variant_capacity INTEGER NULL CHECK(variant_capacity IS NULL OR variant_capacity > 0),
                enabled INTEGER NOT NULL CHECK(enabled IN (0, 1)),
                template_resource_id TEXT NOT NULL,
                PRIMARY KEY(revision_id, page_id),
                UNIQUE(revision_id, order_index),
                FOREIGN KEY(revision_id) REFERENCES template_revisions(revision_id) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS template_frame_bindings (
                revision_id TEXT NOT NULL,
                frame_id TEXT NOT NULL,
                page_id TEXT NOT NULL,
                binding_json TEXT NOT NULL,
                PRIMARY KEY(revision_id, frame_id),
                FOREIGN KEY(revision_id, page_id)
                    REFERENCES template_pages(revision_id, page_id) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS calibration_drafts (
                draft_id TEXT PRIMARY KEY,
                base_revision_id TEXT NOT NULL,
                state TEXT NOT NULL,
                last_event_seq INTEGER NOT NULL CHECK(last_event_seq >= 0),
                FOREIGN KEY(base_revision_id) REFERENCES template_revisions(revision_id)
            );

            CREATE TABLE IF NOT EXISTS calibration_draft_pages (
                draft_id TEXT NOT NULL,
                page_id TEXT NOT NULL,
                PRIMARY KEY(draft_id, page_id),
                FOREIGN KEY(draft_id) REFERENCES calibration_drafts(draft_id) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS template_preview_states (
                revision_id TEXT NOT NULL,
                page_id TEXT NOT NULL,
                status TEXT NOT NULL,
                evidence_ref TEXT NULL,
                evidence_kind TEXT NULL,
                created_at TEXT NOT NULL,
                PRIMARY KEY(revision_id, page_id),
                FOREIGN KEY(revision_id, page_id)
                    REFERENCES template_pages(revision_id, page_id) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS batch_template_snapshots (
                snapshot_id TEXT PRIMARY KEY,
                batch_id TEXT NOT NULL UNIQUE,
                revision_id TEXT NOT NULL,
                pages_json TEXT NOT NULL,
                rules_digest TEXT NOT NULL,
                created_at TEXT NOT NULL,
                FOREIGN KEY(revision_id) REFERENCES template_revisions(revision_id)
            );

            CREATE INDEX IF NOT EXISTS IX_template_groups_current_revision
                ON template_groups(current_revision_id);
            CREATE INDEX IF NOT EXISTS IX_template_revisions_group
                ON template_revisions(group_id, revision_no);
            CREATE INDEX IF NOT EXISTS IX_template_pages_revision_order
                ON template_pages(revision_id, order_index);
            """),
        new(3, "desktop-v3-template-import-schema-001", """
            CREATE TABLE IF NOT EXISTS template_resources (
                resource_id TEXT PRIMARY KEY,
                group_id TEXT NOT NULL,
                draft_id TEXT NOT NULL,
                internal_relative_path TEXT NOT NULL UNIQUE,
                length INTEGER NOT NULL CHECK(length > 0),
                sha256 TEXT NOT NULL,
                structure_signature TEXT NOT NULL,
                inspection_json TEXT NOT NULL,
                created_at TEXT NOT NULL,
                FOREIGN KEY(group_id) REFERENCES template_groups(group_id)
            );

            CREATE TABLE IF NOT EXISTS template_import_commands (
                command_id TEXT PRIMARY KEY,
                resource_id TEXT NOT NULL,
                completed_at TEXT NOT NULL,
                FOREIGN KEY(resource_id) REFERENCES template_resources(resource_id)
            );

            CREATE INDEX IF NOT EXISTS IX_template_resources_sha256
                ON template_resources(sha256);
            CREATE INDEX IF NOT EXISTS IX_template_resources_draft
                ON template_resources(group_id, draft_id);
            """),
        new(4, "desktop-v4-calibration-state-schema-001", """
            CREATE TABLE IF NOT EXISTS calibration_draft_states (
                draft_id TEXT NOT NULL,
                page_id TEXT NOT NULL,
                state_json TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                PRIMARY KEY(draft_id, page_id),
                FOREIGN KEY(draft_id) REFERENCES calibration_drafts(draft_id) ON DELETE CASCADE
            );
            """),
        new(5, "desktop-v5-batch-input-schema-001", """
            CREATE TABLE IF NOT EXISTS input_batches (
                batch_id TEXT PRIMARY KEY,
                scan_status TEXT NOT NULL,
                input_type TEXT NULL,
                input_root TEXT NOT NULL,
                output_root TEXT NOT NULL,
                template_snapshot_id TEXT NOT NULL,
                selection_mode TEXT NOT NULL,
                include_editable_psd INTEGER NOT NULL CHECK(include_editable_psd IN (0, 1)),
                manifest_reference TEXT NULL,
                manifest_digest TEXT NULL,
                block_reason_code TEXT NULL,
                block_reason_category TEXT NULL,
                block_reason_action TEXT NULL,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                FOREIGN KEY(template_snapshot_id) REFERENCES batch_template_snapshots(snapshot_id)
            );

            CREATE TABLE IF NOT EXISTS input_styles (
                style_id TEXT PRIMARY KEY,
                batch_id TEXT NOT NULL,
                default_style_code TEXT NOT NULL,
                source_relative_directory TEXT NOT NULL,
                order_index INTEGER NOT NULL CHECK(order_index >= 0),
                UNIQUE(batch_id, order_index),
                FOREIGN KEY(batch_id) REFERENCES input_batches(batch_id) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS input_assets (
                asset_id TEXT PRIMARY KEY,
                batch_id TEXT NOT NULL,
                style_id TEXT NOT NULL,
                source_relative_path TEXT NOT NULL,
                file_name TEXT NOT NULL,
                length INTEGER NOT NULL CHECK(length > 0),
                sha256 TEXT NOT NULL,
                pixel_width INTEGER NOT NULL CHECK(pixel_width > 0),
                pixel_height INTEGER NOT NULL CHECK(pixel_height > 0),
                image_format TEXT NOT NULL,
                mime_type TEXT NOT NULL,
                readability TEXT NOT NULL,
                UNIQUE(batch_id, source_relative_path),
                FOREIGN KEY(batch_id) REFERENCES input_batches(batch_id) ON DELETE CASCADE,
                FOREIGN KEY(style_id) REFERENCES input_styles(style_id) ON DELETE CASCADE
            );

            CREATE INDEX IF NOT EXISTS IX_input_batches_status_updated
                ON input_batches(scan_status, updated_at);
            CREATE INDEX IF NOT EXISTS IX_input_styles_batch
                ON input_styles(batch_id, order_index);
            CREATE INDEX IF NOT EXISTS IX_input_assets_batch_style
                ON input_assets(batch_id, style_id);
            """),
        new(6, "desktop-v6-ai-analysis-schema-001", """
            CREATE TABLE IF NOT EXISTS ai_analysis_sessions (
                session_id TEXT PRIMARY KEY,
                batch_id TEXT NOT NULL,
                style_id TEXT NOT NULL,
                template_snapshot_id TEXT NOT NULL,
                analysis_key TEXT NOT NULL,
                status TEXT NOT NULL,
                selection_revision INTEGER NOT NULL CHECK(selection_revision >= 0),
                allowed_requirement_ids_json TEXT NOT NULL,
                allowed_asset_ids_json TEXT NOT NULL,
                locked_selections_json TEXT NOT NULL,
                has_verified_result INTEGER NOT NULL CHECK(has_verified_result IN (0, 1)),
                updated_at TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS ai_request_ledger (
                request_id TEXT PRIMARY KEY,
                fingerprint TEXT NOT NULL UNIQUE,
                batch_id TEXT NOT NULL,
                style_id TEXT NOT NULL,
                status TEXT NOT NULL,
                result_reference TEXT NULL,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS ai_cache_references (
                cache_key TEXT PRIMARY KEY,
                result_reference TEXT NOT NULL,
                consent_id TEXT NOT NULL,
                model_config_version TEXT NOT NULL,
                usage_event_id TEXT NOT NULL,
                created_at TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS ai_usage_events (
                usage_event_id TEXT PRIMARY KEY,
                request_id TEXT NOT NULL UNIQUE,
                model_config_version TEXT NOT NULL,
                input_units INTEGER NOT NULL CHECK(input_units >= 0),
                output_units INTEGER NOT NULL CHECK(output_units >= 0),
                cache_hit INTEGER NOT NULL CHECK(cache_hit IN (0, 1)),
                retry_count INTEGER NOT NULL CHECK(retry_count >= 0),
                provider_cost_amount_text TEXT NULL,
                provider_cost_currency TEXT NULL,
                recorded_at TEXT NOT NULL
            );

            CREATE INDEX IF NOT EXISTS IX_ai_request_ledger_fingerprint
                ON ai_request_ledger(fingerprint);
            CREATE INDEX IF NOT EXISTS IX_ai_usage_events_request
                ON ai_usage_events(request_id);
            """)
    ];

    private static async Task ApplyMigrationsAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);

        await using (var bootstrap = connection.CreateCommand())
        {
            bootstrap.Transaction = transaction;
            bootstrap.CommandText = """
                CREATE TABLE IF NOT EXISTS schema_migrations (
                    version INTEGER PRIMARY KEY,
                    applied_at TEXT NOT NULL,
                    checksum TEXT NOT NULL
                );
                """;
            await bootstrap.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        var applied = await ReadAppliedMigrationsAsync(connection, transaction, cancellationToken)
            .ConfigureAwait(false);
        foreach (var entry in applied)
        {
            var known = Migrations.SingleOrDefault(migration => migration.Version == entry.Key)
                ?? throw new InvalidOperationException("DESKTOP_MIGRATION_VERSION_UNSUPPORTED");
            if (!string.Equals(known.Checksum, entry.Value, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("DESKTOP_MIGRATION_CHECKSUM_MISMATCH");
            }
        }

        foreach (var migration in Migrations)
        {
            if (applied.ContainsKey(migration.Version))
            {
                continue;
            }

            if (migration.Version > 1 && !applied.ContainsKey(migration.Version - 1))
            {
                throw new InvalidOperationException("DESKTOP_MIGRATION_SEQUENCE_INVALID");
            }

            await using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = migration.Sql;
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await using var versionCommand = connection.CreateCommand();
            versionCommand.Transaction = transaction;
            versionCommand.CommandText = """
                INSERT INTO schema_migrations(version, applied_at, checksum)
                VALUES($version, $applied_at, $checksum);
                """;
            versionCommand.Parameters.AddWithValue("$version", migration.Version);
            versionCommand.Parameters.AddWithValue("$applied_at", DateTimeOffset.UtcNow.ToString("O"));
            versionCommand.Parameters.AddWithValue("$checksum", migration.Checksum);
            await versionCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            applied.Add(migration.Version, migration.Checksum);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<Dictionary<int, string>> ReadAppliedMigrationsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT version, checksum FROM schema_migrations ORDER BY version;";
        var applied = new Dictionary<int, string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            applied.Add(reader.GetInt32(0), reader.GetString(1));
        }

        return applied;
    }

    private sealed record Migration(int Version, string Checksum, string Sql);
}
