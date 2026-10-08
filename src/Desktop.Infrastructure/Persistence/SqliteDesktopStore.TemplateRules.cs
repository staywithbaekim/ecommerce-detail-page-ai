using Desktop.Application.Templates;
using Desktop.Domain.Templates;
using Microsoft.Data.Sqlite;

namespace Desktop.Infrastructure.Persistence;

public sealed partial class SqliteDesktopStore : ITemplateRuleCommandStore
{
    public async Task<IReadOnlyList<string>> GetUndoableRuleCommandsAsync(string revisionId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenReadConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT original.command_id FROM template_rule_commands original
            WHERE original.revision_id=$revision AND original.undo_of IS NULL
              AND original.applied_json IS NOT NULL AND original.inverse_json IS NOT NULL
              AND NOT EXISTS (SELECT 1 FROM template_rule_commands undo
                WHERE undo.revision_id=original.revision_id AND undo.undo_of=original.command_id)
            ORDER BY original.result_version DESC LIMIT 100;
            """;
        command.Parameters.AddWithValue("$revision", revisionId);
        var result = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) result.Add(reader.GetString(0));
        return result;
    }
    public async Task<IReadOnlyList<TemplateRuleReference>> GetRuleReferencesAsync(string revisionId, string pageId,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenReadConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT branch_id,recipe_id,page_id,frame_id FROM template_rule_references
            WHERE revision_id=$revision AND page_id=$page ORDER BY branch_id,recipe_id,frame_id;
            """;
        command.Parameters.AddWithValue("$revision", revisionId);
        command.Parameters.AddWithValue("$page", pageId);
        var result = new List<TemplateRuleReference>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            result.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3) is "" ? null : reader.GetString(3)));
        return result;
    }

    public async Task<TemplateRuleCommandReceipt?> GetRuleCommandAsync(string revisionId, string commandId,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenReadConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await LoadRuleCommandAsync(connection, null, revisionId, commandId, cancellationToken).ConfigureAwait(false);
    }

    public Task SaveRuleCommandAsync(TemplateRevision revision, TemplateRuleCommandReceipt receipt,
        string? undoOf, CancellationToken cancellationToken) => _writer.ExecuteAsync(async (connection, token) =>
    {
        if (revision.RuleWorkspace is null || revision.State != TemplateRevisionState.Draft)
            throw new TemplateRuleViolationException("TPL_REVISION_IMMUTABLE");
        TemplateValueRules.Identifier(receipt.CommandId, "TPL_RULE_COMMAND_INVALID");
        TemplateValueRules.Digest(receipt.RequestDigest, "TPL_RULE_COMMAND_INVALID");
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(token).ConfigureAwait(false);
        var previous = await LoadRuleCommandAsync(connection, transaction, revision.RevisionId, receipt.CommandId, token).ConfigureAwait(false);
        if (previous is not null)
        {
            if (previous.RequestDigest != receipt.RequestDigest)
                throw new TemplateRuleViolationException("TPL_RULE_COMMAND_REUSED");
            return true;
        }
        if (await GetRevisionStateAsync(connection, transaction, revision.RevisionId, token).ConfigureAwait(false)
            != TemplateRevisionState.Draft)
            throw new TemplateRuleViolationException("TPL_REVISION_IMMUTABLE");
        if (receipt.ResultVersion != checked(revision.RuleWorkspace.Version + 1))
            throw new TemplateRuleViolationException("TPL_RULE_VERSION_CONFLICT");
        if (undoOf is not null)
        {
            var original = await LoadRuleCommandAsync(connection, transaction, revision.RevisionId, undoOf, token).ConfigureAwait(false);
            if (original is null || original.IsUndone || original.Inverse is null)
                throw new TemplateRuleViolationException("TPL_RULE_UNDO_UNAVAILABLE");
        }
        await ReplaceRevisionGraphAsync(connection, transaction, revision, token).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO template_rule_commands(revision_id,command_id,request_digest,result_version,applied_json,inverse_json,undo_of,before_legacy)
            VALUES($revision,$command,$digest,$version,$applied,$inverse,$undo,$legacy);
            UPDATE template_rule_commands SET applied_json=NULL,inverse_json=NULL
            WHERE revision_id=$revision AND result_version < $version - 100;
            """;
        command.Parameters.AddWithValue("$revision", revision.RevisionId);
        command.Parameters.AddWithValue("$command", receipt.CommandId);
        command.Parameters.AddWithValue("$digest", receipt.RequestDigest);
        command.Parameters.AddWithValue("$version", receipt.ResultVersion);
        command.Parameters.AddWithValue("$applied", receipt.Applied is null ? DBNull.Value : TemplateRuleCodec.Serialize(receipt.Applied));
        command.Parameters.AddWithValue("$inverse", receipt.Inverse is null ? DBNull.Value : TemplateRuleCodec.Serialize(receipt.Inverse));
        command.Parameters.AddWithValue("$undo", (object?)undoOf ?? DBNull.Value);
        command.Parameters.AddWithValue("$legacy", receipt.BeforeRulesWereLegacy ? 1 : 0);
        await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        await transaction.CommitAsync(token).ConfigureAwait(false);
        return true;
    }, cancellationToken);

    private static async Task<TemplateRuleCommandReceipt?> LoadRuleCommandAsync(SqliteConnection connection,
        SqliteTransaction? transaction, string revisionId, string commandId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT request_digest,result_version,applied_json,inverse_json,
                   EXISTS(SELECT 1 FROM template_rule_commands u WHERE u.revision_id=c.revision_id AND u.undo_of=c.command_id),before_legacy
            FROM template_rule_commands c WHERE revision_id=$revision AND command_id=$command;
            """;
        command.Parameters.AddWithValue("$revision", revisionId);
        command.Parameters.AddWithValue("$command", commandId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) return null;
        return new(commandId, reader.GetString(0), reader.GetInt64(1),
            reader.IsDBNull(2) ? null : TemplateRuleCodec.Deserialize<TemplateRuleEditSet>(reader.GetString(2)),
            reader.IsDBNull(3) ? null : TemplateRuleCodec.Deserialize<TemplateRuleEditSet>(reader.GetString(3)), reader.GetBoolean(4), reader.GetBoolean(5));
    }

    private static async Task<TemplateRuleWorkspace?> LoadRuleWorkspaceAsync(SqliteConnection connection,
        SqliteTransaction? transaction, string revisionId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT version,payload_json,payload_digest FROM template_rule_workspaces WHERE revision_id=$revision;";
        command.Parameters.AddWithValue("$revision", revisionId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) return null;
        var json = reader.GetString(1);
        if (TemplateRuleCodec.Hash(json) != reader.GetString(2))
            throw new TemplateRuleViolationException("TPL_RULE_STORED_DIGEST_INVALID");
        var state = TemplateRuleCodec.Deserialize<TemplateRuleWorkspace>(json);
        TemplateRuleCodec.ValidateWorkspace(state);
        if (state.Version != reader.GetInt64(0) || state.Version <= 0)
            throw new TemplateRuleViolationException("TPL_RULE_STORED_DIGEST_INVALID");
        return state;
    }

    private static async Task VerifyRuleBusinessDigestAsync(SqliteConnection connection, SqliteTransaction transaction,
        TemplateRevision revision, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT business_digest FROM template_rule_workspaces WHERE revision_id=$revision;";
        command.Parameters.AddWithValue("$revision", revision.RevisionId);
        var digest = TemplateRuleCodec.BusinessDigest(revision);
        if ((string?)await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) != digest
            || revision.State == TemplateRevisionState.Committed && revision.RuleDigest != digest)
            throw new TemplateRuleViolationException("TPL_RULE_STORED_DIGEST_INVALID");
    }

    private static async Task WriteRuleWorkspaceAsync(SqliteConnection connection, SqliteTransaction transaction,
        TemplateRevision revision, CancellationToken cancellationToken)
    {
        var previous = await LoadRuleWorkspaceAsync(connection, transaction, revision.RevisionId, cancellationToken).ConfigureAwait(false);
        if (revision.RuleWorkspace is not { } incoming)
        {
            if (previous is not null) throw new TemplateRuleViolationException("TPL_RULE_VERSION_CONFLICT");
            return;
        }
        if (incoming.Version != (previous?.Version ?? 0))
            throw new TemplateRuleViolationException("TPL_RULE_VERSION_CONFLICT");
        TemplateRuleCodec.ValidateWorkspace(incoming);
        if (previous is not null && previous.Aliases.Any(alias => !incoming.Aliases.Contains(alias)))
            throw new TemplateRuleViolationException("TPL_RULE_ALIAS_REASSIGNMENT");
        var next = incoming.Reconcile(revision) with
        {
            Version = revision.State == TemplateRevisionState.Committed ? incoming.Version : checked(incoming.Version + 1)
        };
        TemplateRuleCodec.ValidateWorkspace(next);
        var digest = TemplateRuleCodec.BusinessDigest(revision);
        if (revision.State == TemplateRevisionState.Committed
            && (previous is null || revision.RuleDigest != digest
                || next.Rules is { } rules && !TemplateRuleEvaluator.Validate(rules, revision).IsEmpty))
            throw new TemplateRuleViolationException("TPL_RULE_COMMIT_INVALID");
        var json = TemplateRuleCodec.Serialize(next);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO template_rule_workspaces(revision_id,version,payload_json,payload_digest,business_digest)
            VALUES($revision,$version,$json,$payload,$business)
            ON CONFLICT(revision_id) DO UPDATE SET version=excluded.version,payload_json=excluded.payload_json,
                payload_digest=excluded.payload_digest,business_digest=excluded.business_digest;
            DELETE FROM template_rule_references WHERE revision_id=$revision;
            """;
        command.Parameters.AddWithValue("$revision", revision.RevisionId);
        command.Parameters.AddWithValue("$version", next.Version);
        command.Parameters.AddWithValue("$json", json);
        command.Parameters.AddWithValue("$payload", TemplateRuleCodec.Hash(json));
        command.Parameters.AddWithValue("$business", digest);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        if (next.Rules is null) return;
        foreach (var branch in next.Rules.Branches)
        foreach (var recipe in branch.Recipes)
        foreach (var frame in recipe.FrameBindings.Select(binding => binding.FrameId).Prepend(string.Empty).Distinct(StringComparer.Ordinal))
        {
            await using var reference = connection.CreateCommand();
            reference.Transaction = transaction;
            reference.CommandText = """
                INSERT OR IGNORE INTO template_rule_references(revision_id,branch_id,recipe_id,page_id,frame_id)
                VALUES($revision,$branch,$recipe,$page,$frame);
                """;
            reference.Parameters.AddWithValue("$revision", revision.RevisionId);
            reference.Parameters.AddWithValue("$branch", branch.BranchId);
            reference.Parameters.AddWithValue("$recipe", recipe.RecipeId);
            reference.Parameters.AddWithValue("$page", recipe.PageId);
            reference.Parameters.AddWithValue("$frame", frame);
            await reference.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}
