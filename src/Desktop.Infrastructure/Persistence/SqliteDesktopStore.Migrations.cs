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
            """),
        new(7, "desktop-v7-ai-checkpoint-journal-001", """
            CREATE TABLE IF NOT EXISTS ai_analysis_checkpoint_journal (
                checkpoint_id TEXT PRIMARY KEY,
                batch_id TEXT NOT NULL,
                manifest_id TEXT NOT NULL,
                stage_token TEXT NOT NULL,
                manifest_reference TEXT NOT NULL,
                manifest_digest TEXT NOT NULL,
                payload_digest TEXT NOT NULL,
                state TEXT NOT NULL CHECK(state IN ('Pending', 'Committed')),
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS ai_analysis_checkpoint_items (
                checkpoint_id TEXT NOT NULL,
                order_index INTEGER NOT NULL CHECK(order_index >= 0),
                request_id TEXT NOT NULL,
                session_id TEXT NOT NULL,
                usage_event_id TEXT NULL,
                cache_key TEXT NULL,
                expected_request_status TEXT NOT NULL,
                expected_session_status TEXT NOT NULL,
                expected_selection_revision INTEGER NOT NULL CHECK(expected_selection_revision >= 0),
                PRIMARY KEY(checkpoint_id, request_id),
                UNIQUE(checkpoint_id, order_index),
                UNIQUE(checkpoint_id, session_id),
                UNIQUE(checkpoint_id, usage_event_id),
                UNIQUE(checkpoint_id, cache_key),
                CHECK(cache_key IS NULL OR usage_event_id IS NOT NULL),
                FOREIGN KEY(checkpoint_id) REFERENCES ai_analysis_checkpoint_journal(checkpoint_id) ON DELETE CASCADE,
                FOREIGN KEY(request_id) REFERENCES ai_request_ledger(request_id),
                FOREIGN KEY(session_id) REFERENCES ai_analysis_sessions(session_id),
                FOREIGN KEY(usage_event_id) REFERENCES ai_usage_events(usage_event_id),
                FOREIGN KEY(cache_key) REFERENCES ai_cache_references(cache_key)
            );

            CREATE UNIQUE INDEX IF NOT EXISTS IX_ai_checkpoint_pending_batch
                ON ai_analysis_checkpoint_journal(batch_id)
                WHERE state = 'Pending';
            CREATE INDEX IF NOT EXISTS IX_ai_checkpoint_batch_created
                ON ai_analysis_checkpoint_journal(batch_id, created_at DESC);
            """),
        new(8, "desktop-v8-ai-scheduler-state-001", """
            CREATE TABLE IF NOT EXISTS ai_analysis_scheduler_state (
                state_key TEXT PRIMARY KEY,
                pending_items_json TEXT NOT NULL,
                request_count INTEGER NOT NULL CHECK(request_count >= 0),
                input_tokens INTEGER NOT NULL CHECK(input_tokens >= 0),
                output_tokens INTEGER NOT NULL CHECK(output_tokens >= 0),
                estimated_cost_text TEXT NOT NULL,
                warning_reached INTEGER NOT NULL CHECK(warning_reached IN (0, 1)),
                hard_stopped INTEGER NOT NULL CHECK(hard_stopped IN (0, 1)),
                updated_at TEXT NOT NULL
            );
            """),
        new(9, "desktop-v9-ai-checkpoint-scheduler-closure-001", """
            ALTER TABLE ai_analysis_checkpoint_journal
                ADD COLUMN scheduler_state_key TEXT NULL;
            ALTER TABLE ai_analysis_checkpoint_journal
                ADD COLUMN scheduler_state_digest TEXT NULL;
            ALTER TABLE ai_analysis_checkpoint_journal
                ADD COLUMN scheduler_state_json TEXT NULL;
            """),
        new(10, "desktop-v10-ai-scheduler-control-001", """
            ALTER TABLE ai_analysis_scheduler_state
                ADD COLUMN control_state TEXT NOT NULL DEFAULT 'Running';
            ALTER TABLE ai_analysis_scheduler_state
                ADD COLUMN control_reason TEXT NULL;
            """),
        new(11, "desktop-v11-ai-cache-logical-invalidation-001", """
            ALTER TABLE ai_cache_references
                ADD COLUMN is_reusable INTEGER NOT NULL DEFAULT 1 CHECK(is_reusable IN (0, 1));
            """),
        new(12, "desktop-v12-ai-upload-consent-001", """
            CREATE TABLE IF NOT EXISTS ai_upload_consents (
                scope TEXT PRIMARY KEY,
                consent_id TEXT NOT NULL,
                state TEXT NOT NULL CHECK(state IN ('Granted', 'Revoked')),
                policy_version TEXT NOT NULL,
                changed_at TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS ai_upload_consent_events (
                event_id TEXT PRIMARY KEY,
                scope TEXT NOT NULL,
                consent_id TEXT NOT NULL,
                state TEXT NOT NULL CHECK(state IN ('Granted', 'Revoked')),
                policy_version TEXT NOT NULL,
                occurred_at TEXT NOT NULL,
                FOREIGN KEY(scope) REFERENCES ai_upload_consents(scope)
            );

            CREATE INDEX IF NOT EXISTS IX_ai_upload_consent_events_scope_time
                ON ai_upload_consent_events(scope, occurred_at);
            """),
        new(13, "desktop-v13-template-canvas-page-state-001", """
            ALTER TABLE template_pages ADD COLUMN notes TEXT NOT NULL DEFAULT '';
            ALTER TABLE template_pages ADD COLUMN canvas_x REAL NOT NULL DEFAULT 0;
            ALTER TABLE template_pages ADD COLUMN canvas_y REAL NOT NULL DEFAULT 0;
            """),
        new(14, "desktop-v14-size-table-template-calibration-001", """
            ALTER TABLE template_pages ADD COLUMN size_table_calibration_json TEXT NULL;
            """),
        new(15, "desktop-v15-render-page-variant-attempt-checkpoints-001", """
            CREATE TABLE render_page_checkpoints (
                task_id TEXT NOT NULL,
                style_id TEXT NOT NULL,
                page_id TEXT NOT NULL,
                output_variant_id TEXT NOT NULL DEFAULT '',
                stage TEXT NOT NULL CHECK(stage = 'render_output'),
                attempt INTEGER NOT NULL CHECK(attempt > 0),
                plan_digest TEXT NOT NULL UNIQUE,
                bridge_execution_id TEXT NOT NULL,
                output_commit_state TEXT NOT NULL CHECK(output_commit_state IN ('Verified','Committed')),
                updated_at TEXT NOT NULL,
                PRIMARY KEY(task_id, style_id, page_id, output_variant_id, attempt),
                FOREIGN KEY(task_id) REFERENCES tasks(task_id)
            );
            INSERT INTO render_page_checkpoints(task_id,style_id,page_id,stage,attempt,plan_digest,
                bridge_execution_id,output_commit_state,updated_at)
            SELECT task_id,style_id,page_id,stage,attempt,plan_digest,bridge_execution_id,output_commit_state,updated_at
                FROM checkpoints WHERE stage='render_output';
            """),
        new(16, "desktop-v16-render-batch-intent-journal-001", """
            CREATE TABLE render_batch_journals (
                batch_id TEXT NOT NULL,
                revision INTEGER NOT NULL CHECK(revision > 0),
                payload_json TEXT NOT NULL,
                payload_sha256 TEXT NOT NULL,
                PRIMARY KEY(batch_id, revision)
            );
            """),
        new(17, "desktop-v17-render-page-regeneration-origin-001", """
            CREATE TABLE render_batch_attempt_origins (
                batch_id TEXT NOT NULL,
                page_index INTEGER NOT NULL CHECK(page_index > 0),
                birth_revision INTEGER NOT NULL CHECK(birth_revision > 1),
                event_id TEXT NOT NULL UNIQUE,
                PRIMARY KEY(batch_id, page_index),
                FOREIGN KEY(batch_id, birth_revision) REFERENCES render_batch_journals(batch_id, revision),
                FOREIGN KEY(event_id) REFERENCES task_events(event_id)
            );
            """),
        new(18, "desktop-v18-size-page-repair-request-001", """
            CREATE TABLE render_page_repairs (
                batch_id TEXT NOT NULL,
                request_id TEXT NOT NULL,
                page_index INTEGER NOT NULL CHECK(page_index > 0),
                birth_revision INTEGER NOT NULL CHECK(birth_revision > 1),
                request_digest TEXT NOT NULL,
                PRIMARY KEY(batch_id, request_id),
                UNIQUE(batch_id, page_index),
                FOREIGN KEY(batch_id, page_index) REFERENCES render_batch_attempt_origins(batch_id, page_index),
                FOREIGN KEY(batch_id, birth_revision) REFERENCES render_batch_journals(batch_id, revision)
            );
            """),
        new(19, "desktop-v19-input-output-selection-001", """
            CREATE TABLE input_output_selections (
                batch_id TEXT PRIMARY KEY,
                mode TEXT NOT NULL CHECK(mode IN ('UnifiedDirectory', 'StyleSourceDirectories')),
                unified_root TEXT NULL,
                CHECK((mode = 'UnifiedDirectory' AND unified_root IS NOT NULL AND length(unified_root) > 0)
                    OR (mode = 'StyleSourceDirectories' AND unified_root IS NULL)),
                FOREIGN KEY(batch_id) REFERENCES input_batches(batch_id) ON DELETE CASCADE
            );
            """),
        new(20, "desktop-v20-render-output-registration-001", """
            CREATE TABLE render_output_registrations (
                batch_id TEXT PRIMARY KEY,
                render_revision INTEGER NOT NULL CHECK(render_revision = 1),
                payload_json TEXT NOT NULL,
                payload_sha256 TEXT NOT NULL,
                FOREIGN KEY(batch_id) REFERENCES input_batches(batch_id),
                FOREIGN KEY(batch_id, render_revision) REFERENCES render_batch_journals(batch_id, revision)
            );
            CREATE TABLE render_output_stages (
                batch_id TEXT NOT NULL,
                style_id TEXT NOT NULL,
                payload_json TEXT NOT NULL,
                payload_sha256 TEXT NOT NULL,
                PRIMARY KEY(batch_id, style_id),
                FOREIGN KEY(batch_id) REFERENCES render_output_registrations(batch_id)
            );
            """),
        new(21, "desktop-v21-render-output-publication-001", """
            CREATE TABLE render_output_publications (
                batch_id TEXT NOT NULL,
                style_id TEXT NOT NULL,
                state TEXT NOT NULL CHECK(state IN ('Copying','Verified','Committed')),
                payload_json TEXT NOT NULL,
                payload_sha256 TEXT NOT NULL,
                PRIMARY KEY(batch_id, style_id),
                FOREIGN KEY(batch_id, style_id) REFERENCES render_output_stages(batch_id, style_id)
            );
            CREATE TABLE render_output_publication_files (
                batch_id TEXT NOT NULL,
                style_id TEXT NOT NULL,
                file_name TEXT NOT NULL,
                payload_json TEXT NOT NULL,
                payload_sha256 TEXT NOT NULL,
                PRIMARY KEY(batch_id, style_id, file_name),
                FOREIGN KEY(batch_id, style_id) REFERENCES render_output_publications(batch_id, style_id)
            );
            """),
        new(22, "desktop-v22-template-rule-workspaces-001", """
            CREATE TABLE template_rule_workspaces (
                revision_id TEXT PRIMARY KEY,
                version INTEGER NOT NULL CHECK(version > 0),
                payload_json TEXT NOT NULL,
                payload_digest TEXT NOT NULL,
                business_digest TEXT NOT NULL,
                FOREIGN KEY(revision_id) REFERENCES template_revisions(revision_id) ON DELETE CASCADE
            );
            CREATE TABLE template_rule_commands (
                revision_id TEXT NOT NULL,
                command_id TEXT NOT NULL,
                request_digest TEXT NOT NULL,
                result_version INTEGER NOT NULL,
                applied_json TEXT,
                inverse_json TEXT,
                undo_of TEXT,
                before_legacy INTEGER NOT NULL CHECK(before_legacy IN (0,1)),
                PRIMARY KEY(revision_id, command_id),
                UNIQUE(revision_id, undo_of),
                FOREIGN KEY(revision_id) REFERENCES template_rule_workspaces(revision_id) ON DELETE CASCADE
            );
            CREATE TABLE template_rule_references (
                revision_id TEXT NOT NULL,
                branch_id TEXT NOT NULL,
                recipe_id TEXT NOT NULL,
                page_id TEXT NOT NULL,
                frame_id TEXT NOT NULL,
                PRIMARY KEY(revision_id, branch_id, recipe_id, page_id, frame_id),
                FOREIGN KEY(revision_id) REFERENCES template_rule_workspaces(revision_id) ON DELETE CASCADE
            );
            CREATE INDEX ix_template_rule_target ON template_rule_references(revision_id, page_id, frame_id);
            """),
        new(23, "desktop-v23-template-instance-projection-snapshots-001", """
            CREATE TABLE IF NOT EXISTS template_instance_projection_snapshots (
                batch_id TEXT NOT NULL,
                style_id TEXT NOT NULL,
                schema_version INTEGER NOT NULL CHECK(schema_version = 1),
                snapshot_version INTEGER NOT NULL CHECK(snapshot_version = 1),
                snapshot_id TEXT NOT NULL,
                revision_id TEXT NOT NULL,
                rule_digest TEXT NOT NULL,
                input_facts_digest TEXT NOT NULL,
                projection_digest TEXT NOT NULL,
                payload_json TEXT NOT NULL,
                frozen_at TEXT NOT NULL,
                command_id TEXT NOT NULL,
                command_digest TEXT NOT NULL,
                PRIMARY KEY(batch_id, style_id)
            );
            CREATE TABLE IF NOT EXISTS template_instance_projection_commands (
                batch_id TEXT NOT NULL,
                style_id TEXT NOT NULL,
                command_id TEXT NOT NULL,
                command_digest TEXT NOT NULL,
                result_version INTEGER NOT NULL CHECK(result_version > 0),
                projection_digest TEXT NOT NULL,
                PRIMARY KEY(batch_id, style_id, command_id),
                FOREIGN KEY(batch_id, style_id)
                    REFERENCES template_instance_projection_snapshots(batch_id, style_id) ON DELETE CASCADE
            );
            CREATE TABLE IF NOT EXISTS template_instance_projection_members (
                batch_id TEXT NOT NULL,
                style_id TEXT NOT NULL,
                page_instance_id TEXT NOT NULL,
                source_page_id TEXT NOT NULL,
                recipe_id TEXT NOT NULL,
                repetition_index INTEGER NOT NULL CHECK(repetition_index >= 0),
                stable_order INTEGER NOT NULL CHECK(stable_order >= 0),
                PRIMARY KEY(batch_id, style_id, page_instance_id),
                UNIQUE(batch_id, style_id, source_page_id, recipe_id, repetition_index),
                UNIQUE(batch_id, style_id, stable_order),
                FOREIGN KEY(batch_id, style_id)
                    REFERENCES template_instance_projection_snapshots(batch_id, style_id) ON DELETE CASCADE
            );
            """),
        new(24, "desktop-v24-rule-production-context-001", """
            ALTER TABLE batch_template_snapshots ADD COLUMN context_version TEXT NOT NULL DEFAULT 'batch-template-snapshot/v1';
            ALTER TABLE batch_template_snapshots ADD COLUMN business_rule_digest TEXT NULL;
            CREATE TABLE template_instance_production_facts (
                batch_id TEXT NOT NULL,
                style_id TEXT NOT NULL,
                payload_json TEXT NOT NULL,
                payload_digest TEXT NOT NULL,
                PRIMARY KEY(batch_id, style_id),
                FOREIGN KEY(batch_id) REFERENCES input_batches(batch_id)
            );
            """),
        new(25, "desktop-v25-render-instance-checkpoints-001", """
            ALTER TABLE render_page_checkpoints RENAME TO render_page_checkpoints_v15;
            CREATE TABLE render_page_checkpoints (
                task_id TEXT NOT NULL,
                style_id TEXT NOT NULL,
                page_id TEXT NOT NULL,
                output_variant_id TEXT NOT NULL DEFAULT '',
                page_instance_id TEXT NOT NULL DEFAULT '',
                instance_json TEXT NULL,
                stage TEXT NOT NULL CHECK(stage = 'render_output'),
                attempt INTEGER NOT NULL CHECK(attempt > 0),
                plan_digest TEXT NOT NULL UNIQUE,
                bridge_execution_id TEXT NOT NULL,
                output_commit_state TEXT NOT NULL CHECK(output_commit_state IN ('Verified','Committed')),
                updated_at TEXT NOT NULL,
                CHECK((page_instance_id = '' AND instance_json IS NULL) OR
                    (page_instance_id <> '' AND output_variant_id = '' AND instance_json IS NOT NULL)),
                PRIMARY KEY(task_id, style_id, page_id, output_variant_id, page_instance_id, attempt),
                FOREIGN KEY(task_id) REFERENCES tasks(task_id)
            );
            INSERT INTO render_page_checkpoints(task_id,style_id,page_id,output_variant_id,stage,attempt,
                plan_digest,bridge_execution_id,output_commit_state,updated_at)
            SELECT task_id,style_id,page_id,output_variant_id,stage,attempt,
                plan_digest,bridge_execution_id,output_commit_state,updated_at FROM render_page_checkpoints_v15;
            DROP TABLE render_page_checkpoints_v15;
            """),
        new(26, "desktop-v26-instance-manual-selections-001", """
            CREATE TABLE template_instance_manual_selections (
                batch_id TEXT NOT NULL,
                style_id TEXT NOT NULL,
                revision INTEGER NOT NULL CHECK(revision > 0),
                payload_json TEXT NOT NULL,
                payload_digest TEXT NOT NULL,
                PRIMARY KEY(batch_id, style_id),
                FOREIGN KEY(batch_id) REFERENCES input_batches(batch_id)
            );
            """),
        new(27, "desktop-v27-remove-redundant-request-fingerprint-index-001", """
            DROP INDEX IF EXISTS IX_ai_request_ledger_fingerprint;
            """),
        new(28, "desktop-v28-ai-cached-input-units-001", """
            ALTER TABLE ai_usage_events ADD COLUMN cached_input_units INTEGER NOT NULL DEFAULT 0;
            """),
        new(29, "desktop-v29-template-page-type-recognition-001", """
            ALTER TABLE template_pages ADD COLUMN type_recognition TEXT NOT NULL DEFAULT 'OrdinaryPage';
            UPDATE template_pages
            SET type_recognition = 'SizeTablePage'
            WHERE content_type = 'DynamicSizeTable';
            """),
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

            if (migration.Version == 13)
            {
                await EnsureTemplateCanvasColumnsAsync(
                    connection,
                    transaction,
                    cancellationToken).ConfigureAwait(false);
            }
            else if (migration.Version == 28)
            {
                await EnsureCachedInputUnitsColumnAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await using var command = connection.CreateCommand();
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

    private static async Task EnsureCachedInputUnitsColumnAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "PRAGMA table_info(ai_usage_events);";
        var hasColumn = false;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (string.Equals(reader.GetString(1), "cached_input_units", StringComparison.OrdinalIgnoreCase))
                {
                    hasColumn = true;
                    break;
                }
            }
        }
        if (hasColumn) return;
        await using var alter = connection.CreateCommand();
        alter.Transaction = transaction;
        alter.CommandText = "ALTER TABLE ai_usage_events ADD COLUMN cached_input_units INTEGER NOT NULL DEFAULT 0;";
        await alter.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task EnsureTemplateCanvasColumnsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using (var pragma = connection.CreateCommand())
        {
            pragma.Transaction = transaction;
            pragma.CommandText = "PRAGMA table_info(template_pages);";
            await using var reader = await pragma.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                existing.Add(reader.GetString(1));
            }
        }

        foreach (var definition in new[]
        {
            (Name: "notes", Sql: "ALTER TABLE template_pages ADD COLUMN notes TEXT NOT NULL DEFAULT '';"),
            (Name: "canvas_x", Sql: "ALTER TABLE template_pages ADD COLUMN canvas_x REAL NOT NULL DEFAULT 0;"),
            (Name: "canvas_y", Sql: "ALTER TABLE template_pages ADD COLUMN canvas_y REAL NOT NULL DEFAULT 0;")
        })
        {
            if (existing.Contains(definition.Name))
            {
                continue;
            }

            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = definition.Sql;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            existing.Add(definition.Name);
        }
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
