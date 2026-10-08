namespace Desktop.Application.Ai;

/// <summary>
/// Owns the scheduler's monotonic budget snapshot and its synchronization.
/// Scheduling code can reserve/release request slots without owning token and
/// cost accounting state directly.
/// </summary>
internal sealed class AiAnalysisBudgetLedger(AiAnalysisBudgetPolicy policy)
{
    private readonly AiAnalysisBudgetPolicy _policy = policy ?? throw new ArgumentNullException(nameof(policy));
    private readonly object _gate = new();
    private AiAnalysisBudgetSnapshot _snapshot = AiAnalysisBudgetSnapshot.Empty;

    public object Gate => _gate;

    public AiAnalysisBudgetSnapshot Snapshot
    {
        get
        {
            lock (_gate) return _snapshot;
        }
    }

    public AiAnalysisBudgetSnapshot UnsafeSnapshot
    {
        get => _snapshot;
        set => _snapshot = value;
    }

    public AiAnalysisBudgetSnapshot Normalize(AiAnalysisBudgetSnapshot snapshot)
    {
        var hardStopped = snapshot.HardStopped
            || snapshot.RequestCount >= _policy.MaxRequests
            || snapshot.InputTokens >= _policy.MaxInputTokens
            || snapshot.OutputTokens >= _policy.MaxOutputTokens
            || snapshot.EstimatedCost >= _policy.MaxEstimatedCost;
        return snapshot with
        {
            WarningReached = snapshot.WarningReached || snapshot.EstimatedCost >= _policy.WarningThreshold,
            HardStopped = hardStopped
        };
    }

    public void Restore(AiAnalysisBudgetSnapshot snapshot)
    {
        lock (_gate) _snapshot = Normalize(snapshot);
    }
}
