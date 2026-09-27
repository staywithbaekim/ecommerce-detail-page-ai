using System.Net.NetworkInformation;
using System.Security.Cryptography;
using Desktop.Application.Rendering;
using Desktop.Contracts.Bridge;
using Desktop.Infrastructure.Bridge;
using Desktop.Infrastructure.Files;

namespace Desktop.Infrastructure.Tests;

// Same xUnit class/collection as every existing fixed-port listener test.
public sealed partial class BridgeLoopbackHostTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(31)]
    public async Task ConnectionCheckWithoutValidPairingDoesNotListenOrCreateTaskFiles(int length)
    {
        using var fixture = new BridgeResourceLeaseTests.Fixture();
        var store = new PairingMemoryStore { Value = length == 0 ? null : RandomNumberGenerator.GetBytes(length) };
        await using var factory = new BridgeSessionOwnerFactory(store, new LocalAppPathPolicy(fixture.Root), TimeProvider.System);
        Assert.Null(factory.Latest);
        Assert.Equal(0, store.Reads);
        var result = await factory.CheckAsync(default);
        Assert.Equal(PhotoshopConnectionCheckStatus.PairingRequired, result.Status);
        Assert.Same(result, factory.Latest);
        Assert.Null(result.MatchedPhotoshopBuild);
        AssertCheckLeftNoWork(store, fixture.Root);
        await using var owner = CreateOwner(new byte[32]);
        await owner.StartAsync(default);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ConnectionCheckAuthenticatesThreeHeartbeatsWithoutSavingAndReleasesBeforePublishing(bool ready)
    {
        using var fixture = new BridgeResourceLeaseTests.Fixture();
        var original = RandomNumberGenerator.GetBytes(32);
        var store = new PairingMemoryStore { Value = original.ToArray() };
        await using var factory = new BridgeSessionOwnerFactory(store, new LocalAppPathPolicy(fixture.Root), TimeProvider.System);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var check = factory.CheckAsync(stop.Token);
        await WaitForConnectionCheckListener(stop.Token);
        await AuthenticateDevice(original.ToArray(), () => Assert.False(check.IsCompleted), stop.Token,
            hostState: ready ? "ready" : "busy", modalState: ready ? "idle" : "running");
        var result = await check;
        Assert.Equal(ready ? PhotoshopConnectionCheckStatus.Ready : PhotoshopConnectionCheckStatus.HostNotReady, result.Status);
        Assert.Equal(3, result.AuthenticatedHeartbeats);
        Assert.Equal(BridgeSessionOwnerFactory.SupportedPhotoshopBuild, result.MatchedPhotoshopBuild);
        Assert.Same(result, factory.Latest);
        Assert.Equal(original, store.Value);
        AssertCheckLeftNoWork(store, fixture.Root);
        // A completed historical result cannot still own the rendezvous port.
        await using var next = CreateOwner(new byte[32]);
        await next.StartAsync(default);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public async Task ConnectionCheckCannotAcceptHandshakeOrFewerThanThreeHeartbeats(int count)
    {
        using var fixture = new BridgeResourceLeaseTests.Fixture();
        var store = new PairingMemoryStore { Value = RandomNumberGenerator.GetBytes(32) };
        var clock = new ConnectionCheckClock();
        await using var factory = new BridgeSessionOwnerFactory(store, new LocalAppPathPolicy(fixture.Root), clock);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var check = factory.CheckAsync(stop.Token);
        await WaitForConnectionCheckListener(stop.Token);
        await AuthenticateDevice(store.Value.ToArray(), () => Assert.False(check.IsCompleted), stop.Token, heartbeatCount: count);
        Assert.False(check.IsCompleted);
        clock.Advance(BridgeSessionOwnerFactory.ConnectionCheckWaitLimit + TimeSpan.FromSeconds(1));
        var result = await check;
        Assert.Equal(PhotoshopConnectionCheckStatus.TimedOut, result.Status);
        Assert.InRange(result.AuthenticatedHeartbeats, 0, count);
        Assert.Null(result.MatchedPhotoshopBuild);
        AssertCheckLeftNoWork(store, fixture.Root);
        await using var next = CreateOwner(new byte[32]);
        await next.StartAsync(default);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ConnectionCheckRejectsWrongBuildOrWrongSecretWithoutClaimingAuthenticatedMatch(bool wrongBuild)
    {
        using var fixture = new BridgeResourceLeaseTests.Fixture();
        var store = new PairingMemoryStore { Value = RandomNumberGenerator.GetBytes(32) };
        var clock = new ConnectionCheckClock();
        await using var factory = new BridgeSessionOwnerFactory(store, new LocalAppPathPolicy(fixture.Root), clock);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var check = factory.CheckAsync(stop.Token);
        await WaitForConnectionCheckListener(stop.Token);
        await AuthenticateDevice(store.Value.ToArray(), () => { }, stop.Token,
            photoshopBuild: wrongBuild ? "26.8.1 (unapproved)" : null,
            wrongHandshakeSecret: wrongBuild ? null : new byte[32]);
        Assert.False(check.IsCompleted);
        clock.Advance(BridgeSessionOwnerFactory.ConnectionCheckWaitLimit + TimeSpan.FromSeconds(1));
        var result = await check;
        Assert.Equal(PhotoshopConnectionCheckStatus.TimedOut, result.Status);
        Assert.Equal(0, result.AuthenticatedHeartbeats);
        Assert.Null(result.MatchedPhotoshopBuild);
        AssertCheckLeftNoWork(store, fixture.Root);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConnectionCheckAndPairingShareBusyGateAndCancelOrDisposeReleasesIt(bool dispose)
    {
        using var fixture = new BridgeResourceLeaseTests.Fixture();
        var store = new PairingMemoryStore { Value = RandomNumberGenerator.GetBytes(32) };
        await using var factory = new BridgeSessionOwnerFactory(store, new LocalAppPathPolicy(fixture.Root), TimeProvider.System);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var check = factory.CheckAsync(stop.Token);
        await WaitForConnectionCheckListener(stop.Token);
        var busy = await factory.CheckAsync(stop.Token);
        Assert.Equal(PhotoshopConnectionCheckStatus.Busy, busy.Status);
        Assert.Equal(1, store.Reads);
        var displayed = false;
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => factory.PairAsync((_, _) =>
        { displayed = true; return Task.CompletedTask; }, stop.Token));
        Assert.Equal("BRIDGE_BUSY", error.Message);
        Assert.False(displayed);
        if (dispose) await factory.DisposeAsync(); else stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => check);
        Assert.Same(busy, factory.Latest);
        AssertCheckLeftNoWork(store, fixture.Root);
        await using var next = CreateOwner(new byte[32]);
        await next.StartAsync(default);
    }

    [Fact]
    public async Task ConnectionCheckDuringPairingIsBusyAndNeverReadsOrSavesExistingSecret()
    {
        using var fixture = new BridgeResourceLeaseTests.Fixture();
        var store = new PairingMemoryStore { Value = RandomNumberGenerator.GetBytes(32) };
        await using var factory = new BridgeSessionOwnerFactory(store, new LocalAppPathPolicy(fixture.Root), TimeProvider.System);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => factory.PairAsync(async (bytes, token) =>
        {
            if (bytes.IsEmpty) return;
            var result = await factory.CheckAsync(token);
            Assert.Equal(PhotoshopConnectionCheckStatus.Busy, result.Status);
            Assert.Equal(0, store.Reads);
            stop.Cancel();
        }, stop.Token));
        Assert.Equal(0, store.Saves);
    }

    [Fact]
    public async Task ConnectionCheckOccupiedPortDoesNotDisruptExistingOwner()
    {
        using var fixture = new BridgeResourceLeaseTests.Fixture();
        var store = new PairingMemoryStore { Value = RandomNumberGenerator.GetBytes(32) };
        await using var owner = CreateOwner(new byte[32]);
        await owner.StartAsync(default);
        await using var factory = new BridgeSessionOwnerFactory(store, new LocalAppPathPolicy(fixture.Root), TimeProvider.System);
        var result = await factory.CheckAsync(default);
        Assert.Equal(PhotoshopConnectionCheckStatus.Busy, result.Status);
        Assert.Null(result.MatchedPhotoshopBuild);
        Assert.True((await owner.GetStatusAsync(default)).Listening);
        AssertCheckLeftNoWork(store, fixture.Root);
    }

    [Fact]
    public async Task ConnectionCheckStorageFailureIsNotMissingPairingAndPreCancellationDoesNotRead()
    {
        using var fixture = new BridgeResourceLeaseTests.Fixture();
        var store = new PairingMemoryStore { FailRead = true };
        await using var factory = new BridgeSessionOwnerFactory(store, new LocalAppPathPolicy(fixture.Root), TimeProvider.System);
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => factory.CheckAsync(stop.Token));
        Assert.Equal(0, store.Reads);
        Assert.Null(factory.Latest);
        var result = await factory.CheckAsync(default);
        Assert.Equal(PhotoshopConnectionCheckStatus.Failed, result.Status);
        AssertCheckLeftNoWork(store, fixture.Root);
    }

    [Fact]
    public async Task TaskOwnerStartPreemptsConnectionCheckAndKeepsAuthenticatedOwnerAvailable()
    {
        using var fixture = new BridgeResourceLeaseTests.Fixture();
        var secret = RandomNumberGenerator.GetBytes(32);
        var store = new PairingMemoryStore { Value = secret.ToArray() };
        await using var factory = new BridgeSessionOwnerFactory(store, new LocalAppPathPolicy(fixture.Root), TimeProvider.System);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var check = factory.CheckAsync(stop.Token);
        await WaitForConnectionCheckListener(stop.Token);
        Assert.Null(factory.Latest);

        var production = factory.StartTaskOwnerAsync("task_connection_check_preemption", stop.Token);
        var displayed = false;
        var competingPair = factory.PairAsync((_, _) =>
        {
            displayed = true;
            return Task.CompletedTask;
        }, stop.Token);
        var competingCheck = factory.CheckAsync(stop.Token);

        var pairingError = await Assert.ThrowsAsync<InvalidOperationException>(() => competingPair);
        Assert.Equal("BRIDGE_BUSY", pairingError.Message);
        Assert.False(displayed);
        var busy = await competingCheck;
        Assert.Equal(PhotoshopConnectionCheckStatus.Busy, busy.Status);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => check);
        Assert.Same(busy, factory.Latest); // The preempted check did not publish a late result.

        await using var owner = await production;
        var listening = await owner.GetStatusAsync(stop.Token);
        Assert.True(listening.Listening);
        Assert.False(listening.Paired);
        Assert.Empty(await owner.ListExecutionsAsync(stop.Token));

        await AuthenticateDevice(secret.ToArray(), () => { }, stop.Token);
        var authenticated = await owner.GetStatusAsync(stop.Token);
        Assert.True(authenticated.Listening);
        Assert.True(authenticated.Paired);
        Assert.True(authenticated.Connected);
        Assert.True(authenticated.Ready);
        Assert.Equal(3, authenticated.Heartbeats);
        Assert.Equal(0, store.Saves);
    }

    [Fact]
    public async Task TaskOwnerStartCancellationReleasesArbitrationForTheNextProductionStart()
    {
        using var fixture = new BridgeResourceLeaseTests.Fixture();
        var memoryStore = new PairingMemoryStore { Value = RandomNumberGenerator.GetBytes(32) };
        var store = new SecondReadLatchPairingStore(memoryStore);
        await using var factory = new BridgeSessionOwnerFactory(store, new LocalAppPathPolicy(fixture.Root), TimeProvider.System);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var check = factory.CheckAsync(stop.Token);
        await WaitForConnectionCheckListener(stop.Token);
        using var cancelStart = new CancellationTokenSource();

        var cancelledProduction = Task.Run(() =>
            factory.StartTaskOwnerAsync("task_cancelled_start", cancelStart.Token));
        try
        {
            await store.SecondReadStarted.WaitAsync(stop.Token);
            cancelStart.Cancel();
        }
        finally { store.ReleaseSecondRead(); }

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelledProduction);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => check);
        Assert.Null(factory.Latest);
        await using var next = await factory.StartTaskOwnerAsync("task_after_cancelled_start", stop.Token);
        Assert.True((await next.GetStatusAsync(stop.Token)).Listening);
    }

    [Fact]
    public async Task TaskOwnerStartFailureReleasesArbitrationWithoutDisruptingExistingOwner()
    {
        using var fixture = new BridgeResourceLeaseTests.Fixture();
        var store = new PairingMemoryStore { Value = RandomNumberGenerator.GetBytes(32) };
        await using var factory = new BridgeSessionOwnerFactory(store, new LocalAppPathPolicy(fixture.Root), TimeProvider.System);
        await using var occupied = factory.CreateTaskOwner("task_existing_owner");
        await occupied.StartAsync(default);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var error = await Assert.ThrowsAsync<IOException>(() =>
            factory.StartTaskOwnerAsync("task_start_failure", stop.Token));

        Assert.Equal("BRIDGE_PORT_UNAVAILABLE", error.Message);
        Assert.True((await occupied.GetStatusAsync(stop.Token)).Listening);
        await AuthenticateDevice(store.Value!.ToArray(), () => { }, stop.Token);
        var occupiedStatus = await occupied.GetStatusAsync(stop.Token);
        Assert.True(occupiedStatus.Connected);
        Assert.True(occupiedStatus.Ready);
        Assert.Equal(3, occupiedStatus.Heartbeats);

        await occupied.DisposeAsync();
        await using var next = await factory.StartTaskOwnerAsync("task_after_start_failure", stop.Token);
        Assert.True((await next.GetStatusAsync(stop.Token)).Listening);
    }

    [Fact]
    public async Task TaskOwnerStartDuringPairingReturnsBusyWithoutCancellingPairing()
    {
        using var fixture = new BridgeResourceLeaseTests.Fixture();
        var original = RandomNumberGenerator.GetBytes(32);
        var store = new PairingMemoryStore { Value = original.ToArray() };
        await using var factory = new BridgeSessionOwnerFactory(store, new LocalAppPathPolicy(fixture.Root), TimeProvider.System);
        using var pairingStop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var shown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pairing = factory.PairAsync(async (bytes, token) =>
        {
            if (bytes.IsEmpty) return;
            shown.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        }, pairingStop.Token);
        await shown.Task.WaitAsync(TimeSpan.FromSeconds(3));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            factory.StartTaskOwnerAsync("task_during_pairing", CancellationToken.None));

        Assert.Equal("BRIDGE_BUSY", error.Message);
        Assert.False(pairing.IsCompleted);
        Assert.Equal(0, store.Reads);
        Assert.Equal(0, store.Saves);
        pairingStop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pairing);
        Assert.Equal(original, store.Value);

        using var nextStop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var next = await factory.StartTaskOwnerAsync("task_after_pairing_cancel", nextStop.Token);
        Assert.True((await next.GetStatusAsync(nextStop.Token)).Listening);
    }

    [Fact]
    public async Task TaskOwnerStartValidatesPairingTaskAndDisposalWithoutLeakingGate()
    {
        using var fixture = new BridgeResourceLeaseTests.Fixture();
        var store = new PairingMemoryStore();
        await using var factory = new BridgeSessionOwnerFactory(store, new LocalAppPathPolicy(fixture.Root), TimeProvider.System);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var pairingError = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            factory.StartTaskOwnerAsync("task_without_pairing", stop.Token));
        Assert.Equal("BRIDGE_PAIRING_REQUIRED", pairingError.Message);
        Assert.Null(factory.Latest);
        Assert.Equal(1, store.Reads);

        store.Value = RandomNumberGenerator.GetBytes(32);
        await Assert.ThrowsAsync<ArgumentException>(() =>
            factory.StartTaskOwnerAsync("bad\u0001task", stop.Token));
        Assert.Equal(1, store.Reads); // Invalid task IDs are rejected before protected storage is read.

        await using (var owner = await factory.StartTaskOwnerAsync("task_after_validation_failures", stop.Token))
            Assert.True((await owner.GetStatusAsync(stop.Token)).Listening);

        await factory.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            factory.StartTaskOwnerAsync("task_after_factory_dispose", CancellationToken.None));
    }

    private static void AssertCheckLeftNoWork(PairingMemoryStore store, string root)
    {
        Assert.Equal(0, store.Saves);
        if (store.LastRead is not null) Assert.All(store.LastRead, value => Assert.Equal(0, value));
        Assert.False(Directory.Exists(Path.Combine(root, "render")));
    }

    private static async Task WaitForConnectionCheckListener(CancellationToken token)
    {
        // Observe the test-owned listener, without sending a request to an unknown service.
        while (!IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners()
            .Any(endpoint => endpoint.Port == BridgeProtocol.RendezvousPort))
            await Task.Delay(20, token);
    }

    private sealed class SecondReadLatchPairingStore(PairingMemoryStore inner) : IBridgePairingSecretStore
    {
        private readonly TaskCompletionSource _secondReadStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseSecondRead = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _readCount;

        public Task SecondReadStarted => _secondReadStarted.Task;

        public byte[]? Read()
        {
            if (Interlocked.Increment(ref _readCount) == 2)
            {
                _secondReadStarted.TrySetResult();
                _releaseSecondRead.Task.GetAwaiter().GetResult();
            }
            return inner.Read();
        }

        public void Save(ReadOnlySpan<byte> secret) => inner.Save(secret);

        public void ReleaseSecondRead() => _releaseSecondRead.TrySetResult();
    }

    private sealed class ConnectionCheckClock : TimeProvider
    {
        private long _ticks = DateTime.UtcNow.Ticks;
        public override DateTimeOffset GetUtcNow() => new(Volatile.Read(ref _ticks), TimeSpan.Zero);
        public void Advance(TimeSpan duration) => Interlocked.Add(ref _ticks, duration.Ticks);
    }
}
