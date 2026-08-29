using System.Collections.Immutable;

namespace Desktop.Domain.Ai;

public enum AiAnalysisSessionStatus
{
    Queued = 0,
    Uploading,
    Analysing,
    Validated,
    ManualReview,
    PlanClosed,
    RetryableError,
    TerminalError,
    Cancelled,
    AiResultUnknown
}

public sealed class AiSelectionConcurrencyException(string code) : InvalidOperationException(code)
{
    public string Code { get; } = code;
}

public sealed record AiLockedSelection(
    string RequirementId,
    string AssetId,
    long SelectionRevision);

public sealed record AiAnalysisSession
{
    private AiAnalysisSession(
        string sessionId,
        string batchId,
        string styleId,
        string templateSnapshotId,
        string analysisKey,
        AiAnalysisSessionStatus status,
        long selectionRevision,
        ImmutableDictionary<string, AiLockedSelection> lockedSelections,
        ImmutableHashSet<string> allowedRequirementIds,
        ImmutableHashSet<string> allowedAssetIds,
        bool hasVerifiedResult)
    {
        SessionId = sessionId;
        BatchId = batchId;
        StyleId = styleId;
        TemplateSnapshotId = templateSnapshotId;
        AnalysisKey = analysisKey;
        Status = status;
        SelectionRevision = selectionRevision;
        LockedSelections = lockedSelections;
        AllowedRequirementIds = allowedRequirementIds;
        AllowedAssetIds = allowedAssetIds;
        HasVerifiedResult = hasVerifiedResult;
    }

    public string SessionId { get; }
    public string BatchId { get; }
    public string StyleId { get; }
    public string TemplateSnapshotId { get; }
    public string AnalysisKey { get; }
    public AiAnalysisSessionStatus Status { get; }
    public long SelectionRevision { get; }
    public IReadOnlyDictionary<string, AiLockedSelection> LockedSelections { get; }
    public IReadOnlySet<string> AllowedRequirementIds { get; }
    public IReadOnlySet<string> AllowedAssetIds { get; }
    public bool HasVerifiedResult { get; }

    public static AiAnalysisSession Create(
        string sessionId,
        string batchId,
        string styleId,
        string templateSnapshotId,
        string analysisKey,
        IEnumerable<string> allowedRequirementIds,
        IEnumerable<string> allowedAssetIds) => new(
            Identifier(sessionId, "AI_SESSION_ID_INVALID"),
            Identifier(batchId, "AI_BATCH_ID_INVALID"),
            Identifier(styleId, "AI_STYLE_ID_INVALID"),
            Identifier(templateSnapshotId, "AI_SNAPSHOT_ID_INVALID"),
            Identifier(analysisKey, "AI_ANALYSIS_KEY_INVALID"),
            AiAnalysisSessionStatus.Queued,
            0,
            ImmutableDictionary<string, AiLockedSelection>.Empty,
            Identifiers(allowedRequirementIds, "AI_REQUIREMENTS_INVALID"),
            Identifiers(allowedAssetIds, "AI_ASSETS_INVALID"),
            false);

    public AiAnalysisSession AdvanceTo(AiAnalysisSessionStatus next)
    {
        if (Status is AiAnalysisSessionStatus.Cancelled or AiAnalysisSessionStatus.PlanClosed
            or AiAnalysisSessionStatus.TerminalError)
        {
            throw new InvalidOperationException("AI_SESSION_TERMINAL");
        }

        if (!IsAllowed(Status, next))
        {
            throw new InvalidOperationException("AI_SESSION_TRANSITION_INVALID");
        }

        return Copy(
            next,
            SelectionRevision,
            LockedSelections.ToImmutableDictionary(StringComparer.Ordinal),
            next == AiAnalysisSessionStatus.Validated || HasVerifiedResult);
    }

