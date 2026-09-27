using Desktop.Application.Ai;
using NSubstitute;

namespace Desktop.Application.Tests;

public sealed class AiAnalysisRecoveryServiceTests
{
    [Theory]
    [InlineData(AiAnalysisSchedulingControlState.Running)]
    [InlineData(AiAnalysisSchedulingControlState.PauseRequested)]
    [InlineData(AiAnalysisSchedulingControlState.Paused)]
    [InlineData(AiAnalysisSchedulingControlState.WaitingForNetwork)]
    [InlineData(AiAnalysisSchedulingControlState.CancelRequested)]
    [InlineData(AiAnalysisSchedulingControlState.Cancelled)]
    public async Task SchedulerRestoresPersistedControlOnceWithoutCallingProvider(
        AiAnalysisSchedulingControlState controlState)
    {
        using var fixture = new Fixture();
        fixture.State.LoadSchedulingStateAsync(fixture.Scheduler.StateKey, Arg.Any<CancellationToken>())
            .Returns(new AiAnalysisSchedulingState([], AiAnalysisBudgetSnapshot.Empty,
                DateTimeOffset.UtcNow, controlState, "persisted-reason"));

        await fixture.Recovery.EnsureSchedulerRestoredAsync(CancellationToken.None);
        await fixture.Recovery.EnsureSchedulerRestoredAsync(CancellationToken.None);

        Assert.Equal(controlState, fixture.Scheduler.ControlState);
        Assert.Equal("persisted-reason", fixture.Scheduler.ControlReason);
        Assert.Null(fixture.Recovery.RestoreError);
        fixture.Recovery.ThrowIfAnalysisStateUnavailable();
        Assert.Equal(1, fixture.CallCount(checkpoint: false));
        Assert.Empty(fixture.Provider.ReceivedCalls());
        Assert.Empty(fixture.Checkpoints.ReceivedCalls());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConcurrentCallersShareOneAttemptAndCancelledWaiterDoesNotCancelOwner(bool checkpoint)
    {
        using var fixture = new Fixture();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.SetOperation(checkpoint, token => release.Task.WaitAsync(token));
        using var cancellation = new CancellationTokenSource();
        var owner = fixture.RecoverAsync(checkpoint);
        var cancelledWaiter = fixture.RecoverAsync(checkpoint, cancellation.Token);
        var otherWaiter = fixture.RecoverAsync(checkpoint);
        try
        {
            Assert.False(owner.IsCompleted);
            Assert.False(otherWaiter.IsCompleted);
            cancellation.Cancel();
            var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelledWaiter);
            Assert.Equal(cancellation.Token, error.CancellationToken);
            Assert.False(owner.IsCompleted);
            Assert.Equal(1, fixture.CallCount(checkpoint));
        }
        finally
        {
            release.TrySetResult();
            await Task.WhenAll(owner, otherWaiter).WaitAsync(TimeSpan.FromSeconds(5));
        }

        await fixture.RecoverAsync(checkpoint);
        Assert.Equal(1, fixture.CallCount(checkpoint));
        Assert.Null(fixture.Recovery.RestoreError);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationBeforeEntryLeavesRecoveryAvailable(bool checkpoint)
    {
        using var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => fixture.RecoverAsync(checkpoint, cancellation.Token));

        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.Equal(0, fixture.CallCount(checkpoint));
        await fixture.RecoverAsync(checkpoint);
        Assert.Equal(1, fixture.CallCount(checkpoint));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationDuringIoPropagatesWithoutRepeatingEnteredAttempt(bool checkpoint)
    {
        using var fixture = new Fixture();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.SetOperation(checkpoint, token => release.Task.WaitAsync(token));
        using var cancellation = new CancellationTokenSource();
        var operation = fixture.RecoverAsync(checkpoint, cancellation.Token);
        cancellation.Cancel();

        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);

        Assert.Equal(cancellation.Token, error.CancellationToken);
        await fixture.RecoverAsync(checkpoint).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, fixture.CallCount(checkpoint));
        Assert.Null(fixture.Recovery.RestoreError);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvalidOperationBecomesStickyRestoreErrorWithoutRetry(bool checkpoint)
    {
        using var fixture = new Fixture();
        fixture.SetOperation(checkpoint,
            _ => Task.FromException(new InvalidOperationException("AI_RECOVERY_TEST_FAILURE")));

        await fixture.RecoverAsync(checkpoint);
        await fixture.RecoverAsync(checkpoint);

        Assert.Equal(1, fixture.CallCount(checkpoint));
        Assert.Equal("AI_RECOVERY_TEST_FAILURE", fixture.Recovery.RestoreError);
        var error = Assert.Throws<InvalidOperationException>(fixture.Recovery.ThrowIfAnalysisStateUnavailable);
        Assert.Equal("AI_RECOVERY_TEST_FAILURE", error.Message);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task StorageExceptionsPropagateWithoutInventingAnErrorOrRetry(
        bool checkpoint, bool corruptData)
    {
        using var fixture = new Fixture();
        Exception expected = corruptData
            ? new InvalidDataException("corrupt")
            : new IOException("unavailable");
        fixture.SetOperation(checkpoint, _ => Task.FromException(expected));

        var error = await Record.ExceptionAsync(() => fixture.RecoverAsync(checkpoint));

        Assert.Same(expected, error);
        Assert.Null(fixture.Recovery.RestoreError);
        await fixture.RecoverAsync(checkpoint).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, fixture.CallCount(checkpoint));
    }

