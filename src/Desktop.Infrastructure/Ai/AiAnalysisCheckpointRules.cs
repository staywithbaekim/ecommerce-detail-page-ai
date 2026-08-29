using System.Security.Cryptography;
using System.Text.Json;
using Desktop.Application.Ai;
using Desktop.Domain.Ai;

namespace Desktop.Infrastructure.Ai;

internal static class AiAnalysisCheckpointRules
{
    private const int MaximumGraphs = 4096;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static void Validate(AiAnalysisCheckpointCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        ValidateIdentifier(command.CheckpointId, "AI_CHECKPOINT_ID_INVALID");
        ArgumentNullException.ThrowIfNull(command.Manifest);
        ArgumentNullException.ThrowIfNull(command.Graphs);

        if (command.Graphs.Count is 0 or > MaximumGraphs)
        {
            throw new InvalidOperationException("AI_CHECKPOINT_GRAPH_COUNT_INVALID");
        }

        if (!string.Equals(command.Manifest.SchemaVersion, AiAnalysisStateProtocol.Version, StringComparison.Ordinal)
            || !string.Equals(command.Manifest.ManifestId, command.CheckpointId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("AI_CHECKPOINT_IDENTITY_CLOSURE_INVALID");
        }

        ValidateIdentifier(command.Manifest.BatchId, "AI_BATCH_ID_INVALID");
        ValidateTimestamp(command.CreatedAt, "AI_CHECKPOINT_TIME_CLOSURE_INVALID");
        ValidateTimestamp(command.Manifest.CreatedAt, "AI_CHECKPOINT_TIME_CLOSURE_INVALID");
        if (command.CreatedAt.Offset != TimeSpan.Zero
            || command.Manifest.CreatedAt.Offset != TimeSpan.Zero
            || command.Manifest.CreatedAt != command.CreatedAt)
        {
            throw new InvalidOperationException("AI_CHECKPOINT_TIME_CLOSURE_INVALID");
        }

        var requestIds = new HashSet<string>(StringComparer.Ordinal);
        var sessionIds = new HashSet<string>(StringComparer.Ordinal);
        var usageIds = new HashSet<string>(StringComparer.Ordinal);
        var cacheKeys = new HashSet<string>(StringComparer.Ordinal);

        foreach (var graph in command.Graphs)
        {
            ValidateGraph(command, graph, requestIds, sessionIds, usageIds, cacheKeys);
        }

        if (!SetEqualsExact(command.Manifest.RequestIds, requestIds, ValidateIdentifier)
            || !SetEqualsExact(command.Manifest.SessionIds, sessionIds, ValidateIdentifier)
            || !SetEqualsExact(command.Manifest.CacheKeys, cacheKeys, ValidateDigest))
        {
            throw new InvalidOperationException("AI_CHECKPOINT_MANIFEST_GRAPH_INVALID");
        }
    }

    public static string ComputePayloadDigest(AiAnalysisCheckpointCommand command)
    {
        Validate(command);
        var payload = new
        {
            schema_version = AiAnalysisCheckpointProtocol.Version,
            checkpoint_id = command.CheckpointId,
            manifest = new
            {
                command.Manifest.SchemaVersion,
                command.Manifest.ManifestId,
                command.Manifest.BatchId,
                SessionIds = command.Manifest.SessionIds.Order(StringComparer.Ordinal).ToArray(),
                RequestIds = command.Manifest.RequestIds.Order(StringComparer.Ordinal).ToArray(),
                CacheKeys = command.Manifest.CacheKeys.Order(StringComparer.Ordinal).ToArray(),
                CreatedAt = command.Manifest.CreatedAt.ToString("O")
            },
            graphs = command.Graphs
                .OrderBy(graph => graph.Request.RequestId, StringComparer.Ordinal)
                .Select(graph => new
                {
                    request = graph.Request,
                    expected_request_status = graph.ExpectedRequestStatus,
                    session = SessionPayload(graph.Session),
                    expected_session_status = graph.ExpectedSessionStatus.ToString(),
                    graph.ExpectedSelectionRevision,
                    usage = graph.Usage,
                    cache = graph.Cache
                })
                .ToArray(),
            created_at = command.CreatedAt.ToString("O")
        };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions);
        return "sha256:" + Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    public static void EnsureCommandMatchesJournal(
        AiAnalysisCheckpointCommand command,
        string payloadDigest,
        AiAnalysisCheckpointJournal journal)
    {
        var expectedItems = command.Graphs
            .Select(ToJournalItem)
            .OrderBy(item => item.RequestId, StringComparer.Ordinal)
            .ToArray();
        var actualItems = journal.Items
            .OrderBy(item => item.RequestId, StringComparer.Ordinal)
            .ToArray();

        if (!string.Equals(journal.CheckpointId, command.CheckpointId, StringComparison.Ordinal)
            || !string.Equals(journal.BatchId, command.Manifest.BatchId, StringComparison.Ordinal)
            || !string.Equals(journal.ManifestId, command.Manifest.ManifestId, StringComparison.Ordinal)
            || !string.Equals(journal.PayloadDigest, payloadDigest, StringComparison.Ordinal)
            || journal.CreatedAt != command.CreatedAt
            || !expectedItems.SequenceEqual(actualItems))
        {
            throw new InvalidOperationException("AI_CHECKPOINT_IDEMPOTENCY_CONFLICT");
        }
    }

    public static AiAnalysisCheckpointJournalItem ToJournalItem(AiAnalysisCheckpointGraph graph) => new(
        graph.Request.RequestId,
        graph.Session.SessionId,
        graph.Usage?.UsageEventId,
        graph.Cache?.CacheKey,
        graph.ExpectedRequestStatus,
        graph.ExpectedSessionStatus,
        graph.ExpectedSelectionRevision);

    public static void EnsureManifestHistoryRetained(
        AiAnalysisStateManifestV1 previous,
        AiAnalysisStateManifestV1 next)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(next);
        if (!string.Equals(previous.BatchId, next.BatchId, StringComparison.Ordinal)
            || !IsSubset(previous.RequestIds, next.RequestIds)
            || !IsSubset(previous.SessionIds, next.SessionIds)
            || !IsSubset(previous.CacheKeys, next.CacheKeys))
        {
            throw new InvalidOperationException("AI_CHECKPOINT_PRIOR_MANIFEST_REFERENCE_OMITTED");
        }
    }

