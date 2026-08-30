using System.Globalization;
using System.Text.Json;
using Desktop.Application.Ai;
using Desktop.Contracts.Ai;
using Microsoft.Data.Sqlite;

namespace Desktop.Infrastructure.Persistence;

public sealed partial class SqliteDesktopStore
{
    public async Task<AiAnalysisSchedulingState?> LoadSchedulingStateAsync(
        string stateKey,
        CancellationToken cancellationToken)
    {
        ValidateIdentifier(stateKey, "AI_SCHEDULER_STATE_KEY_INVALID");
        await using var connection = await OpenReadConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT pending_items_json, request_count, input_tokens, output_tokens,
                   estimated_cost_text, warning_reached, hard_stopped, updated_at,
                   control_state, control_reason
            FROM ai_analysis_scheduler_state
            WHERE state_key = $state_key;
            """;
        command.Parameters.AddWithValue("$state_key", stateKey);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        try
        {
            return ReadSchedulingState(reader);
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (Exception exception) when (exception is
            JsonException or InvalidOperationException or InvalidCastException
            or FormatException or OverflowException or ArgumentException)
        {
            throw new InvalidDataException("AI_SCHEDULER_STATE_CORRUPT", exception);
        }
    }

    public Task SaveSchedulingStateAsync(
        string stateKey,
        AiAnalysisSchedulingState state,
        CancellationToken cancellationToken) => _writer.ExecuteAsync(
        async (connection, token) =>
        {
            ValidateIdentifier(stateKey, "AI_SCHEDULER_STATE_KEY_INVALID");
            ValidateSchedulingState(state);
            await using var transaction = (SqliteTransaction)await connection
                .BeginTransactionAsync(token).ConfigureAwait(false);
            await UpsertSchedulingStateAsync(connection, transaction, stateKey, state, token).ConfigureAwait(false);
            await transaction.CommitAsync(token).ConfigureAwait(false);
            return true;
        }, cancellationToken);

    private static async Task UpsertSchedulingStateAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string stateKey,
        AiAnalysisSchedulingState state,
        CancellationToken cancellationToken)
    {
        await using (var existingCommand = connection.CreateCommand())
        {
            existingCommand.Transaction = transaction;
            existingCommand.CommandText = """
                SELECT pending_items_json, request_count, input_tokens, output_tokens,
                       estimated_cost_text, warning_reached, hard_stopped, updated_at,
                       control_state, control_reason
                FROM ai_analysis_scheduler_state WHERE state_key = $state_key;
                """;
            existingCommand.Parameters.AddWithValue("$state_key", stateKey);
            await using var reader = await existingCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var existing = ReadSchedulingState(reader);
                if (state.UpdatedAt < existing.UpdatedAt
                    || (state.UpdatedAt == existing.UpdatedAt && !SchedulingStateEquals(existing, state)))
                {
                    throw new InvalidOperationException("AI_SCHEDULER_STATE_STALE_WRITE");
                }

                if (existing.ControlState == AiAnalysisSchedulingControlState.Cancelled
                    && state.ControlState != AiAnalysisSchedulingControlState.Cancelled)
                {
                    throw new InvalidOperationException("AI_SCHEDULER_CONTROL_STATE_ROLLBACK");
                }

                if (existing.ControlState == AiAnalysisSchedulingControlState.CancelRequested
                    && state.ControlState is not (AiAnalysisSchedulingControlState.CancelRequested
                        or AiAnalysisSchedulingControlState.Cancelled))
                {
                    throw new InvalidOperationException("AI_SCHEDULER_CONTROL_STATE_ROLLBACK");
                }

                if (SchedulingStateEquals(existing, state))
                {
                    return;
                }
            }
        }

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO ai_analysis_scheduler_state(
                state_key, pending_items_json, request_count, input_tokens,
                output_tokens, estimated_cost_text, warning_reached,
                hard_stopped, updated_at, control_state, control_reason)
            VALUES(
                $state_key, $pending_items_json, $request_count, $input_tokens,
                $output_tokens, $estimated_cost_text, $warning_reached,
                $hard_stopped, $updated_at, $control_state, $control_reason)
            ON CONFLICT(state_key) DO UPDATE SET
                pending_items_json = excluded.pending_items_json,
                request_count = excluded.request_count,
                input_tokens = excluded.input_tokens,
                output_tokens = excluded.output_tokens,
                estimated_cost_text = excluded.estimated_cost_text,
                warning_reached = excluded.warning_reached,
                hard_stopped = excluded.hard_stopped,
                updated_at = excluded.updated_at,
                control_state = excluded.control_state,
                control_reason = excluded.control_reason;
            """;
        command.Parameters.AddWithValue("$state_key", stateKey);
        command.Parameters.AddWithValue("$pending_items_json", JsonSerializer.Serialize(
            new PersistedSchedulerState(state.PendingItems, state.HistoricalRequests ?? []), AiJsonOptions));
        command.Parameters.AddWithValue("$request_count", state.Budget.RequestCount);
        command.Parameters.AddWithValue("$input_tokens", state.Budget.InputTokens);
        command.Parameters.AddWithValue("$output_tokens", state.Budget.OutputTokens);
        command.Parameters.AddWithValue("$estimated_cost_text", state.Budget.EstimatedCost.ToString("G29", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$warning_reached", state.Budget.WarningReached ? 1 : 0);
        command.Parameters.AddWithValue("$hard_stopped", state.Budget.HardStopped ? 1 : 0);
        command.Parameters.AddWithValue("$updated_at", state.UpdatedAt.ToString("O"));
        command.Parameters.AddWithValue("$control_state", state.ControlState.ToString());
        command.Parameters.AddWithValue("$control_reason", (object?)state.ControlReason ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static AiAnalysisSchedulingState ReadSchedulingState(Microsoft.Data.Sqlite.SqliteDataReader reader)
    {
        try
        {
            var (items, historicalRequests) = DeserializeSchedulerPayload(reader.GetString(0));
            var budget = new AiAnalysisBudgetSnapshot(
                reader.GetInt32(1), reader.GetInt32(2), reader.GetInt32(3),
                decimal.Parse(reader.GetString(4), NumberStyles.Float, CultureInfo.InvariantCulture),
                ReadBoolean(reader.GetInt32(5), "AI_SCHEDULER_STATE_CORRUPT"),
                ReadBoolean(reader.GetInt32(6), "AI_SCHEDULER_STATE_CORRUPT"));
            var controlState = Enum.Parse<AiAnalysisSchedulingControlState>(reader.GetString(8), false);
            if (!Enum.IsDefined(controlState))
            {
                throw new InvalidDataException("AI_SCHEDULER_CONTROL_STATE_CORRUPT");
            }

            var controlReason = reader.IsDBNull(9) ? null : reader.GetString(9);
            if (controlReason is not null && (controlReason.Length > 512 || controlReason.Contains('\0')))
            {
                throw new InvalidDataException("AI_SCHEDULER_CONTROL_REASON_CORRUPT");
            }

            var state = new AiAnalysisSchedulingState(
                items,
                budget,
                ParseTime(reader.GetString(7)),
                controlState,
                controlReason,
                historicalRequests);
            ValidateSchedulingState(state);
            return state;
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException
            or InvalidCastException or FormatException or OverflowException or ArgumentException)
        {
            throw new InvalidDataException("AI_SCHEDULER_STATE_CORRUPT", exception);
        }
    }

    private static bool SchedulingStateEquals(AiAnalysisSchedulingState left, AiAnalysisSchedulingState right) =>
        left.UpdatedAt == right.UpdatedAt
        && left.Budget == right.Budget
        && left.ControlState == right.ControlState
        && string.Equals(left.ControlReason, right.ControlReason, StringComparison.Ordinal)
        && JsonSerializer.Serialize(left.PendingItems, AiJsonOptions) == JsonSerializer.Serialize(right.PendingItems, AiJsonOptions)
        && JsonSerializer.Serialize(left.HistoricalRequests ?? [], AiJsonOptions)
            == JsonSerializer.Serialize(right.HistoricalRequests ?? [], AiJsonOptions);

    private static (AiAnalysisWorkItem[] Items, AiAnalysisRequestV1[] HistoricalRequests) DeserializeSchedulerPayload(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind == JsonValueKind.Array)
        {
            return (JsonSerializer.Deserialize<AiAnalysisWorkItem[]>(json, AiJsonOptions)
                        ?? throw new InvalidDataException("AI_SCHEDULER_STATE_CORRUPT"), []);
        }

        var payload = JsonSerializer.Deserialize<PersistedSchedulerState>(json, AiJsonOptions)
            ?? throw new InvalidDataException("AI_SCHEDULER_STATE_CORRUPT");
        return (payload.PendingItems?.ToArray() ?? throw new InvalidDataException("AI_SCHEDULER_STATE_CORRUPT"),
            payload.HistoricalRequests?.ToArray() ?? []);
    }

    private static async Task<AiAnalysisSchedulingState?> LoadSchedulingStateForWriteAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string stateKey,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT pending_items_json, request_count, input_tokens, output_tokens,
                   estimated_cost_text, warning_reached, hard_stopped, updated_at,
                   control_state, control_reason
            FROM ai_analysis_scheduler_state WHERE state_key = $state_key;
            """;
        command.Parameters.AddWithValue("$state_key", stateKey);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? ReadSchedulingState(reader)
            : null;
    }

    private static void ValidateSchedulingState(AiAnalysisSchedulingState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state.UpdatedAt == default)
        {
            throw new InvalidOperationException("AI_SCHEDULER_STATE_TIME_INVALID");
        }

        if (!Enum.IsDefined(state.ControlState)
            || (state.ControlReason is not null
                && (state.ControlReason.Length > 512 || state.ControlReason.Contains('\0'))))
        {
            throw new InvalidOperationException("AI_SCHEDULER_CONTROL_STATE_INVALID");
        }

        var budget = state.Budget;
        if (budget.RequestCount < 0
            || budget.InputTokens < 0
            || budget.OutputTokens < 0
            || budget.EstimatedCost < 0m)
        {
            throw new InvalidOperationException("AI_SCHEDULER_BUDGET_INVALID");
        }

        var items = state.PendingItems ?? throw new InvalidOperationException("AI_SCHEDULER_STATE_CORRUPT");
        if (items.Count > 10_000)
        {
            throw new InvalidOperationException("AI_SCHEDULER_STATE_TOO_LARGE");
        }

        var workIds = new HashSet<string>(StringComparer.Ordinal);
        var logicalIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            ValidateSchedulingWorkItem(item);
            if (!workIds.Add(item.WorkItemId))
            {
                throw new InvalidOperationException("AI_SCHEDULER_DUPLICATE_WORK_ITEM");
            }

            var logicalId = $"{item.Request.RequestId}|{item.Stage}";
            if (!logicalIds.Add(logicalId))
            {
                throw new InvalidOperationException("AI_SCHEDULER_DUPLICATE_WORK_ITEM");
            }
        }

        var history = state.HistoricalRequests ?? [];
        if (history.Count > 10_000)
        {
            throw new InvalidOperationException("AI_SCHEDULER_HISTORY_TOO_LARGE");
        }

        var historyIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var request in history)
        {
            if (request is null || !historyIds.Add(request.RequestId))
            {
                throw new InvalidOperationException("AI_SCHEDULER_HISTORY_INVALID");
            }

            ValidateIdentifier(request.RequestId, "AI_REQUEST_ID_INVALID");
            ValidateIdentifier(request.BatchId, "AI_BATCH_ID_INVALID");
            ValidateIdentifier(request.StyleId, "AI_STYLE_ID_INVALID");
        }
    }

    private static void ValidateSchedulingWorkItem(AiAnalysisWorkItem item)
    {
        if (item is null
            || string.IsNullOrWhiteSpace(item.WorkItemId)
            || item.WorkItemId.Length > 256
            || item.WorkItemId.Any(character => character is '\\' or '/' or '\0')
            || item.Attempt is < 0 or > 100
            || item.EnqueuedAt == default)
        {
            throw new InvalidOperationException("AI_SCHEDULER_WORK_ITEM_INVALID");
        }

        if (!Enum.IsDefined(item.Stage))
        {
            throw new InvalidOperationException("AI_SCHEDULER_STAGE_INVALID");
        }

        var request = item.Request ?? throw new InvalidOperationException("AI_SCHEDULER_REQUEST_INVALID");
        var expectedKind = item.Stage switch
        {
            AiAnalysisWorkStage.ProductJointUnderstanding => AiAnalysisKind.ProductJointUnderstanding,
            AiAnalysisWorkStage.SelectedAssetGeometry => AiAnalysisKind.SelectedAssetGeometry,
            AiAnalysisWorkStage.SizeTableExtraction => AiAnalysisKind.SizeTableExtraction,
            _ => throw new InvalidOperationException("AI_SCHEDULER_STAGE_INVALID")
        };
        if (!string.Equals(request.AnalysisKind, expectedKind, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("AI_SCHEDULER_STAGE_KIND_MISMATCH");
        }
        ValidateIdentifier(request.RequestId, "AI_REQUEST_ID_INVALID");
        ValidateIdentifier(request.CorrelationId, "AI_CORRELATION_ID_INVALID");
        ValidateIdentifier(request.ProductId, "AI_PRODUCT_ID_INVALID");
        ValidateIdentifier(request.BatchId, "AI_BATCH_ID_INVALID");
        ValidateIdentifier(request.StyleId, "AI_STYLE_ID_INVALID");
        ValidateIdentifier(request.TemplateSnapshotId, "AI_SNAPSHOT_ID_INVALID");
        ValidateIdentifier(request.TemplateRevisionId, "AI_TEMPLATE_REVISION_ID_INVALID");
        ValidateOpaqueReference(request.ModelConfigVersion, "AI_MODEL_CONFIG_INVALID");
        ValidateOpaqueReference(request.PromptContractVersion, "AI_PROMPT_VERSION_INVALID");
        ValidateOpaqueReference(request.ResponseSchemaVersion, "AI_SCHEMA_VERSION_INVALID");
        ValidateOpaqueReference(request.CompressionProfileVersion, "AI_COMPRESSION_VERSION_INVALID");
        ValidateOpaqueReference(request.ConsentId, "AI_CONSENT_ID_INVALID");
        ValidateDigest(request.RequirementsDigest, "AI_REQUIREMENTS_DIGEST_INVALID");
        if (request.Assets is null || request.Assets.Count == 0
            || request.Requirements is null || request.Requirements.Count == 0)
        {
            throw new InvalidOperationException("AI_SCHEDULER_REQUEST_INVALID");
        }

        foreach (var asset in request.Assets)
        {
            ValidateIdentifier(asset.AssetId, "AI_ASSET_ID_INVALID");
            ValidateDigest(asset.ContentHash, "AI_ASSET_HASH_INVALID");
            if (string.IsNullOrWhiteSpace(asset.AnalysisCopyReference)
                || asset.AnalysisCopyReference.Length > 512
                || asset.AnalysisCopyReference.Contains('\\')
                || asset.AnalysisCopyReference.Contains(':')
                || asset.AnalysisCopyReference.Contains('\0')
                || asset.AnalysisCopyReference.Split('/').Any(segment => segment is "" or "." or ".."))
            {
                throw new InvalidOperationException("AI_SCHEDULER_SOURCE_PATH_INVALID");
            }
        }

        foreach (var requirement in request.Requirements)
        {
            ValidateIdentifier(requirement.RequirementId, "AI_REQUIREMENT_ID_INVALID");
        }

        if (item.ParentRequestId is not null)
        {
            ValidateIdentifier(item.ParentRequestId, "AI_PARENT_REQUEST_ID_INVALID");
        }
    }

    private sealed record PersistedSchedulerState(
        IReadOnlyList<AiAnalysisWorkItem> PendingItems,
        IReadOnlyList<AiAnalysisRequestV1> HistoricalRequests);

    private static bool ReadBoolean(int value, string code) => value switch
    {
        0 => false,
        1 => true,
        _ => throw new InvalidDataException(code)
    };
}