    public AiAnalysisSession LockSelection(
        string requirementId,
        string assetId,
        long expectedSelectionRevision)
    {
        if (expectedSelectionRevision != SelectionRevision)
        {
            throw new AiSelectionConcurrencyException("AI_SELECTION_REVISION_CONFLICT");
        }

        if (Status is not (AiAnalysisSessionStatus.Validated or AiAnalysisSessionStatus.ManualReview))
        {
            throw new InvalidOperationException("AI_SELECTION_NOT_AVAILABLE");
        }

        if (!AllowedRequirementIds.Contains(requirementId))
        {
            throw new InvalidOperationException("AI_SELECTION_REQUIREMENT_UNKNOWN");
        }

        if (!AllowedAssetIds.Contains(assetId))
        {
            throw new InvalidOperationException("AI_SELECTION_ASSET_UNKNOWN");
        }

        var nextRevision = SelectionRevision + 1;
        var selection = new AiLockedSelection(
            Identifier(requirementId, "AI_REQUIREMENT_ID_INVALID"),
            Identifier(assetId, "AI_ASSET_ID_INVALID"),
            nextRevision);
        return Copy(
            AiAnalysisSessionStatus.ManualReview,
            nextRevision,
            LockedSelections.ToImmutableDictionary(StringComparer.Ordinal)
                .SetItem(selection.RequirementId, selection),
            HasVerifiedResult);
    }

    private AiAnalysisSession Copy(
        AiAnalysisSessionStatus status,
        long selectionRevision,
        ImmutableDictionary<string, AiLockedSelection> lockedSelections,
        bool hasVerifiedResult) => new(
            SessionId,
            BatchId,
            StyleId,
            TemplateSnapshotId,
            AnalysisKey,
            status,
            selectionRevision,
            lockedSelections,
            AllowedRequirementIds.ToImmutableHashSet(StringComparer.Ordinal),
            AllowedAssetIds.ToImmutableHashSet(StringComparer.Ordinal),
            hasVerifiedResult);

    private static bool IsAllowed(AiAnalysisSessionStatus current, AiAnalysisSessionStatus next) =>
        (current, next) is
            (AiAnalysisSessionStatus.Queued, AiAnalysisSessionStatus.Uploading)
            or (AiAnalysisSessionStatus.Uploading, AiAnalysisSessionStatus.Analysing)
            or (AiAnalysisSessionStatus.Analysing, AiAnalysisSessionStatus.Validated)
            or (AiAnalysisSessionStatus.Analysing, AiAnalysisSessionStatus.RetryableError)
            or (AiAnalysisSessionStatus.Uploading, AiAnalysisSessionStatus.TerminalError)
            or (AiAnalysisSessionStatus.Analysing, AiAnalysisSessionStatus.TerminalError)
            or (AiAnalysisSessionStatus.Analysing, AiAnalysisSessionStatus.AiResultUnknown)
            or (AiAnalysisSessionStatus.Validated, AiAnalysisSessionStatus.ManualReview)
            or (AiAnalysisSessionStatus.Validated, AiAnalysisSessionStatus.PlanClosed)
            or (AiAnalysisSessionStatus.ManualReview, AiAnalysisSessionStatus.PlanClosed)
            or (AiAnalysisSessionStatus.RetryableError, AiAnalysisSessionStatus.Queued)
            or (AiAnalysisSessionStatus.AiResultUnknown, AiAnalysisSessionStatus.TerminalError)
            or (AiAnalysisSessionStatus.AiResultUnknown, AiAnalysisSessionStatus.Cancelled)
            or (AiAnalysisSessionStatus.Queued, AiAnalysisSessionStatus.Cancelled)
            or (AiAnalysisSessionStatus.Uploading, AiAnalysisSessionStatus.Cancelled)
            or (AiAnalysisSessionStatus.Analysing, AiAnalysisSessionStatus.Cancelled)
            or (AiAnalysisSessionStatus.RetryableError, AiAnalysisSessionStatus.Cancelled);

    private static string Identifier(string value, string code)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Length > 128
            || value.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '_' or '-')))
        {
            throw new InvalidOperationException(code);
        }

        return value;
    }

    private static ImmutableHashSet<string> Identifiers(IEnumerable<string> values, string code)
    {
        ArgumentNullException.ThrowIfNull(values);
        var result = values.Select(value => Identifier(value, code))
            .ToImmutableHashSet(StringComparer.Ordinal);
        if (result.Count == 0)
        {
            throw new InvalidOperationException(code);
        }

        return result;
    }
}