    public static bool StageMatches(
        AiAnalysisCheckpointJournal journal,
        StagedAiAnalysisStateManifest staged) =>
        string.Equals(journal.BatchId, staged.BatchId, StringComparison.Ordinal)
        && string.Equals(journal.StageToken, staged.StageToken, StringComparison.Ordinal)
        && string.Equals(journal.ManifestReference, staged.ManifestReference, StringComparison.Ordinal)
        && string.Equals(journal.ManifestDigest, staged.ManifestDigest, StringComparison.Ordinal);

    public static bool CommittedMatches(
        AiAnalysisCheckpointJournal journal,
        AiAnalysisStateManifestInspection inspection) =>
        inspection.IsCommitted
        && string.Equals(journal.BatchId, inspection.BatchId, StringComparison.Ordinal)
        && string.Equals(journal.ManifestReference, inspection.CommittedManifestReference, StringComparison.Ordinal)
        && string.Equals(journal.ManifestDigest, inspection.CommittedManifestDigest, StringComparison.Ordinal)
        && string.Equals(journal.ManifestId, inspection.CommittedManifest?.ManifestId, StringComparison.Ordinal);

    public static bool StagedMatches(
        AiAnalysisCheckpointJournal journal,
        AiAnalysisStateManifestInspection inspection) =>
        inspection.StagedManifest is not null
        && inspection.StagedStateManifest is not null
        && StageMatches(journal, inspection.StagedManifest)
        && string.Equals(journal.ManifestId, inspection.StagedStateManifest.ManifestId, StringComparison.Ordinal);

