using Desktop.Domain.Tasks;

namespace Desktop.Domain.Tests;

public sealed class TaskStateReducerTests
{
    [Fact]
    public void ZeroOutputCannotBecomeCompleted()
    {
        var state = Replay(TaskEventType.TaskCreated, TaskEventType.ReadyToRender);
        var completion = Event(state.TaskId, state.LastSequence + 1, TaskEventType.Completed);

        var error = Assert.Throws<TaskRuleViolationException>(() => TaskStateReducer.Apply(state, completion));

        Assert.Equal("TASK_ZERO_OUTPUT_CANNOT_COMPLETE", error.Code);
    }

    [Fact]
    public void CommittedPageAllowsOrdinaryCompletion()
    {
        var state = Replay(
            TaskEventType.TaskCreated,
            TaskEventType.ReadyToRender,
            TaskEventType.RenderingStarted);
        state = TaskStateReducer.Apply(
            state,
            Event(state.TaskId, 4, TaskEventType.PageCommitted, "page-01"));
        state = TaskStateReducer.Apply(
            state,
            Event(state.TaskId, 5, TaskEventType.Completed));

        Assert.Equal(Desktop.Domain.Tasks.TaskStatus.Completed, state.Status);
        Assert.Equal(1, state.OutputCount);
    }

    [Fact]
    public void CompletedWithIssuesRequiresOutputAndIssue()
    {
        var state = Replay(
            TaskEventType.TaskCreated,
            TaskEventType.ReadyToRender,
            TaskEventType.RenderingStarted);
        state = TaskStateReducer.Apply(state, Event(state.TaskId, 4, TaskEventType.IssueRecorded));
        state = TaskStateReducer.Apply(state, Event(state.TaskId, 5, TaskEventType.PageCommitted, "page-01"));
        state = TaskStateReducer.Apply(state, Event(state.TaskId, 6, TaskEventType.CompletedWithIssues));

        Assert.Equal(Desktop.Domain.Tasks.TaskStatus.CompletedWithIssues, state.Status);
        Assert.Equal(1, state.IssueCount);
    }

    [Theory]
    [MemberData(nameof(ReplayCases))]
    public void ReplaysRequiredStatus(TaskEventType[] events, Desktop.Domain.Tasks.TaskStatus expected)
    {
        var state = Replay(events);

        Assert.Equal(expected, state.Status);
    }

    [Fact]
    public void DuplicateEventIdIsIdempotent()
    {
        var created = Event("task-01", 1, TaskEventType.TaskCreated);
        var state = TaskStateReducer.Apply(TaskState.Empty("task-01"), created);

        var duplicate = TaskStateReducer.Apply(state, created);

        Assert.Same(state, duplicate);
    }

    [Theory]
    [InlineData(1, "TASK_EVENT_SEQUENCE_REGRESSION")]
    [InlineData(3, "TASK_EVENT_SEQUENCE_GAP")]
    public void RejectsInvalidSequence(long sequence, string expectedCode)
    {
        var state = Replay(TaskEventType.TaskCreated);

        var error = Assert.Throws<TaskRuleViolationException>(() => TaskStateReducer.Apply(
            state,
            Event(state.TaskId, sequence, TaskEventType.ScanningStarted)));

        Assert.Equal(expectedCode, error.Code);
    }

    public static TheoryData<TaskEventType[], Desktop.Domain.Tasks.TaskStatus> ReplayCases => new()
    {
        { [TaskEventType.TaskCreated], Desktop.Domain.Tasks.TaskStatus.Draft },
        { [TaskEventType.TaskCreated, TaskEventType.ScanningStarted], Desktop.Domain.Tasks.TaskStatus.Scanning },
        { [TaskEventType.TaskCreated, TaskEventType.SystemBlocked], Desktop.Domain.Tasks.TaskStatus.SystemBlocked },
        { [TaskEventType.TaskCreated, TaskEventType.ReadyToRender], Desktop.Domain.Tasks.TaskStatus.ReadyToRender },
        { [TaskEventType.TaskCreated, TaskEventType.ReadyToRender, TaskEventType.RenderingStarted], Desktop.Domain.Tasks.TaskStatus.Rendering },
        { [TaskEventType.TaskCreated, TaskEventType.ScanningStarted, TaskEventType.PauseRequested], Desktop.Domain.Tasks.TaskStatus.Paused },
        { [TaskEventType.TaskCreated, TaskEventType.RecoveryRequired], Desktop.Domain.Tasks.TaskStatus.RecoveryRequired },
        { [TaskEventType.TaskCreated, TaskEventType.Failed], Desktop.Domain.Tasks.TaskStatus.Failed },
        { [TaskEventType.TaskCreated, TaskEventType.Cancelled], Desktop.Domain.Tasks.TaskStatus.Cancelled },
        { [TaskEventType.TaskCreated, TaskEventType.UserActionRequired], Desktop.Domain.Tasks.TaskStatus.UserActionRequired }
    };

    private static TaskState Replay(params TaskEventType[] eventTypes)
    {
        const string taskId = "task-01";
        var events = eventTypes.Select((type, index) => Event(taskId, index + 1, type));
        return TaskStateReducer.Replay(taskId, events);
    }

    private static TaskEvent Event(
        string taskId,
        long sequence,
        TaskEventType type,
        string? pageId = null) =>
        new(Guid.NewGuid(), taskId, sequence, type, DateTimeOffset.UtcNow, pageId);
}
