using System.Text.Json;
using Desktop.Application.Ai;
using Desktop.Domain.Ai;
using Desktop.Infrastructure.Ai;
using Microsoft.Data.Sqlite;

namespace Desktop.Infrastructure.Persistence;

public sealed partial class SqliteDesktopStore
{
    private static void ValidateStoredJournal(AiAnalysisCheckpointJournal journal)
    {
        try
        {
            AiAnalysisCheckpointRules.ValidateIdentifier(journal.CheckpointId, "AI_CHECKPOINT_ID_INVALID");
            AiAnalysisCheckpointRules.ValidateIdentifier(journal.BatchId, "AI_BATCH_ID_INVALID");
            AiAnalysisCheckpointRules.ValidateIdentifier(journal.ManifestId, "AI_MANIFEST_ID_INVALID");
            AiAnalysisCheckpointRules.ValidateIdentifier(journal.StageToken, "AI_STAGE_TOKEN_INVALID");
            ValidateOwnedReference(journal.ManifestReference, "AI_MANIFEST_REFERENCE_INVALID");
            AiAnalysisCheckpointRules.ValidateDigest(journal.ManifestDigest, "AI_MANIFEST_DIGEST_INVALID");
            AiAnalysisCheckpointRules.ValidateDigest(journal.PayloadDigest, "AI_PAYLOAD_DIGEST_INVALID");
            if ((journal.SchedulerStateKey is null) != (journal.SchedulerState is null)
                || (journal.SchedulerState is not null && journal.SchedulerStateDigest is null))
            {
                throw new InvalidOperationException("AI_CHECKPOINT_SCHEDULER_CLOSURE_INVALID");
            }

            if (journal.SchedulerStateKey is not null)
            {
                AiAnalysisCheckpointRules.ValidateIdentifier(journal.SchedulerStateKey, "AI_SCHEDULER_STATE_KEY_INVALID");
                AiAnalysisCheckpointRules.ValidateDigest(journal.SchedulerStateDigest!, "AI_SCHEDULER_STATE_DIGEST_INVALID");
                if (!string.Equals(
                        journal.SchedulerStateDigest,
                        AiAnalysisCheckpointRules.ComputeSchedulerStateDigest(journal.SchedulerState!),
                        StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("AI_CHECKPOINT_SCHEDULER_DIGEST_INVALID");
                }
            }
            if (!Enum.IsDefined(journal.State)
                || journal.Items.Count is 0 or > 4096
                || journal.CreatedAt == default
                || journal.CreatedAt.Offset != TimeSpan.Zero
                || journal.UpdatedAt.Offset != TimeSpan.Zero
                || journal.UpdatedAt < journal.CreatedAt)
            {
                throw new InvalidOperationException("AI_CHECKPOINT_JOURNAL_INVALID");
            }

            var requests = new HashSet<string>(StringComparer.Ordinal);
            var sessions = new HashSet<string>(StringComparer.Ordinal);
            var usage = new HashSet<string>(StringComparer.Ordinal);
            var caches = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in journal.Items)
            {
                AiAnalysisCheckpointRules.ValidateIdentifier(item.RequestId, "AI_REQUEST_ID_INVALID");
                AiAnalysisCheckpointRules.ValidateIdentifier(item.SessionId, "AI_SESSION_ID_INVALID");
                if (!Enum.TryParse<AiAnalysisSessionStatus>(item.ExpectedRequestStatus, false, out var expectedRequest)
                    || !Enum.IsDefined(expectedRequest)
                    || expectedRequest != item.ExpectedSessionStatus
                    || item.ExpectedSelectionRevision < 0
                    || !requests.Add(item.RequestId)
                    || !sessions.Add(item.SessionId))
                {
                    throw new InvalidOperationException("AI_CHECKPOINT_JOURNAL_ITEM_INVALID");
                }

                if (item.UsageEventId is not null
                    && (!usage.Add(item.UsageEventId) || string.IsNullOrWhiteSpace(item.UsageEventId)))
                {
                    throw new InvalidOperationException("AI_CHECKPOINT_JOURNAL_ITEM_INVALID");
                }

                if (item.UsageEventId is not null)
                {
                    AiAnalysisCheckpointRules.ValidateIdentifier(item.UsageEventId, "AI_USAGE_ID_INVALID");
                }

                if (item.CacheKey is not null)
                {
                    AiAnalysisCheckpointRules.ValidateDigest(item.CacheKey, "AI_CACHE_KEY_INVALID");
                    if (item.UsageEventId is null || !caches.Add(item.CacheKey))
                    {
                        throw new InvalidOperationException("AI_CHECKPOINT_JOURNAL_ITEM_INVALID");
                    }
                }
            }
        }
        catch (InvalidOperationException exception)
        {
            throw new InvalidDataException("AI_CHECKPOINT_JOURNAL_CORRUPT", exception);
        }
    }

