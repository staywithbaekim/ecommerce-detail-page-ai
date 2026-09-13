using Desktop.Domain.Ai;

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Desktop.Application.Ai;

public static class AiAnalysisCheckpointProtocol
{
    public const string Version = "ai-analysis-checkpoint/v1";
}

public enum AiAnalysisCheckpointOutcome
{
    Committed = 0,
    RolledBackOrphan,
    NothingToRecover,
    RecoveryRequired
}

public enum AiAnalysisCheckpointJournalState
{
    Pending = 0,
    Committed
}

public sealed record AiAnalysisCheckpointGraph(
    AiRequestLedgerEntry Request,
    string ExpectedRequestStatus,
    AiAnalysisSession Session,
    AiAnalysisSessionStatus ExpectedSessionStatus,
    long ExpectedSelectionRevision,
    AiUsageEventRecord? Usage,
    AiCacheReference? Cache);

/// <summary>
/// Represents one versioned, full-batch checkpoint. <see cref="Graphs"/> contains every
/// request/session graph retained by the batch manifest, not only the style being changed.
/// </summary>
public sealed record AiAnalysisCheckpointCommand(
    string CheckpointId,
    AiAnalysisStateManifestV1 Manifest,
    IReadOnlyList<AiAnalysisCheckpointGraph> Graphs,
    DateTimeOffset CreatedAt,
    string? SchedulerStateKey = null,
    AiAnalysisSchedulingState? SchedulerState = null);

public sealed record AiAnalysisCheckpointJournalItem(
    string RequestId,
    string SessionId,
    string? UsageEventId,
    string? CacheKey,
    string ExpectedRequestStatus,
    AiAnalysisSessionStatus ExpectedSessionStatus,
    long ExpectedSelectionRevision);

public sealed record AiAnalysisCheckpointJournal(
    string CheckpointId,
    string BatchId,
    string ManifestId,
    string StageToken,
    string ManifestReference,
    string ManifestDigest,
    string PayloadDigest,
    IReadOnlyList<AiAnalysisCheckpointJournalItem> Items,
    AiAnalysisCheckpointJournalState State,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string? SchedulerStateKey = null,
    string? SchedulerStateDigest = null,
    AiAnalysisSchedulingState? SchedulerState = null);

public sealed record AiAnalysisCheckpointResult(
    string? CheckpointId,
    string BatchId,
    AiAnalysisCheckpointOutcome Outcome,
    string ReasonCode);

public interface IAiAnalysisCheckpointPort
{
    Task<AiAnalysisCheckpointResult> CommitAsync(
        AiAnalysisCheckpointCommand command,
        CancellationToken cancellationToken);

    Task<AiAnalysisCheckpointResult> RecoverAsync(
        string batchId,
        CancellationToken cancellationToken);
}

/// <summary>Canonical digest shared by the scheduler and infrastructure coordinator.</summary>
public static class AiAnalysisCheckpointStateDigest
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static string Compute(AiAnalysisSchedulingState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var canonical = new
        {
            pending_items = state.PendingItems
                .OrderBy(item => item.WorkItemId, StringComparer.Ordinal)
                .Select(item => new
                {
                    item.WorkItemId,
                    request = new
                    {
                        item.Request.RequestId,
                        item.Request.CorrelationId,
                        item.Request.ProductId,
                        item.Request.BatchId,
                        item.Request.StyleId,
                        item.Request.TemplateSnapshotId,
                        item.Request.AnalysisKind,
                        item.Request.ModelConfigVersion,
                        item.Request.PromptContractVersion,
                        item.Request.ResponseSchemaVersion,
                        item.Request.CompressionProfileVersion,
                        item.Request.ConsentId,
                        item.Request.RequirementsDigest,
                        assets = item.Request.Assets.OrderBy(asset => asset.AssetId, StringComparer.Ordinal),
                        requirements = item.Request.Requirements.OrderBy(requirement => requirement.RequirementId, StringComparer.Ordinal)
                    },
                    stage = item.Stage.ToString(),
                    item.Attempt,
                    EnqueuedAt = item.EnqueuedAt.ToString("O"),
                    item.ParentRequestId
                }).ToArray(),
            historical_requests = (state.HistoricalRequests ?? [])
                .OrderBy(request => request.RequestId, StringComparer.Ordinal)
                .ToArray(),
            terminal_errors = (state.TerminalErrors ?? Enumerable.Empty<KeyValuePair<string, string>>())
                .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => new { request_id = pair.Key, error_code = pair.Value })
                .ToArray(),
            budget = new
            {
                state.Budget.RequestCount,
                state.Budget.InputTokens,
                state.Budget.OutputTokens,
                EstimatedCost = state.Budget.EstimatedCost.ToString("G29", System.Globalization.CultureInfo.InvariantCulture),
                state.Budget.WarningReached,
                state.Budget.HardStopped
            },
            control = new { State = state.ControlState.ToString(), state.ControlReason },
            UpdatedAt = state.UpdatedAt.ToString("O")
        };
        return "sha256:" + Convert.ToHexString(
            SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(canonical, JsonOptions))).ToLowerInvariant();
    }
}
