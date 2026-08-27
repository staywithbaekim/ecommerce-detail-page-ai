using Desktop.Domain.Tasks;

namespace Desktop.Application.Tasks;

public sealed class TaskCoordinator(ITaskEventStore store)
{
    public async Task<TaskState> LoadAsync(string taskId, CancellationToken cancellationToken = default)
    {
        var events = await store.LoadEventsAsync(taskId, cancellationToken).ConfigureAwait(false);
        return TaskStateReducer.Replay(taskId, events);
    }

    public async Task<(TaskState State, AppendEventOutcome Outcome)> RecordAsync(
        TaskEvent taskEvent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(taskEvent);
        var current = await LoadAsync(taskEvent.TaskId, cancellationToken).ConfigureAwait(false);
        var next = TaskStateReducer.Apply(current, taskEvent);
        var outcome = await store.AppendAsync(taskEvent, next, cancellationToken).ConfigureAwait(false);
        return (next, outcome);
    }

    public async Task<TaskProjection> GetProjectionAsync(
        string taskId,
        CancellationToken cancellationToken = default)
    {
        var state = await LoadAsync(taskId, cancellationToken).ConfigureAwait(false);
        return new TaskProjection(
            state.TaskId,
            state.Status.ToString(),
            state.LastSequence,
            state.OutputCount,
            state.IssueCount,
            state.Status is Desktop.Domain.Tasks.TaskStatus.Paused
                or Desktop.Domain.Tasks.TaskStatus.RecoveryRequired
                or Desktop.Domain.Tasks.TaskStatus.UserActionRequired);
    }
}
