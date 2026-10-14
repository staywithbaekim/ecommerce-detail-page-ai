using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using Desktop.Application.Templates;
using Desktop.Domain.Templates;
using Microsoft.Data.Sqlite;

namespace Desktop.Infrastructure.Persistence;

public sealed partial class SqliteDesktopStore
{
    public Task SaveDraftRevisionAsync(
        TemplateRevision revision,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(revision);
        if (revision.State == TemplateRevisionState.Committed)
        {
            throw new TemplateRuleViolationException("TPL_REVISION_IMMUTABLE");
        }

        return _writer.ExecuteAsync(
            async (connection, token) =>
            {
                await using var transaction = (SqliteTransaction)await connection
                    .BeginTransactionAsync(token)
                    .ConfigureAwait(false);
                await EnsureGroupExistsAsync(connection, transaction, revision.GroupId, token).ConfigureAwait(false);
                var existingState = await GetRevisionStateAsync(
                    connection,
                    transaction,
                    revision.RevisionId,
                    token).ConfigureAwait(false);
                if (existingState == TemplateRevisionState.Committed)
                {
                    throw new TemplateRuleViolationException("TPL_REVISION_IMMUTABLE");
                }

                await ReplaceRevisionGraphAsync(connection, transaction, revision, token).ConfigureAwait(false);
                await transaction.CommitAsync(token).ConfigureAwait(false);
                return true;
            },
            cancellationToken);
    }