    [Theory]
    [InlineData(AiAnalysisCheckpointOutcome.NothingToRecover)]
    [InlineData(AiAnalysisCheckpointOutcome.Committed)]
    [InlineData(AiAnalysisCheckpointOutcome.RolledBackOrphan)]
    public async Task SuccessfulCheckpointRecoveryDoesNotClearSchedulerFailure(
        AiAnalysisCheckpointOutcome outcome)
    {
        using var fixture = new Fixture();
        fixture.SetOperation(checkpoint: false,
            _ => Task.FromException(new InvalidOperationException("AI_SCHEDULER_TEST_FAILURE")));
        fixture.Checkpoints.RecoverAsync(Fixture.BatchId, Arg.Any<CancellationToken>())
            .Returns(new AiAnalysisCheckpointResult(null, Fixture.BatchId, outcome, "resolved"));

        await fixture.Recovery.EnsureSchedulerRestoredAsync(CancellationToken.None);
        await fixture.Recovery.EnsureAnalysisStateRecoveredAsync(CancellationToken.None);

        Assert.Equal("AI_SCHEDULER_TEST_FAILURE", fixture.Recovery.RestoreError);
        Assert.Throws<InvalidOperationException>(fixture.Recovery.ThrowIfAnalysisStateUnavailable);
    }

    [Fact]
    public async Task CheckpointRecoveryRequiredReplacesEarlierSchedulerErrorAndUsesExactBatch()
    {
        using var fixture = new Fixture();
        fixture.SetOperation(checkpoint: false,
            _ => Task.FromException(new InvalidOperationException("AI_SCHEDULER_TEST_FAILURE")));
        fixture.Checkpoints.RecoverAsync(Fixture.BatchId, Arg.Any<CancellationToken>())
            .Returns(new AiAnalysisCheckpointResult(null, Fixture.BatchId,
                AiAnalysisCheckpointOutcome.RecoveryRequired, "AI_CHECKPOINT_TEST_FAILURE"));

        await fixture.Recovery.EnsureSchedulerRestoredAsync(CancellationToken.None);
        await fixture.Recovery.EnsureAnalysisStateRecoveredAsync(CancellationToken.None);
        await fixture.Recovery.EnsureAnalysisStateRecoveredAsync(CancellationToken.None);

        Assert.Equal("AI_CHECKPOINT_TEST_FAILURE", fixture.Recovery.RestoreError);
        await fixture.Checkpoints.Received(1).RecoverAsync(Fixture.BatchId, CancellationToken.None);
        Assert.Equal("AI_CHECKPOINT_TEST_FAILURE",
            Assert.Throws<InvalidOperationException>(fixture.Recovery.ThrowIfAnalysisStateUnavailable).Message);
    }

    [Fact]
    public async Task OptionalCheckpointRemainsNoOpEvenWithCancelledToken()
    {
        using var fixture = new Fixture();
        using var recovery = new AiAnalysisRecoveryService(Fixture.BatchId, fixture.Scheduler);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await recovery.EnsureAnalysisStateRecoveredAsync(cancellation.Token);

        Assert.Null(recovery.RestoreError);
        Assert.Empty(fixture.State.ReceivedCalls());
        Assert.Empty(fixture.Checkpoints.ReceivedCalls());
    }

