namespace Desktop.Contracts.Ai;

public sealed record AiAnalysisRequest(
    string RequestId,
    string ProductId,
    IReadOnlyList<string> LogicalAssetIds,
    IReadOnlyList<string> RequiredSemantics);

public sealed record AiCandidate(
    string LogicalAssetId,
    string SemanticLabel,
    decimal Confidence);

public sealed record AiAnalysisResult(
    string RequestId,
    IReadOnlyList<AiCandidate> Candidates,
    string UsageEventId);

public sealed record AiUsageEvent(
    string UsageEventId,
    string RequestId,
    int InputUnits,
    int OutputUnits,
    DateTimeOffset RecordedAt);