    private static void ValidateStagedManifest(
        AiAnalysisCheckpointCommand command,
        StagedAiAnalysisStateManifest staged,
        string payloadDigest)
    {
        ArgumentNullException.ThrowIfNull(staged);
        AiAnalysisCheckpointRules.ValidateDigest(payloadDigest, "AI_PAYLOAD_DIGEST_INVALID");
        if (!string.Equals(payloadDigest, AiAnalysisCheckpointRules.ComputePayloadDigest(command), StringComparison.Ordinal)
            || !string.Equals(staged.BatchId, command.Manifest.BatchId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("AI_CHECKPOINT_STAGE_COMMAND_MISMATCH");
        }

        AiAnalysisCheckpointRules.ValidateIdentifier(staged.StageToken, "AI_STAGE_TOKEN_INVALID");
        ValidateOwnedReference(staged.ManifestReference, "AI_MANIFEST_REFERENCE_INVALID");
        AiAnalysisCheckpointRules.ValidateDigest(staged.ManifestDigest, "AI_MANIFEST_DIGEST_INVALID");
    }

    private static async Task<AiCacheReference?> FindCacheByUsageIdAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string usageEventId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT cache_key, result_reference, consent_id, model_config_version, usage_event_id, created_at, is_reusable FROM ai_cache_references WHERE usage_event_id = $usage_event_id LIMIT 1;";
        command.Parameters.AddWithValue("$usage_event_id", usageEventId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadCacheReference(reader) : null;
    }

    private static void AddJournalParameters(SqliteCommand command, AiAnalysisCheckpointJournal journal)
    {
        command.Parameters.AddWithValue("$checkpoint_id", journal.CheckpointId);
        command.Parameters.AddWithValue("$batch_id", journal.BatchId);
        command.Parameters.AddWithValue("$manifest_id", journal.ManifestId);
        command.Parameters.AddWithValue("$stage_token", journal.StageToken);
        command.Parameters.AddWithValue("$manifest_reference", journal.ManifestReference);
        command.Parameters.AddWithValue("$manifest_digest", journal.ManifestDigest);
        command.Parameters.AddWithValue("$payload_digest", journal.PayloadDigest);
        command.Parameters.AddWithValue("$state", journal.State.ToString());
        command.Parameters.AddWithValue("$created_at", journal.CreatedAt.ToString("O"));
        command.Parameters.AddWithValue("$updated_at", journal.UpdatedAt.ToString("O"));
        command.Parameters.AddWithValue("$scheduler_state_key", (object?)journal.SchedulerStateKey ?? DBNull.Value);
        command.Parameters.AddWithValue("$scheduler_state_digest", (object?)journal.SchedulerStateDigest ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "$scheduler_state_json",
            journal.SchedulerState is null
                ? DBNull.Value
                : JsonSerializer.Serialize(journal.SchedulerState, AiJsonOptions));
    }

    private static void EnsureSameJournalIdentity(
        AiAnalysisCheckpointJournal expected,
        AiAnalysisCheckpointJournal actual)
    {
        if (!string.Equals(expected.CheckpointId, actual.CheckpointId, StringComparison.Ordinal)
            || !string.Equals(expected.BatchId, actual.BatchId, StringComparison.Ordinal)
            || !string.Equals(expected.ManifestId, actual.ManifestId, StringComparison.Ordinal)
            || !string.Equals(expected.StageToken, actual.StageToken, StringComparison.Ordinal)
            || !string.Equals(expected.ManifestReference, actual.ManifestReference, StringComparison.Ordinal)
            || !string.Equals(expected.ManifestDigest, actual.ManifestDigest, StringComparison.Ordinal)
            || !string.Equals(expected.PayloadDigest, actual.PayloadDigest, StringComparison.Ordinal)
            || !string.Equals(expected.SchedulerStateKey, actual.SchedulerStateKey, StringComparison.Ordinal)
            || !string.Equals(expected.SchedulerStateDigest, actual.SchedulerStateDigest, StringComparison.Ordinal)
            || (expected.SchedulerState is null) != (actual.SchedulerState is null)
            || (expected.SchedulerState is not null
                && !SchedulingStateEquals(expected.SchedulerState, actual.SchedulerState!))
            || expected.CreatedAt != actual.CreatedAt
            || !expected.Items.OrderBy(item => item.RequestId, StringComparer.Ordinal)
                .SequenceEqual(actual.Items.OrderBy(item => item.RequestId, StringComparer.Ordinal)))
        {
            throw new InvalidOperationException("AI_CHECKPOINT_JOURNAL_CAS_CONFLICT");
        }
    }

    private static AiAnalysisStateManifestV1 ManifestFromJournal(AiAnalysisCheckpointJournal journal) => new(
        AiAnalysisStateProtocol.Version,
        journal.ManifestId,
        journal.BatchId,
        journal.Items.Select(item => item.SessionId).ToArray(),
        journal.Items.Select(item => item.RequestId).ToArray(),
        journal.Items.Where(item => item.CacheKey is not null).Select(item => item.CacheKey!).ToArray(),
        journal.CreatedAt,
        journal.SchedulerStateDigest);

    private static void EnsurePriorGraphRetained(
        AiAnalysisCheckpointCommand command,
        AiAnalysisCheckpointJournal prior)
    {
        // S1 has no cache-invalidation command. Every reference in the active checkpoint
        // remains protected until a later, explicit cleanup protocol is introduced.
        var graphsByRequest = command.Graphs.ToDictionary(
            graph => graph.Request.RequestId,
            StringComparer.Ordinal);
        foreach (var item in prior.Items)
        {
            if (!graphsByRequest.TryGetValue(item.RequestId, out var graph)
                || !string.Equals(graph.Session.SessionId, item.SessionId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("AI_CHECKPOINT_PRIOR_GRAPH_OMITTED");
            }

            if (item.UsageEventId is not null
                && !string.Equals(graph.Usage?.UsageEventId, item.UsageEventId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("AI_CHECKPOINT_PRIOR_USAGE_OMITTED");
            }

            if (item.CacheKey is not null
                && !string.Equals(graph.Cache?.CacheKey, item.CacheKey, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("AI_CHECKPOINT_PRIOR_CACHE_OMITTED");
            }
        }
    }

    private static void EnsurePriorSchedulerStateRetained(
        AiAnalysisCheckpointCommand command,
        AiAnalysisCheckpointJournal prior)
    {
        var previous = prior.SchedulerState;
        if (previous is null)
        {
            return;
        }

        if (command.SchedulerState is null
            || !string.Equals(command.SchedulerStateKey, prior.SchedulerStateKey, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("AI_CHECKPOINT_PRIOR_SCHEDULER_OMITTED");
        }

        var next = command.SchedulerState;
        var requestReservationReleased = next.Budget.RequestCount < previous.Budget.RequestCount
            && IsCredentialPreflightReservationRelease(command, previous, next);
        if ((next.Budget.RequestCount < previous.Budget.RequestCount && !requestReservationReleased)
            || next.Budget.InputTokens < previous.Budget.InputTokens
            || next.Budget.OutputTokens < previous.Budget.OutputTokens
            || next.Budget.EstimatedCost < previous.Budget.EstimatedCost)
        {
            throw new InvalidOperationException("AI_CHECKPOINT_PRIOR_SCHEDULER_BUDGET_ROLLBACK");
        }

        if (previous.ControlState == AiAnalysisSchedulingControlState.Cancelled
            && next.ControlState != AiAnalysisSchedulingControlState.Cancelled)
        {
            throw new InvalidOperationException("AI_CHECKPOINT_PRIOR_SCHEDULER_CONTROL_ROLLBACK");
        }

        if (previous.ControlState == AiAnalysisSchedulingControlState.CancelRequested
            && next.ControlState is not (AiAnalysisSchedulingControlState.CancelRequested
                or AiAnalysisSchedulingControlState.Cancelled))
        {
            throw new InvalidOperationException("AI_CHECKPOINT_PRIOR_SCHEDULER_CONTROL_ROLLBACK");
        }

        var graphs = command.Graphs.ToDictionary(
            graph => graph.Request.RequestId,
            StringComparer.Ordinal);
        foreach (var previousItem in previous.PendingItems)
        {
            if (!graphs.TryGetValue(previousItem.Request.RequestId, out var graph))
            {
                throw new InvalidOperationException("AI_CHECKPOINT_PRIOR_SCHEDULER_PENDING_OMITTED");
            }

            // A pending work item may disappear only when its graph advances
            // to an explicit terminal/settled status.  Queued and retryable
            // work must remain represented in the new full-batch snapshot.
            if (graph.Request.Status is not ("Queued" or "RetryableError"))
            {
                continue;
            }

            var retained = next.PendingItems.Any(item =>
                string.Equals(item.Request.RequestId, previousItem.Request.RequestId, StringComparison.Ordinal)
                && item.Stage == previousItem.Stage
                && string.Equals(
                    AiAnalysisCacheKeyFactory.Create(item.Request),
                    AiAnalysisCacheKeyFactory.Create(previousItem.Request),
                    StringComparison.Ordinal)
                && item.Attempt >= previousItem.Attempt);
            if (!retained)
            {
                throw new InvalidOperationException("AI_CHECKPOINT_PRIOR_SCHEDULER_PENDING_OMITTED");
            }
        }
    }

    private static bool IsCredentialPreflightReservationRelease(
        AiAnalysisCheckpointCommand command,
        AiAnalysisSchedulingState previous,
        AiAnalysisSchedulingState next)
    {
        // RequestCount normally remains monotonic because crossing the HTTP
        // boundary may be billable even when no result is available.  The
        // only exception is the adapter's three credential checks: all are
        // guaranteed to return before an HttpRequestMessage is constructed.
        // The scheduler commits Analysing -> RetryableError and this one-count
        // release in the same checkpoint so a crash before the subsequent
        // ordinary RetryableError -> Queued hop still restores budget zero.
        // The exception cannot be reused by 401 responses, unknown outcomes
        // or generic terminal failures.
        if (previous.Budget.RequestCount != next.Budget.RequestCount + 1
            || previous.Budget.InputTokens != next.Budget.InputTokens
            || previous.Budget.OutputTokens != next.Budget.OutputTokens
            || previous.Budget.EstimatedCost != next.Budget.EstimatedCost
            || previous.Budget.WarningReached != next.Budget.WarningReached
            || previous.Budget.HardStopped != next.Budget.HardStopped
            || previous.ControlState != AiAnalysisSchedulingControlState.Running
            || previous.ControlReason is not null
            || next.ControlState != AiAnalysisSchedulingControlState.Paused
            || next.ControlReason is not (
                "AI_PROVIDER_CREDENTIAL_MISSING"
                or "AI_PROVIDER_CREDENTIAL_UNAVAILABLE"
                or "AI_PROVIDER_CREDENTIAL_INVALID"))
        {
            return false;
        }

        var releasedGraphs = command.Graphs.Where(graph =>
                graph.ExpectedRequestStatus == AiAnalysisSessionStatus.Analysing.ToString()
                && graph.Request.Status == AiAnalysisSessionStatus.RetryableError.ToString()
                && graph.ExpectedSessionStatus == AiAnalysisSessionStatus.Analysing
                && graph.Session.Status == AiAnalysisSessionStatus.RetryableError
                && graph.Usage is null
                && graph.Cache is null)
            .ToArray();
        if (releasedGraphs.Length != 1)
        {
            return false;
        }

        var releasedRequestId = releasedGraphs[0].Request.RequestId;
        return previous.PendingItems.Any(item =>
                string.Equals(item.Request.RequestId, releasedRequestId, StringComparison.Ordinal))
            && next.PendingItems.Any(item =>
                string.Equals(item.Request.RequestId, releasedRequestId, StringComparison.Ordinal))
            && command.Graphs.Where(graph =>
                    !string.Equals(graph.Request.RequestId, releasedRequestId, StringComparison.Ordinal))
                .All(graph =>
                    string.Equals(graph.ExpectedRequestStatus, graph.Request.Status, StringComparison.Ordinal)
                    && graph.ExpectedSessionStatus == graph.Session.Status);
    }

}
