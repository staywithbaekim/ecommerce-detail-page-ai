using Desktop.Application.Events;
using Desktop.Application.Tasks;
using Desktop.Domain.Tasks;

namespace Desktop.Application.Tests;

public sealed class ApplicationFoundationTests
{
    [Fact]
    public async Task CoordinatorBuildsProjectionFromPersistedEvents()
    {
        var store = new MemoryTaskStore();
        var coordinator = new TaskCoordinator(store);
        await coordinator.RecordAsync(Event(1, TaskEventType.TaskCreated));
        await coordinator.RecordAsync(Event(2, TaskEventType.ScanningStarted));
        await coordinator.RecordAsync(Event(3, TaskEventType.PauseRequested));

        var projection = await coordinator.GetProjectionAsync("task-01");

        Assert.Equal("Paused", projection.Status);
        Assert.True(projection.CanResume);
        Assert.Equal(3, projection.LastSequence);
    }

    [Fact]
    public async Task CoordinatorPreservesDuplicateEventId()
    {
        var store = new MemoryTaskStore();
        var coordinator = new TaskCoordinator(store);
        var taskEvent = Event(1, TaskEventType.TaskCreated);
        var first = await coordinator.RecordAsync(taskEvent);
        var duplicate = await coordinator.RecordAsync(taskEvent);

        Assert.Equal(AppendEventOutcome.Appended, first.Outcome);
        Assert.Equal(AppendEventOutcome.Duplicate, duplicate.Outcome);
        Assert.Single(store.Events);
    }

    [Fact]
    public void EventBusPublishesAndUnsubscribes()
    {
        var eventBus = new ApplicationEventBus();
        var calls = 0;
        var subscription = eventBus.Subscribe<TestEvent>(_ => calls++);

        eventBus.Publish(new TestEvent());
        subscription.Dispose();
        eventBus.Publish(new TestEvent());

        Assert.Equal(1, calls);
    }

    private static TaskEvent Event(long sequence, TaskEventType type) =>
        new(Guid.NewGuid(), "task-01", sequence, type, DateTimeOffset.UtcNow);

    private sealed record TestEvent : IApplicationEvent;

    private sealed class MemoryTaskStore : ITaskEventStore
    {
        private readonly List<TaskEvent> _events = [];

        public IReadOnlyList<TaskEvent> Events => _events;

        public Task<IReadOnlyList<TaskEvent>> LoadEventsAsync(
            string taskId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<TaskEvent>>(
                _events.Where(item => item.TaskId == taskId).OrderBy(item => item.Sequence).ToArray());
        }

        public Task<AppendEventOutcome> AppendAsync(
            TaskEvent taskEvent,
            TaskState resultingState,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_events.Any(item => item.EventId == taskEvent.EventId))
            {
                return Task.FromResult(AppendEventOutcome.Duplicate);
            }

            _events.Add(taskEvent);
            return Task.FromResult(AppendEventOutcome.Appended);
        }
    }
}
