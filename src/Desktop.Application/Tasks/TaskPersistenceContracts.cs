using Desktop.Domain.Tasks;

namespace Desktop.Application.Tasks;

public enum AppendEventOutcome
{
    Appended = 0,
    Duplicate
}

public interface ITaskEventStore
{
    Task<IReadOnlyList<TaskEvent>> LoadEventsAsync(string taskId, CancellationToken cancellationToken);

    Task<AppendEventOutcome> AppendAsync(
        TaskEvent taskEvent,
        TaskState resultingState,
        CancellationToken cancellationToken);
}

public enum OutputCommitState
{
    None = 0,
    Staged,
    Verified,
    Committed,
    RecoveryRequired
}

public sealed record TaskCheckpoint(
    string TaskId,
    string StyleId,
    string PageId,
    string Stage,
    int Attempt,
    string PlanDigest,
    string? BridgeExecutionId,
    OutputCommitState OutputState,
    DateTimeOffset UpdatedAt);

public interface ICheckpointStore
{
    Task SaveAsync(TaskCheckpoint checkpoint, CancellationToken cancellationToken);

    Task<IReadOnlyList<TaskCheckpoint>> LoadAsync(string taskId, CancellationToken cancellationToken);
}

public sealed record TaskProjection(
    string TaskId,
    string Status,
    long LastSequence,
    int OutputCount,
    int IssueCount,
    bool CanResume);