    public static void ValidateIdentifier(string value, string code)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128
            || value.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '_' or '-')))
        {
            throw new InvalidOperationException(code);
        }
    }

    public static void ValidateDigest(string value, string code)
    {
        if (value is null
            || value.Length != 71
            || !value.StartsWith("sha256:", StringComparison.Ordinal)
            || value.Skip(7).Any(character => !((character >= '0' && character <= '9')
                || (character >= 'a' && character <= 'f'))))
        {
            throw new InvalidOperationException(code);
        }
    }

    private static void ValidateGraph(
        AiAnalysisCheckpointCommand command,
        AiAnalysisCheckpointGraph graph,
        HashSet<string> requestIds,
        HashSet<string> sessionIds,
        HashSet<string> usageIds,
        HashSet<string> cacheKeys)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(graph.Request);
        ArgumentNullException.ThrowIfNull(graph.Session);

        ValidateIdentifier(graph.Request.RequestId, "AI_REQUEST_ID_INVALID");
        ValidateIdentifier(graph.Session.SessionId, "AI_SESSION_ID_INVALID");
        ValidateDigest(graph.Request.Fingerprint, "AI_CHECKPOINT_FINGERPRINT_INVALID");

        if (!requestIds.Add(graph.Request.RequestId)
            || !sessionIds.Add(graph.Session.SessionId))
        {
            throw new InvalidOperationException("AI_CHECKPOINT_GRAPH_DUPLICATE");
        }

        if (!string.Equals(command.Manifest.BatchId, graph.Request.BatchId, StringComparison.Ordinal)
            || !string.Equals(command.Manifest.BatchId, graph.Session.BatchId, StringComparison.Ordinal)
            || !string.Equals(graph.Request.StyleId, graph.Session.StyleId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("AI_CHECKPOINT_IDENTITY_CLOSURE_INVALID");
        }

        if (!TryStatus(graph.Request.Status, out var requestStatus)
            || requestStatus != graph.Session.Status
            || !TryStatus(graph.ExpectedRequestStatus, out var expectedRequestStatus)
            || expectedRequestStatus != graph.ExpectedSessionStatus)
        {
            throw new InvalidOperationException("AI_CHECKPOINT_STATUS_CLOSURE_INVALID");
        }

        var requiresResult = RequiresResult(requestStatus);
        if (requiresResult != (graph.Request.ResultReference is not null)
            || graph.Session.HasVerifiedResult != requiresResult)
        {
            throw new InvalidOperationException("AI_CHECKPOINT_RESULT_CLOSURE_INVALID");
        }

        if (graph.ExpectedSelectionRevision < 0
            || graph.Session.SelectionRevision < graph.ExpectedSelectionRevision
            || graph.Session.SelectionRevision > graph.ExpectedSelectionRevision + 1)
        {
            throw new InvalidOperationException("AI_CHECKPOINT_SELECTION_REVISION_INVALID");
        }

        ValidateTimestamp(graph.Request.CreatedAt, "AI_CHECKPOINT_TIME_CLOSURE_INVALID");
        ValidateTimestamp(graph.Request.UpdatedAt, "AI_CHECKPOINT_TIME_CLOSURE_INVALID");
        if (graph.Request.CreatedAt > graph.Request.UpdatedAt
            || graph.Request.UpdatedAt > command.CreatedAt)
        {
            throw new InvalidOperationException("AI_CHECKPOINT_TIME_CLOSURE_INVALID");
        }

        if (graph.Usage is not null)
        {
            ValidateIdentifier(graph.Usage.UsageEventId, "AI_USAGE_ID_INVALID");
            ValidateUsage(graph.Usage);
            if (!usageIds.Add(graph.Usage.UsageEventId)
                || !string.Equals(graph.Usage.RequestId, graph.Request.RequestId, StringComparison.Ordinal)
                || graph.Usage.RecordedAt < graph.Request.CreatedAt
                || graph.Usage.RecordedAt > command.CreatedAt)
            {
                throw new InvalidOperationException("AI_CHECKPOINT_USAGE_CLOSURE_INVALID");
            }
        }

        if (graph.Cache is null)
        {
            return;
        }

        if (graph.Usage is null)
        {
            throw new InvalidOperationException("AI_CHECKPOINT_CACHE_USAGE_REQUIRED");
        }

        ValidateDigest(graph.Cache.CacheKey, "AI_CHECKPOINT_CACHE_KEY_INVALID");
        if (!cacheKeys.Add(graph.Cache.CacheKey)
            || !requiresResult
            || !string.Equals(graph.Request.ResultReference, graph.Cache.ResultReference, StringComparison.Ordinal)
            || !string.Equals(graph.Cache.UsageEventId, graph.Usage.UsageEventId, StringComparison.Ordinal)
            || !string.Equals(graph.Cache.ModelConfigVersion, graph.Usage.ModelConfigVersion, StringComparison.Ordinal)
            || graph.Cache.CreatedAt < graph.Usage.RecordedAt
            || graph.Cache.CreatedAt > command.CreatedAt)
        {
            throw new InvalidOperationException("AI_CHECKPOINT_USAGE_CACHE_CLOSURE_INVALID");
        }
    }

    private static bool SetEqualsExact(
        IReadOnlyList<string> declared,
        HashSet<string> expected,
        Action<string, string> validate)
    {
        ArgumentNullException.ThrowIfNull(declared);
        if (declared.Count != expected.Count)
        {
            return false;
        }

        var actual = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in declared)
        {
            validate(value, "AI_CHECKPOINT_MANIFEST_REFERENCE_INVALID");
            if (!actual.Add(value))
            {
                return false;
            }
        }

        return actual.SetEquals(expected);
    }

    private static bool IsSubset(IReadOnlyList<string> previous, IReadOnlyList<string> next)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(next);
        var nextSet = next.ToHashSet(StringComparer.Ordinal);
        return previous.All(nextSet.Contains);
    }

    private static object SessionPayload(AiAnalysisSession session) => new
    {
        session.SessionId,
        session.BatchId,
        session.StyleId,
        session.TemplateSnapshotId,
        session.AnalysisKey,
        Status = session.Status.ToString(),
        session.SelectionRevision,
        AllowedRequirementIds = session.AllowedRequirementIds.Order(StringComparer.Ordinal).ToArray(),
        AllowedAssetIds = session.AllowedAssetIds.Order(StringComparer.Ordinal).ToArray(),
        LockedSelections = session.LockedSelections.Values
            .OrderBy(item => item.RequirementId, StringComparer.Ordinal)
            .ToArray(),
        session.HasVerifiedResult
    };

    private static bool TryStatus(string value, out AiAnalysisSessionStatus status) =>
        Enum.TryParse(value, false, out status) && Enum.IsDefined(status);

    private static bool RequiresResult(AiAnalysisSessionStatus status) => status is
        AiAnalysisSessionStatus.Validated or AiAnalysisSessionStatus.ManualReview or AiAnalysisSessionStatus.PlanClosed;

    private static void ValidateUsage(AiUsageEventRecord usage)
    {
        if (usage.InputUnits < 0 || usage.OutputUnits < 0 || usage.RetryCount < 0
            || (usage.ProviderCostAmount is null) != (usage.ProviderCostCurrency is null)
            || usage.ProviderCostAmount < 0
            || (usage.ProviderCostCurrency is not null
                && !System.Text.RegularExpressions.Regex.IsMatch(usage.ProviderCostCurrency, "^[A-Z]{3}$"))
            || string.IsNullOrWhiteSpace(usage.ModelConfigVersion)
            || usage.ModelConfigVersion.Length > 512)
        {
            throw new InvalidOperationException("AI_CHECKPOINT_USAGE_INVALID");
        }

        ValidateTimestamp(usage.RecordedAt, "AI_CHECKPOINT_TIME_CLOSURE_INVALID");
    }

    private static void ValidateTimestamp(DateTimeOffset value, string code)
    {
        if (value == default)
        {
            throw new InvalidOperationException(code);
        }
    }
}