    public async Task<TemplateRevision?> GetRevisionAsync(
        string revisionId,
        CancellationToken cancellationToken)
    {
        TemplateValueRules.Identifier(revisionId, "TPL_REVISION_ID_INVALID");
        await using var connection = await OpenReadConnectionAsync(cancellationToken).ConfigureAwait(false);

        await using var transaction = connection.BeginTransaction(deferred: true);
        return await LoadRevisionAsync(connection, transaction, revisionId, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<TemplateRevision?> LoadRevisionAsync(SqliteConnection connection,
        SqliteTransaction transaction, string revisionId, CancellationToken cancellationToken)
    {
        string groupId;
        int revisionNo;
        TemplateRevisionState state;
        string? manifestReference;
        string? ruleDigest;
        DateTimeOffset createdAt;
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                SELECT group_id, revision_no, state, manifest_ref, rule_digest, created_at
                FROM template_revisions
                WHERE revision_id = $revision_id;
                """;
            command.Parameters.AddWithValue("$revision_id", revisionId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return null;
            }

            groupId = reader.GetString(0);
            revisionNo = reader.GetInt32(1);
            state = Enum.Parse<TemplateRevisionState>(reader.GetString(2), ignoreCase: false);
            manifestReference = reader.IsDBNull(3) ? null : reader.GetString(3);
            ruleDigest = reader.IsDBNull(4) ? null : reader.GetString(4);
            createdAt = ParseTimestamp(reader.GetString(5));
        }

        var pages = await LoadPagesAsync(connection, revisionId, cancellationToken, transaction).ConfigureAwait(false);
        var bindings = await LoadBindingsAsync(connection, revisionId, cancellationToken, transaction).ConfigureAwait(false);
        var workspace = await LoadRuleWorkspaceAsync(connection, transaction, revisionId, cancellationToken).ConfigureAwait(false);
        var revision = TemplateRevision.Restore(
            revisionId,
            groupId,
            revisionNo,
            state,
            manifestReference,
            ruleDigest,
            createdAt,
            pages,
            bindings, workspace);
        if (workspace is not null)
            await VerifyRuleBusinessDigestAsync(connection, transaction, revision, cancellationToken).ConfigureAwait(false);
        return revision;
    }

    public async Task<TemplateRevision?> GetLatestRevisionForGroupAsync(
        string groupId,
        CancellationToken cancellationToken)
    {
        TemplateValueRules.Identifier(groupId, "TPL_GROUP_ID_INVALID");
        string? revisionId;
        await using (var connection = await OpenReadConnectionAsync(cancellationToken).ConfigureAwait(false))
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT revision_id
                FROM template_revisions
                WHERE group_id = $group_id
                ORDER BY revision_no DESC
                LIMIT 1;
                """;
            command.Parameters.AddWithValue("$group_id", groupId);
            revisionId = (string?)await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        }

        return revisionId is null
            ? null
            : await GetRevisionAsync(revisionId, cancellationToken).ConfigureAwait(false);
    }

    public Task CommitRevisionAsync(
        TemplateRevision committedRevision,
        string? expectedCurrentRevisionId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(committedRevision);
        if (committedRevision.State != TemplateRevisionState.Committed)
        {
            throw new TemplateRuleViolationException("TPL_REVISION_NOT_COMMITTED");
        }

        return _writer.ExecuteAsync(
            async (connection, token) =>
            {
                await using var transaction = (SqliteTransaction)await connection
                    .BeginTransactionAsync(token)
                    .ConfigureAwait(false);
                var currentRevisionId = await GetCurrentRevisionIdAsync(
                    connection,
                    transaction,
                    committedRevision.GroupId,
                    token).ConfigureAwait(false);
                var existingState = await GetRevisionStateAsync(
                    connection,
                    transaction,
                    committedRevision.RevisionId,
                    token).ConfigureAwait(false);
                if (string.Equals(currentRevisionId, committedRevision.RevisionId, StringComparison.Ordinal)
                    && existingState == TemplateRevisionState.Committed)
                {
                    await EnsureCommittedRevisionMatchesAsync(
                        connection,
                        transaction,
                        committedRevision,
                        token).ConfigureAwait(false);
                    await transaction.CommitAsync(token).ConfigureAwait(false);
                    return true;
                }

                if (!string.Equals(currentRevisionId, expectedCurrentRevisionId, StringComparison.Ordinal))
                {
                    throw new TemplateRuleViolationException("TPL_REVISION_CONCURRENCY_CONFLICT");
                }

                if (existingState == TemplateRevisionState.Committed)
                {
                    await EnsureCommittedRevisionMatchesAsync(
                        connection,
                        transaction,
                        committedRevision,
                        token).ConfigureAwait(false);
                }
                else
                {
                    await ReplaceRevisionGraphAsync(
                        connection,
                        transaction,
                        committedRevision,
                        token).ConfigureAwait(false);
                }

                await using var pointerCommand = connection.CreateCommand();
                pointerCommand.Transaction = transaction;
                pointerCommand.CommandText = """
                    UPDATE template_groups
                    SET current_revision_id = $revision_id,
                        updated_at = $updated_at
                    WHERE group_id = $group_id;
                    """;
                pointerCommand.Parameters.AddWithValue("$revision_id", committedRevision.RevisionId);
                pointerCommand.Parameters.AddWithValue("$updated_at", DateTimeOffset.UtcNow.ToString("O"));
                pointerCommand.Parameters.AddWithValue("$group_id", committedRevision.GroupId);
                if (await pointerCommand.ExecuteNonQueryAsync(token).ConfigureAwait(false) != 1)
                {
                    throw new TemplateRuleViolationException("TPL_GROUP_NOT_FOUND");
                }

                await transaction.CommitAsync(token).ConfigureAwait(false);
                return true;
            },
            cancellationToken);
    }

    public Task SaveCalibrationDraftAsync(
        CalibrationDraft draft,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(draft);
        return _writer.ExecuteAsync(
            async (connection, token) =>
            {
                await using var transaction = (SqliteTransaction)await connection
                    .BeginTransactionAsync(token)
                    .ConfigureAwait(false);
                await using (var command = connection.CreateCommand())
                {
                    command.Transaction = transaction;
                    command.CommandText = """
                        INSERT INTO calibration_drafts(draft_id, base_revision_id, state, last_event_seq)
                        VALUES($draft_id, $base_revision_id, $state, $last_event_seq)
                        ON CONFLICT(draft_id) DO UPDATE SET
                            state = excluded.state,
                            last_event_seq = excluded.last_event_seq;
                        """;
                    command.Parameters.AddWithValue("$draft_id", draft.DraftId);
                    command.Parameters.AddWithValue("$base_revision_id", draft.BaseRevisionId);
                    command.Parameters.AddWithValue("$state", draft.State.ToString());
                    command.Parameters.AddWithValue("$last_event_seq", draft.LastEventSequence);
                    await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }

                await using (var deleteCommand = connection.CreateCommand())
                {
                    deleteCommand.Transaction = transaction;
                    deleteCommand.CommandText = "DELETE FROM calibration_draft_pages WHERE draft_id = $draft_id;";
                    deleteCommand.Parameters.AddWithValue("$draft_id", draft.DraftId);
                    await deleteCommand.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }

                foreach (var pageId in draft.AffectedPageIds)
                {
                    await using var pageCommand = connection.CreateCommand();
                    pageCommand.Transaction = transaction;
                    pageCommand.CommandText = """
                        INSERT INTO calibration_draft_pages(draft_id, page_id)
                        VALUES($draft_id, $page_id);
                        """;
                    pageCommand.Parameters.AddWithValue("$draft_id", draft.DraftId);
                    pageCommand.Parameters.AddWithValue("$page_id", pageId);
                    await pageCommand.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }

                await transaction.CommitAsync(token).ConfigureAwait(false);
                return true;
            },
            cancellationToken);
    }

    public async Task<CalibrationDraft?> GetCalibrationDraftAsync(
        string draftId,
        CancellationToken cancellationToken)
    {
        TemplateValueRules.Identifier(draftId, "TPL_DRAFT_ID_INVALID");
        await using var connection = await OpenReadConnectionAsync(cancellationToken).ConfigureAwait(false);
        string baseRevisionId;
        TemplateRevisionState state;
        long lastEventSequence;
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT base_revision_id, state, last_event_seq
                FROM calibration_drafts
                WHERE draft_id = $draft_id;
                """;
            command.Parameters.AddWithValue("$draft_id", draftId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return null;
            }

            baseRevisionId = reader.GetString(0);
            state = Enum.Parse<TemplateRevisionState>(reader.GetString(1), ignoreCase: false);
            lastEventSequence = reader.GetInt64(2);
        }

        var affectedPages = new List<string>();
        await using (var pageCommand = connection.CreateCommand())
        {
            pageCommand.CommandText = """
                SELECT page_id
                FROM calibration_draft_pages
                WHERE draft_id = $draft_id
                ORDER BY page_id;
                """;
            pageCommand.Parameters.AddWithValue("$draft_id", draftId);
            await using var reader = await pageCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                affectedPages.Add(reader.GetString(0));
            }
        }

        return CalibrationDraft.Create(
            draftId,
            baseRevisionId,
            affectedPages,
            state,
            lastEventSequence);
    }

    public Task SaveCalibrationStateAsync(
        CalibrationStateSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var json = JsonSerializer.Serialize(snapshot, TemplateJsonOptions);
        return _writer.ExecuteAsync(
            async (connection, token) =>
            {
                await using var command = connection.CreateCommand();
                command.CommandText = """
                    INSERT INTO calibration_draft_states(draft_id, page_id, state_json, updated_at)
                    VALUES($draft_id, $page_id, $state_json, $updated_at)
                    ON CONFLICT(draft_id, page_id) DO UPDATE SET
                        state_json = excluded.state_json,
                        updated_at = excluded.updated_at;
                    """;
                command.Parameters.AddWithValue("$draft_id", snapshot.DraftId);
                command.Parameters.AddWithValue("$page_id", snapshot.PageId);
                command.Parameters.AddWithValue("$state_json", json);
                command.Parameters.AddWithValue("$updated_at", DateTimeOffset.UtcNow.ToString("O"));
                await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                return true;
            },
            cancellationToken);
    }

    public async Task<CalibrationStateSnapshot?> GetCalibrationStateAsync(
        string draftId,
        string pageId,
        CancellationToken cancellationToken)
    {
        TemplateValueRules.Identifier(draftId, "TPL_DRAFT_ID_INVALID");
        TemplateValueRules.Identifier(pageId, "TPL_PAGE_ID_INVALID");
        await using var connection = await OpenReadConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT state_json
            FROM calibration_draft_states
            WHERE draft_id = $draft_id AND page_id = $page_id;
            """;
        command.Parameters.AddWithValue("$draft_id", draftId);
        command.Parameters.AddWithValue("$page_id", pageId);
        var json = (string?)await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return json is null
            ? null
            : JsonSerializer.Deserialize<CalibrationStateSnapshot>(json, TemplateJsonOptions)
                ?? throw new InvalidDataException("TPL_CALIBRATION_STATE_INVALID");
    }

    public Task SavePreviewStateAsync(
        TemplatePreviewState preview,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(preview);
        return _writer.ExecuteAsync(
            async (connection, token) =>
            {
                await using var command = connection.CreateCommand();
                command.CommandText = """
                    INSERT INTO template_preview_states(
                        revision_id, page_id, status, evidence_ref, evidence_kind, created_at)
                    VALUES(
                        $revision_id, $page_id, $status, $evidence_ref, $evidence_kind, $created_at)
                    ON CONFLICT(revision_id, page_id) DO UPDATE SET
                        status = excluded.status,
                        evidence_ref = excluded.evidence_ref,
                        evidence_kind = excluded.evidence_kind,
                        created_at = excluded.created_at;
                    """;
                command.Parameters.AddWithValue("$revision_id", preview.RevisionId);
                command.Parameters.AddWithValue("$page_id", preview.PageId);
                command.Parameters.AddWithValue("$status", preview.Status.ToString());
                command.Parameters.AddWithValue("$evidence_ref", (object?)preview.EvidenceReference ?? DBNull.Value);
                command.Parameters.AddWithValue("$evidence_kind", preview.EvidenceKind is null
                    ? DBNull.Value
                    : preview.EvidenceKind.Value.ToString());
                command.Parameters.AddWithValue("$created_at", preview.CreatedAt.ToString("O"));
                await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                return true;
            },
            cancellationToken);
    }

    public async Task<TemplatePreviewState?> GetPreviewStateAsync(
        string revisionId,
        string pageId,
        CancellationToken cancellationToken)
    {
        TemplateValueRules.Identifier(revisionId, "TPL_REVISION_ID_INVALID");
        TemplateValueRules.Identifier(pageId, "TPL_PAGE_ID_INVALID");
        await using var connection = await OpenReadConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT status, evidence_ref, evidence_kind, created_at
            FROM template_preview_states
            WHERE revision_id = $revision_id AND page_id = $page_id;
            """;
        command.Parameters.AddWithValue("$revision_id", revisionId);
        command.Parameters.AddWithValue("$page_id", pageId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var createdAt = ParseTimestamp(reader.GetString(3));
        if (Enum.Parse<TemplatePreviewStatus>(reader.GetString(0), false)
            == TemplatePreviewStatus.NotPreviewed)
        {
            return TemplatePreviewState.NotPreviewed(revisionId, pageId, createdAt);
        }

        if (reader.IsDBNull(1) || reader.IsDBNull(2))
        {
            throw new InvalidDataException("TPL_PREVIEW_STATE_INVALID");
        }

        return TemplatePreviewState.NotPreviewed(revisionId, pageId, createdAt).RecordSuccess(
            reader.GetString(1),
            Enum.Parse<TemplatePreviewEvidenceKind>(reader.GetString(2), false),
            createdAt);
    }

    private static async Task ReplaceRevisionGraphAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        TemplateRevision revision,
        CancellationToken cancellationToken)
    {
        await using (var deleteBindings = connection.CreateCommand())
        {
            deleteBindings.Transaction = transaction;
            deleteBindings.CommandText = "DELETE FROM template_frame_bindings WHERE revision_id = $revision_id;";
            deleteBindings.Parameters.AddWithValue("$revision_id", revision.RevisionId);
            await deleteBindings.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var deletePages = connection.CreateCommand())
        {
            deletePages.Transaction = transaction;
            deletePages.CommandText = "DELETE FROM template_pages WHERE revision_id = $revision_id;";
            deletePages.Parameters.AddWithValue("$revision_id", revision.RevisionId);
            await deletePages.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var revisionCommand = connection.CreateCommand())
        {
            revisionCommand.Transaction = transaction;
            revisionCommand.CommandText = """
                INSERT INTO template_revisions(
                    revision_id, group_id, revision_no, state, manifest_ref, rule_digest, created_at)
                VALUES(
                    $revision_id, $group_id, $revision_no, $state, $manifest_ref, $rule_digest, $created_at)
                ON CONFLICT(revision_id) DO UPDATE SET
                    state = excluded.state,
                    manifest_ref = excluded.manifest_ref,
                    rule_digest = excluded.rule_digest;
                """;
            AddRevisionParameters(revisionCommand, revision);
            await revisionCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        foreach (var page in revision.Pages)
        {
            await using var pageCommand = connection.CreateCommand();
            pageCommand.Transaction = transaction;
            pageCommand.CommandText = """
                INSERT INTO template_pages(
                    revision_id, page_id, order_index, name, content_type, generation_mode,
                    variant_capacity, enabled, template_resource_id, notes, canvas_x, canvas_y, size_table_calibration_json)
                VALUES(
                    $revision_id, $page_id, $order_index, $name, $content_type, $generation_mode,
                    $variant_capacity, $enabled, $template_resource_id, $notes, $canvas_x, $canvas_y, $size_table_calibration_json);
                """;
            AddPageParameters(pageCommand, page);
            await pageCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        foreach (var binding in revision.FrameBindings)
        {
            await using var bindingCommand = connection.CreateCommand();
            bindingCommand.Transaction = transaction;
            bindingCommand.CommandText = """
                INSERT INTO template_frame_bindings(revision_id, frame_id, page_id, binding_json)
                VALUES($revision_id, $frame_id, $page_id, $binding_json);
                """;
            bindingCommand.Parameters.AddWithValue("$revision_id", revision.RevisionId);
            bindingCommand.Parameters.AddWithValue("$frame_id", binding.FrameId);
            bindingCommand.Parameters.AddWithValue("$page_id", binding.PageId);
            bindingCommand.Parameters.AddWithValue("$binding_json", SerializeBinding(binding));
            await bindingCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await WriteRuleWorkspaceAsync(connection, transaction, revision, cancellationToken).ConfigureAwait(false);
    }

    private static void AddRevisionParameters(SqliteCommand command, TemplateRevision revision)
    {
        command.Parameters.AddWithValue("$revision_id", revision.RevisionId);
        command.Parameters.AddWithValue("$group_id", revision.GroupId);
        command.Parameters.AddWithValue("$revision_no", revision.RevisionNo);
        command.Parameters.AddWithValue("$state", revision.State.ToString());
        command.Parameters.AddWithValue("$manifest_ref", (object?)revision.ManifestRef ?? DBNull.Value);
        command.Parameters.AddWithValue("$rule_digest", (object?)revision.RuleDigest ?? DBNull.Value);
        command.Parameters.AddWithValue("$created_at", revision.CreatedAt.ToString("O"));
    }

    private static void AddPageParameters(SqliteCommand command, TemplatePage page)
    {
        command.Parameters.AddWithValue("$revision_id", page.RevisionId);
        command.Parameters.AddWithValue("$page_id", page.PageId);
        command.Parameters.AddWithValue("$order_index", page.Order);
        command.Parameters.AddWithValue("$name", page.Name);
        command.Parameters.AddWithValue("$content_type", page.ContentType.ToString());
        command.Parameters.AddWithValue("$generation_mode", page.GenerationMode.ToString());
        command.Parameters.AddWithValue("$variant_capacity", (object?)page.VariantCapacity ?? DBNull.Value);
        command.Parameters.AddWithValue("$enabled", page.Enabled ? 1 : 0);
        command.Parameters.AddWithValue("$template_resource_id", page.TemplateResourceId);
        command.Parameters.AddWithValue("$notes", page.Notes);
        command.Parameters.AddWithValue("$canvas_x", page.CanvasX);
        command.Parameters.AddWithValue("$canvas_y", page.CanvasY);
        command.Parameters.AddWithValue("$size_table_calibration_json", page.SizeTableCalibration is null
            ? DBNull.Value : JsonSerializer.Serialize(page.SizeTableCalibration));
    }

    private static async Task EnsureCommittedRevisionMatchesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        TemplateRevision revision,
        CancellationToken cancellationToken)
    {
        var storedRules = await LoadRuleWorkspaceAsync(connection, transaction, revision.RevisionId, cancellationToken).ConfigureAwait(false);
        if (TemplateRuleCodec.Serialize(storedRules) != TemplateRuleCodec.Serialize(revision.RuleWorkspace))
            throw new TemplateRuleViolationException("TPL_REVISION_IMMUTABLE");
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                SELECT group_id, revision_no, manifest_ref, rule_digest
                FROM template_revisions
                WHERE revision_id = $revision_id;
                """;
            command.Parameters.AddWithValue("$revision_id", revision.RevisionId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
                || !string.Equals(reader.GetString(0), revision.GroupId, StringComparison.Ordinal)
                || reader.GetInt32(1) != revision.RevisionNo
                || !string.Equals(reader.GetString(2), revision.ManifestRef, StringComparison.Ordinal)
                || !string.Equals(reader.GetString(3), revision.RuleDigest, StringComparison.Ordinal))
            {
                throw new TemplateRuleViolationException("TPL_REVISION_IMMUTABLE");
            }
        }

        var expectedPages = revision.Pages.OrderBy(page => page.Order).ToArray();
        var pageIndex = 0;
        await using (var pageCommand = connection.CreateCommand())
        {
            pageCommand.Transaction = transaction;
            pageCommand.CommandText = """
                SELECT page_id, order_index, name, content_type, generation_mode,
                       variant_capacity, enabled, template_resource_id, notes, canvas_x, canvas_y, size_table_calibration_json
                FROM template_pages
                WHERE revision_id = $revision_id
                ORDER BY order_index;
                """;
            pageCommand.Parameters.AddWithValue("$revision_id", revision.RevisionId);
            await using var reader = await pageCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (pageIndex >= expectedPages.Length
                    || !PageMatches(reader, expectedPages[pageIndex]))
                {
                    throw new TemplateRuleViolationException("TPL_REVISION_IMMUTABLE");
                }

                pageIndex++;
            }
        }