    [Fact]
    public async Task RecoveryPhasesRetainIndependentGates()
    {
        using var fixture = new Fixture();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.SetOperation(checkpoint: false, token => release.Task.WaitAsync(token));
        var schedulerRecovery = fixture.Recovery.EnsureSchedulerRestoredAsync(CancellationToken.None);
        try
        {
            await fixture.Recovery.EnsureAnalysisStateRecoveredAsync(CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(schedulerRecovery.IsCompleted);
            Assert.Equal(1, fixture.CallCount(checkpoint: true));
        }
        finally
        {
            release.TrySetResult();
            await schedulerRecovery.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task RecoveryOwnershipIsPerContextAndDisposalDoesNotDisposeBorrowedScheduler()
    {
        using var fixture = new Fixture();
        using (var firstContext = new AiAnalysisRecoveryService(Fixture.BatchId, fixture.Scheduler))
        {
            await firstContext.EnsureSchedulerRestoredAsync(CancellationToken.None);
        }
        using (var nextContext = new AiAnalysisRecoveryService(Fixture.BatchId, fixture.Scheduler))
        {
            await nextContext.EnsureSchedulerRestoredAsync(CancellationToken.None);
        }

        Assert.Equal(2, fixture.CallCount(checkpoint: false));
        await fixture.Scheduler.RequestPauseAsync("still-owned-by-caller", CancellationToken.None);
        Assert.Equal(AiAnalysisSchedulingControlState.PauseRequested, fixture.Scheduler.ControlState);
        Assert.Empty(fixture.Provider.ReceivedCalls());
    }

    private sealed class Fixture : IDisposable
    {
        public const string BatchId = "batch_recovery";
        public IAiAnalysisSchedulingStateStore State { get; } = Substitute.For<IAiAnalysisSchedulingStateStore>();
        public IAiAnalysisCheckpointPort Checkpoints { get; } = Substitute.For<IAiAnalysisCheckpointPort>();
        public IAiServicePort Provider { get; } = Substitute.For<IAiServicePort>();
        public AiAnalysisScheduler Scheduler { get; }
        public AiAnalysisRecoveryService Recovery { get; }

        public Fixture()
        {
            Scheduler = new AiAnalysisScheduler(Provider, Substitute.For<IAiAnalysisStore>(),
                Substitute.For<IAiAnalysisWorkBuffer>(), AiAnalysisProductionPolicy.BudgetV1,
                stateStore: State, stateKey: BatchId);
            Recovery = new AiAnalysisRecoveryService(BatchId, Scheduler, Checkpoints);
            SetOperation(checkpoint: false, _ => Task.CompletedTask);
            SetOperation(checkpoint: true, _ => Task.CompletedTask);
        }

        public Task RecoverAsync(bool checkpoint, CancellationToken cancellationToken = default) =>
            checkpoint
                ? Recovery.EnsureAnalysisStateRecoveredAsync(cancellationToken)
                : Recovery.EnsureSchedulerRestoredAsync(cancellationToken);

        public void SetOperation(bool checkpoint, Func<CancellationToken, Task> operation)
        {
            if (checkpoint)
            {
                Checkpoints.RecoverAsync(BatchId, Arg.Any<CancellationToken>()).Returns(async call =>
                {
                    await operation(call.Arg<CancellationToken>());
                    return new AiAnalysisCheckpointResult(null, BatchId,
                        AiAnalysisCheckpointOutcome.NothingToRecover, "none");
                });
            }
            else
            {
                State.LoadSchedulingStateAsync(Scheduler.StateKey, Arg.Any<CancellationToken>()).Returns(async call =>
                {
                    await operation(call.Arg<CancellationToken>());
                    return (AiAnalysisSchedulingState?)null;
                });
            }
        }

        public int CallCount(bool checkpoint) => checkpoint
            ? Checkpoints.ReceivedCalls().Count(call => call.GetMethodInfo().Name == nameof(Checkpoints.RecoverAsync))
            : State.ReceivedCalls().Count(call => call.GetMethodInfo().Name == nameof(State.LoadSchedulingStateAsync));

        public void Dispose()
        {
            Recovery.Dispose();
            Scheduler.Dispose();
        }
    }
}
