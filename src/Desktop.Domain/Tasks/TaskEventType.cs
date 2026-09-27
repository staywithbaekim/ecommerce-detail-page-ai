namespace Desktop.Domain.Tasks;

public enum TaskEventType
{
    TaskCreated = 0,
    ScanningStarted,
    SystemBlocked,
    ReadyToRender,
    RenderingStarted,
    PauseRequested,
    RecoveryRequired,
    Failed,
    Cancelled,
    UserActionRequired,
    PageCommitted,
    IssueRecorded,
    Completed,
    CompletedWithIssues,
    RenderPageRegenerationRequested,
    RenderPageRepairRequested
}
