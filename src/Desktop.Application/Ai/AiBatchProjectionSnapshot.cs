using System.Collections.Immutable;
using Desktop.Contracts.Ai;
using Desktop.Domain.Inputs;
using Desktop.Domain.Templates;

namespace Desktop.Application.Ai;

/// <summary>Runtime facts captured by the workflow before asynchronous projection reads.</summary>
public sealed record AiBatchProjectionSnapshot(
    BatchDraft Batch,
    TemplateRevision TemplateRevision,
    string TemplateGroupName,
    ImmutableArray<AiAnalysisRequestV1> Requests,
    AiAnalysisSchedulingControlState ControlState,
    string? ControlReason,
    bool BudgetHardStopped,
    string? SchedulerRestoreError,
    bool Started,
    string? AdmissionError,
    ImmutableDictionary<string, string> AdmissionFailures,
    ImmutableDictionary<string, string> TerminalErrors,
    ImmutableDictionary<string, string> RuntimeFailures)
{
    public ImmutableHashSet<string> RecoveredInFlightRequestIds { get; init; } = ImmutableHashSet<string>.Empty;
}
