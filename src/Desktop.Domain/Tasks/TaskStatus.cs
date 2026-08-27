namespace Desktop.Domain.Tasks;

public enum TaskStatus
{
    None = 0,
    Draft,
    Scanning,
    SystemBlocked,
    ReadyToRender,
    Rendering,
    Paused,
    RecoveryRequired,
    Failed,
    Cancelled,
    Completed,
    CompletedWithIssues,
    UserActionRequired
}
