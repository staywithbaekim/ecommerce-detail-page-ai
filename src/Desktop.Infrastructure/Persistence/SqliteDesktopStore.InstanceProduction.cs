using Desktop.Application.Templates;
using Desktop.Application.Rendering;
using Desktop.Domain.Templates;
using Microsoft.Data.Sqlite;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Desktop.Infrastructure.Persistence;

public sealed partial class SqliteDesktopStore : ITemplateInstanceProductionEvidenceStore
{
    private static readonly JsonSerializerOptions InstanceSelectionJson = new()
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, AllowDuplicateProperties = false,
        RespectRequiredConstructorParameters = true, RespectNullableAnnotations = true
    };

    public async Task<TemplateInstanceManualSelections?> ReadManualSelectionsAsync(string batchId, string styleId, CancellationToken token)
    {
        await using var connection = await OpenReadConnectionAsync(token).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT revision,payload_json,payload_digest FROM template_instance_manual_selections WHERE batch_id=$batch AND style_id=$style;";
        command.Parameters.AddWithValue("$batch", batchId); command.Parameters.AddWithValue("$style", styleId);
        await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        if (!await reader.ReadAsync(token).ConfigureAwait(false)) return null;
        var json = reader.GetString(1);
        if (json.Length > 16 * 1024 * 1024 || TemplateRuleCodec.Hash(json) != reader.GetString(2)) throw SelectionInvalid();
        var value = JsonSerializer.Deserialize<TemplateInstanceManualSelections>(json, InstanceSelectionJson) ?? throw SelectionInvalid();
        ValidateManualSelections(value);
        if (value.Revision != reader.GetInt64(0)) throw SelectionInvalid();
        return value;
    }

    public Task SaveManualSelectionsAsync(string batchId, string styleId, TemplateInstanceManualSelections selections,
        long expectedRevision, CancellationToken token) => _writer.ExecuteAsync(async (connection, cancellation) =>
        {
            ValidateManualSelections(selections);
            if (selections.Revision != checked(expectedRevision + 1)) throw SelectionInvalid();
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellation).ConfigureAwait(false);
            await using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.Parameters.AddWithValue("$batch", batchId); command.Parameters.AddWithValue("$style", styleId);
            command.CommandText = """
                SELECT count(*) FROM input_styles t JOIN input_batches b ON t.batch_id=b.batch_id
                JOIN batch_template_snapshots s ON s.snapshot_id=b.template_snapshot_id AND s.batch_id=b.batch_id
                WHERE t.batch_id=$batch AND t.style_id=$style AND b.scan_status='ScanReady'
                  AND s.context_version='batch-rule-template-context/v1';
                """;
            if ((long)(await command.ExecuteScalarAsync(cancellation).ConfigureAwait(false))! != 1) throw SelectionInvalid();
            command.CommandText = """
                SELECT (SELECT count(*) FROM template_instance_production_facts WHERE batch_id=$batch AND style_id=$style)
                     + (SELECT count(*) FROM template_instance_projection_snapshots WHERE batch_id=$batch AND style_id=$style);
                """;
            if ((long)(await command.ExecuteScalarAsync(cancellation).ConfigureAwait(false))! != 0)
                throw new InvalidOperationException("TPL_INSTANCE_PRODUCTION_FACTS_FROZEN");
            command.CommandText = "SELECT revision FROM template_instance_manual_selections WHERE batch_id=$batch AND style_id=$style;";
            var current = await command.ExecuteScalarAsync(cancellation).ConfigureAwait(false);
            if ((current is long revision ? revision : 0) != expectedRevision)
                throw new InvalidOperationException("TPL_INSTANCE_SELECTION_REVISION_CONFLICT");
            var json = JsonSerializer.Serialize(selections, InstanceSelectionJson);
            command.CommandText = """
                INSERT INTO template_instance_manual_selections(batch_id,style_id,revision,payload_json,payload_digest)
                VALUES($batch,$style,$revision,$json,$digest)
                ON CONFLICT(batch_id,style_id) DO UPDATE SET revision=$revision,payload_json=$json,payload_digest=$digest;
                """;
            command.Parameters.AddWithValue("$revision", selections.Revision); command.Parameters.AddWithValue("$json", json);
            command.Parameters.AddWithValue("$digest", TemplateRuleCodec.Hash(json));
            await command.ExecuteNonQueryAsync(cancellation).ConfigureAwait(false);
            await transaction.CommitAsync(cancellation).ConfigureAwait(false);
            return true;
        }, token);

    private static void ValidateManualSelections(TemplateInstanceManualSelections value)
    {
        if (value.Version != 1 || value.Revision <= 0 || value.Overrides.IsDefaultOrEmpty || value.Overrides.Length > 10000
            || value.Overrides.Any(item => item.SelectionRevision <= 0 || item.SelectionRevision > value.Revision)
            || value.Overrides.Max(item => item.SelectionRevision) != value.Revision
            || value.Overrides.Select(item => (item.SourcePageId, item.InstanceKey, item.FrameId)).Distinct().Count() != value.Overrides.Length)
            throw SelectionInvalid();
        TemplateValueRules.Digest(value.BaseFactsDigest, "TPL_INSTANCE_SELECTION_INVALID");
    }
    private static InvalidDataException SelectionInvalid() => new("TPL_INSTANCE_SELECTION_INVALID");

    private static async Task RequireUnfrozenSelectionChangeAsync(SqliteConnection connection, SqliteTransaction? transaction,
        Desktop.Domain.Ai.AiAnalysisSession session, CancellationToken token)
    {
        await using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = """
            SELECT count(*) FROM ai_analysis_sessions s JOIN template_instance_production_facts f
                ON f.batch_id=s.batch_id AND f.style_id=s.style_id
            WHERE s.session_id=$session AND s.selection_revision<>$revision;
            """;
        command.Parameters.AddWithValue("$session", session.SessionId); command.Parameters.AddWithValue("$revision", session.SelectionRevision);
        if ((long)(await command.ExecuteScalarAsync(token).ConfigureAwait(false))! != 0)
            throw new InvalidOperationException("TPL_INSTANCE_PRODUCTION_FACTS_FROZEN");
    }

    private async Task ValidateInstanceRegistrationAsync(RenderBatchJournal journal, CancellationToken token)
    {
        var batch = await GetAsync(journal.BatchId, token).ConfigureAwait(false);
        if (journal.Version != 4)
        {
            if (batch?.TemplateSnapshot is BatchRuleTemplateContext) throw new InvalidDataException("TPL_RULE_INSTANCE_PIPELINE_REQUIRED");
            return;
        }
        if (batch?.TemplateSnapshot is not BatchRuleTemplateContext context || batch.ManifestDigest is null)
            throw new InvalidDataException("TPL_INSTANCE_PRODUCTION_SCOPE_UNREGISTERED");
        if (journal.InstanceSets?.Count != batch.Styles.Length) throw BatchInvalid();
        var revision = await GetRevisionAsync(context.RevisionId, token).ConfigureAwait(false) ?? throw BatchInvalid();
        var expectedMembers = new HashSet<(string Style, string Instance)>();
        foreach (var style in batch.Styles)
        {
            var evidence = await ReadProductionEvidenceAsync(batch.BatchId, style.StyleId, token).ConfigureAwait(false) ?? throw BatchInvalid();
            var frozen = await ReadAsync(batch.BatchId, style.StyleId, token).ConfigureAwait(false) ?? throw BatchInvalid();
            if (TemplateInstanceProjectionSnapshotCodec.Serialize(frozen) != TemplateInstanceProjectionSnapshotCodec.Serialize(evidence.Snapshot)
                || frozen.SnapshotId != context.SnapshotId || frozen.RevisionId != context.RevisionId
                || frozen.RuleDigest != context.BusinessRuleDigest || evidence.InputManifestDigest != batch.ManifestDigest)
                throw BatchInvalid();
            var inputs = new List<TemplateInstanceRenderBatchInput>();
            foreach (var instance in frozen.Instances)
            {
                expectedMembers.Add((style.StyleId, instance.PageInstanceId));
                var resource = await GetByResourceIdAsync(instance.TemplateResourceId, token).ConfigureAwait(false) ?? throw BatchInvalid();
                var origin = resource.DraftId == revision.RevisionId ? null
                    : await GetRevisionAsync(resource.DraftId, token).ConfigureAwait(false);
                inputs.Add(new(evidence.PlanRequest(instance, revision, resource, origin, batch.IncludeEditablePsd), evidence.Facts()));
            }
            var prepared = new TemplateInstanceRenderBatchPlanner(BatchPlanValidator).Build(new(batch.BatchId, style.StyleId,
                revision.RevisionId, inputs, frozen.Instances.IsEmpty ? [new(frozen, revision, evidence.Facts())] : null));
            if (prepared.Status is TemplateInstanceRenderBatchStatus.Blocked or TemplateInstanceRenderBatchStatus.ProductionValidationRequired)
                throw BatchInvalid();
            if (journal.InstanceSets.SingleOrDefault(set => set.StyleId == style.StyleId) != new RenderBatchInstanceSet(
                style.StyleId, prepared.SchemaVersion, prepared.BatchPlanDigest!, prepared.InputFactsDigest!)) throw BatchInvalid();
            foreach (var member in prepared.Members)
            {
                var page = journal.Pages.SingleOrDefault(page => page.StyleId == style.StyleId && page.Instance?.PageInstanceId == member.PageInstanceId)
                    ?? throw BatchInvalid();
                var plan = prepared.Plans.SingleOrDefault(plan => plan.PageInstanceId == member.PageInstanceId);
                var identity = new RenderInstanceIdentity(1, batch.BatchId, style.StyleId, member.PageInstanceId, member.SourcePageId,
                    frozen.SnapshotId, frozen.RevisionId, frozen.RuleDigest, frozen.InputFactsDigest, frozen.ProjectionDigest);
                var reason = plan is null ? member.Diagnostics[0].Code : "AI_RENDER_PLAN_VALID";
                if (page.Instance != identity || page.Attempt != 1 || page.PlanJson != plan?.RenderPlanJson
                    || page.ReasonCode != reason || page.PreparationReasonCode != reason) throw BatchInvalid();
            }
        }
        if (journal.Pages.Count != expectedMembers.Count
            || journal.Pages.Any(page => !expectedMembers.Contains((page.StyleId, page.Instance!.PageInstanceId)))) throw BatchInvalid();
    }

    public async Task<TemplateInstanceProductionEvidence?> ReadProductionEvidenceAsync(string batchId, string styleId, CancellationToken token)
    {
        await using var connection = await OpenReadConnectionAsync(token).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT payload_json,payload_digest FROM template_instance_production_facts WHERE batch_id=$batch AND style_id=$style;";
        command.Parameters.AddWithValue("$batch", batchId); command.Parameters.AddWithValue("$style", styleId);
        await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        if (!await reader.ReadAsync(token).ConfigureAwait(false)) return null;
        var json = reader.GetString(0);
        if (TemplateRuleCodec.Hash(json) != reader.GetString(1)) throw new InvalidDataException("TPL_INSTANCE_PRODUCTION_EVIDENCE_INVALID");
        var evidence = TemplateInstanceProductionEvidenceCodec.Deserialize(json);
        if (evidence.Snapshot.BatchId != batchId || evidence.Snapshot.StyleId != styleId)
            throw new InvalidDataException("TPL_INSTANCE_PRODUCTION_EVIDENCE_INVALID");
        return evidence;
    }

    public Task FreezeProductionEvidenceAsync(TemplateInstanceProductionEvidence evidence, CancellationToken token) =>
        _writer.ExecuteAsync(async (connection, cancellation) =>
        {
            var json = TemplateInstanceProductionEvidenceCodec.Serialize(evidence);
            var body = evidence.Snapshot;
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellation).ConfigureAwait(false);
            await using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = """
                SELECT count(*) FROM input_batches b
                JOIN batch_template_snapshots s ON s.snapshot_id=b.template_snapshot_id AND s.batch_id=b.batch_id
                JOIN input_styles t ON t.batch_id=b.batch_id
                WHERE b.batch_id=$batch AND t.style_id=$style AND b.scan_status='ScanReady'
                  AND b.manifest_digest=$manifest AND s.snapshot_id=$snapshot AND s.revision_id=$revision
                  AND s.context_version='batch-rule-template-context/v1' AND s.business_rule_digest=$rules;
                """;
            command.Parameters.AddWithValue("$batch", body.BatchId); command.Parameters.AddWithValue("$style", body.StyleId);
            command.Parameters.AddWithValue("$manifest", evidence.InputManifestDigest); command.Parameters.AddWithValue("$snapshot", body.SnapshotId);
            command.Parameters.AddWithValue("$revision", body.RevisionId); command.Parameters.AddWithValue("$rules", body.RuleDigest);
            if ((long)(await command.ExecuteScalarAsync(cancellation).ConfigureAwait(false))! != 1)
                throw new InvalidDataException("TPL_INSTANCE_PRODUCTION_SCOPE_UNREGISTERED");
            command.CommandText = "SELECT payload_json FROM template_instance_production_facts WHERE batch_id=$batch AND style_id=$style;";
            if (await command.ExecuteScalarAsync(cancellation).ConfigureAwait(false) is string previous)
            {
                if (previous != json) throw new InvalidOperationException("TPL_INSTANCE_PRODUCTION_FACTS_FROZEN");
            }
            else
            {
                var source = evidence.SourceVersion ?? throw new InvalidDataException("TPL_INSTANCE_PRODUCTION_SOURCE_VERSION_REQUIRED");
                command.CommandText = "SELECT revision FROM template_instance_manual_selections WHERE batch_id=$batch AND style_id=$style;";
                var manual = await command.ExecuteScalarAsync(cancellation).ConfigureAwait(false);
                if ((manual is long revision ? revision : 0) != source.ManualRevision)
                    throw new InvalidOperationException("TPL_INSTANCE_SELECTION_REVISION_CONFLICT");
                command.CommandText = "SELECT selection_revision FROM ai_analysis_sessions WHERE session_id=$session AND batch_id=$batch AND style_id=$style;";
                command.Parameters.AddWithValue("$session", source.SessionId);
                if (await command.ExecuteScalarAsync(cancellation).ConfigureAwait(false) is not long sessionRevision || sessionRevision != source.SessionRevision)
                    throw new InvalidOperationException("TPL_INSTANCE_SELECTION_REVISION_CONFLICT");
                command.CommandText = "INSERT INTO template_instance_production_facts(batch_id,style_id,payload_json,payload_digest) VALUES($batch,$style,$json,$digest);";
                command.Parameters.AddWithValue("$json", json); command.Parameters.AddWithValue("$digest", TemplateRuleCodec.Hash(json));
                await command.ExecuteNonQueryAsync(cancellation).ConfigureAwait(false);
            }
            await transaction.CommitAsync(cancellation).ConfigureAwait(false);
            return true;
        }, token);
}
