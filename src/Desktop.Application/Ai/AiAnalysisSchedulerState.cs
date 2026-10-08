using System.Collections.Immutable;
using Desktop.Contracts.Ai;

namespace Desktop.Application.Ai;

/// <summary>
/// Mutable, provider-neutral scheduler state. Concurrency guards and durable
/// graph mirrors live here so the scheduler coordinates workflows instead of
/// owning every state container itself.
/// </summary>
internal sealed class AiAnalysisSchedulerState
{
    public object PendingGate { get; } = new();
    public object TimestampGate { get; } = new();
    public Dictionary<string, AiAnalysisWorkItem> PendingItems { get; } = new(StringComparer.Ordinal);
    public HashSet<string> AutomaticRefillSuppressedItems { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, AiAnalysisRequestV1> HistoricalRequests { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> TerminalErrors { get; } = new(StringComparer.Ordinal);
    public ImmutableHashSet<string> RecoveredInFlightRequestIds { get; set; } = ImmutableHashSet<string>.Empty;
    public AiAnalysisSchedulingControlState ControlState { get; set; } = AiAnalysisSchedulingControlState.Running;
    public string? ControlReason { get; set; }
    public DateTimeOffset LastPersistenceTimestamp { get; set; }
}