        if (pageIndex != expectedPages.Length)
        {
            throw new TemplateRuleViolationException("TPL_REVISION_IMMUTABLE");
        }

        var expectedBindings = revision.FrameBindings.ToDictionary(
            binding => binding.FrameId,
            SerializeBinding,
            StringComparer.Ordinal);
        var bindingCount = 0;
        await using (var bindingCommand = connection.CreateCommand())
        {
            bindingCommand.Transaction = transaction;
            bindingCommand.CommandText = """
                SELECT frame_id, binding_json
                FROM template_frame_bindings
                WHERE revision_id = $revision_id;
                """;
            bindingCommand.Parameters.AddWithValue("$revision_id", revision.RevisionId);
            await using var reader = await bindingCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (!expectedBindings.TryGetValue(reader.GetString(0), out var expectedJson)
                    || !string.Equals(reader.GetString(1), expectedJson, StringComparison.Ordinal))
                {
                    throw new TemplateRuleViolationException("TPL_REVISION_IMMUTABLE");
                }

                bindingCount++;
            }
        }

        if (bindingCount != expectedBindings.Count)
        {
            throw new TemplateRuleViolationException("TPL_REVISION_IMMUTABLE");
        }
    }

    private static bool PageMatches(SqliteDataReader reader, TemplatePage page) =>
        string.Equals(reader.GetString(0), page.PageId, StringComparison.Ordinal)
        && reader.GetInt32(1) == page.Order
        && string.Equals(reader.GetString(2), page.Name, StringComparison.Ordinal)
        && string.Equals(reader.GetString(3), page.ContentType.ToString(), StringComparison.Ordinal)
        && string.Equals(reader.GetString(4), page.GenerationMode.ToString(), StringComparison.Ordinal)
        && (reader.IsDBNull(5) ? null : reader.GetInt32(5)) == page.VariantCapacity
        && reader.GetBoolean(6) == page.Enabled
        && string.Equals(reader.GetString(7), page.TemplateResourceId, StringComparison.Ordinal)
        && string.Equals(reader.GetString(8), page.Notes, StringComparison.Ordinal)
        && Math.Abs(reader.GetDouble(9) - page.CanvasX) < 0.0001
        && Math.Abs(reader.GetDouble(10) - page.CanvasY) < 0.0001
        && string.Equals(reader.IsDBNull(11) ? null : reader.GetString(11), page.SizeTableCalibration is null
            ? null : JsonSerializer.Serialize(page.SizeTableCalibration), StringComparison.Ordinal);

    private static async Task<ImmutableArray<TemplatePage>> LoadPagesAsync(
        SqliteConnection connection,
        string revisionId,
        CancellationToken cancellationToken, SqliteTransaction? transaction = null)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT page_id, order_index, name, content_type, generation_mode,
                   variant_capacity, enabled, template_resource_id, notes, canvas_x, canvas_y, size_table_calibration_json
            FROM template_pages
            WHERE revision_id = $revision_id
            ORDER BY order_index;
            """;
        command.Parameters.AddWithValue("$revision_id", revisionId);
        var pages = ImmutableArray.CreateBuilder<TemplatePage>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            pages.Add(TemplatePage.Create(
                reader.GetString(0),
                revisionId,
                reader.GetInt32(1),
                reader.GetString(2),
                Enum.Parse<TemplatePageContentType>(reader.GetString(3), false),
                Enum.Parse<TemplateGenerationMode>(reader.GetString(4), false),
                reader.IsDBNull(5) ? null : reader.GetInt32(5),
                reader.GetBoolean(6),
                reader.GetString(7),
                reader.GetString(8),
                reader.GetDouble(9),
                reader.GetDouble(10),
                reader.IsDBNull(11) ? null : JsonSerializer.Deserialize<SizeTableTemplateCalibration>(reader.GetString(11))
                    ?? throw new InvalidDataException("SIZE_TABLE_CALIBRATION_INVALID")));
        }

        return pages.ToImmutable();
    }

    private static async Task<ImmutableArray<FrameBinding>> LoadBindingsAsync(
        SqliteConnection connection,
        string revisionId,
        CancellationToken cancellationToken, SqliteTransaction? transaction = null)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT binding_json
            FROM template_frame_bindings
            WHERE revision_id = $revision_id
            ORDER BY frame_id;
            """;
        command.Parameters.AddWithValue("$revision_id", revisionId);
        var bindings = ImmutableArray.CreateBuilder<FrameBinding>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            bindings.Add(DeserializeBinding(reader.GetString(0)));
        }

        return bindings.ToImmutable();
    }

    private static string SerializeBinding(FrameBinding binding)
    {
        var document = new FrameBindingDocument(
            binding.FrameId,
            binding.PageId,
            binding.LayerPath.Segments.ToArray(),
            new ExpectedLayerDocument(
                binding.ExpectedLayer.LayerId,
                binding.ExpectedLayer.NameBefore,
                binding.ExpectedLayer.Visible,
                binding.ExpectedLayer.Clipping,
                binding.ExpectedLayer.UserMask,
                binding.ExpectedLayer.VectorMask,
                binding.ExpectedLayer.Opacity,
                binding.ExpectedLayer.BlendMode,
                binding.ExpectedLayer.StructureSignature.Value)
            { Kind = binding.ExpectedLayer.Kind == TemplateLayerKind.SmartObject ? null : binding.ExpectedLayer.Kind },
            binding.SupersededLayers.Select(layer => new SupersededLayerDocument(
                layer.LayerPath.Segments.ToArray(),
                layer.ExpectedClipping,
                layer.ExpectedLayerId)).ToArray(),
            new SelectionRequirementDocument(
                binding.SelectionRequirement.Description,
                binding.SelectionRequirement.Criteria
                    .Select(item => new SelectionCriterionDocument(item.Key, item.Value)).ToArray()),
            new LayoutIntentDocument(
                binding.LayoutIntent.Intent,
                binding.LayoutIntent.Scale,
                binding.LayoutIntent.OffsetX,
                binding.LayoutIntent.OffsetY,
                binding.LayoutIntent.SafeBounds.Left,
                binding.LayoutIntent.SafeBounds.Top,
                binding.LayoutIntent.SafeBounds.Right,
                binding.LayoutIntent.SafeBounds.Bottom));
        return JsonSerializer.Serialize(document, TemplateJsonOptions);
    }

    private static FrameBinding DeserializeBinding(string json)
    {
        var document = JsonSerializer.Deserialize<FrameBindingDocument>(json, TemplateJsonOptions)
            ?? throw new InvalidDataException("TPL_FRAME_BINDING_INVALID");
        return FrameBinding.Create(
            document.FrameId,
            document.PageId,
            LayerPath.Create(document.LayerPath),
            ExpectedLayerEvidence.Create(
                document.ExpectedLayer.LayerId,
                document.ExpectedLayer.NameBefore,
                document.ExpectedLayer.Visible,
                document.ExpectedLayer.Clipping,
                document.ExpectedLayer.UserMask,
                document.ExpectedLayer.VectorMask,
                document.ExpectedLayer.Opacity,
                document.ExpectedLayer.BlendMode,
                LayerStructureSignature.Create(document.ExpectedLayer.StructureSignature),
                document.ExpectedLayer.Kind ?? TemplateLayerKind.SmartObject),
            document.SupersededLayers.Select(layer => SupersededLayerEvidence.Create(
                LayerPath.Create(layer.LayerPath),
                layer.ExpectedClipping,
                layer.ExpectedLayerId)),
            SelectionRequirement.Create(
                document.SelectionRequirement.Description,
                document.SelectionRequirement.Criteria.Select(item => SelectionCriterion.Create(item.Key, item.Value))),
            LayoutIntent.Create(
                document.LayoutIntent.Intent,
                document.LayoutIntent.Scale,
                document.LayoutIntent.OffsetX,
                document.LayoutIntent.OffsetY,
                TemplateRect.Create(
                    document.LayoutIntent.Left,
                    document.LayoutIntent.Top,
                    document.LayoutIntent.Right,
                    document.LayoutIntent.Bottom)));
    }


}
