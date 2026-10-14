using System.Net;
using System.Security.Cryptography;
using Desktop.Application.Rendering;
using Desktop.Infrastructure.Bridge;
using Desktop.Infrastructure.Files;

namespace Desktop.Infrastructure.Tests;

public sealed partial class BridgeLoopbackHostTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RasterProductionOwnerNegotiatesImageCapabilityWithoutChangingLegacyProfile(bool coveredStack)
    {
        using var fixture = new BridgeResourceLeaseTests.Fixture();
        var paths = new LocalAppPathPolicy(fixture.Root);
        var path = Path.Combine(fixture.Root, "Photoshop.exe");
        await using var selection = new PhotoshopHostSelectionService(paths, TimeProvider.System,
            new SelectedFactoryReader(), new SelectedFactoryPeers(path));
        await selection.SelectAsync(path, default);
        var secret = RandomNumberGenerator.GetBytes(32);
        await using var factory = new BridgeSessionOwnerFactory(new PairingMemoryStore { Value = secret.ToArray() },
            paths, TimeProvider.System, selection);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var owner = await factory.StartTaskOwnerAsync("task_raster_capability", stop.Token, imageLayerContract: true, coveredStackContract: coveredStack);
        await AuthenticateDevice(secret.ToArray(), () => { }, stop.Token, expectCapabilityRejection: true);
        Assert.Empty(await owner.ListExecutionsAsync(stop.Token));
        if (coveredStack) await AuthenticateDevice(secret.ToArray(), () => { }, stop.Token, imageLayers: true, expectCapabilityRejection: true);
        await AuthenticateDevice(secret.ToArray(), () => { }, stop.Token, imageLayers: true, coveredStack: coveredStack);
        Assert.True((await owner.GetStatusAsync(stop.Token)).Ready);
        Assert.Empty(await owner.ListExecutionsAsync(stop.Token));
    }

    [Fact]
    public async Task E3ProductionOwnerRefusesOldAdapterBeforeExecutionAndAcceptsInstanceCapability()
    {
        using var fixture = new BridgeResourceLeaseTests.Fixture();
        var paths = new LocalAppPathPolicy(fixture.Root);
        var path = Path.Combine(fixture.Root, "Photoshop.exe");
        await using var selection = new PhotoshopHostSelectionService(paths, TimeProvider.System,
            new SelectedFactoryReader(), new SelectedFactoryPeers(path));
        await selection.SelectAsync(path, default);
        var secret = RandomNumberGenerator.GetBytes(32);
        await using var factory = new BridgeSessionOwnerFactory(new PairingMemoryStore { Value = secret.ToArray() }, paths, TimeProvider.System, selection);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var owner = await factory.StartTaskOwnerAsync("task_e3_capability", stop.Token, instanceContract: true);
        await AuthenticateDevice(secret.ToArray(), () => { }, stop.Token, expectCapabilityRejection: true);
        Assert.False((await owner.GetStatusAsync(stop.Token)).Ready);
        Assert.Empty(await owner.ListExecutionsAsync(stop.Token));
        await AuthenticateDevice(secret.ToArray(), () => { }, stop.Token, instanceIdentity: true);
        Assert.True((await owner.GetStatusAsync(stop.Token)).Ready);
        Assert.Empty(await owner.ListExecutionsAsync(stop.Token));
    }

    [Fact]
    public async Task SelectedHostFactoryRequiresSelectionBeforeSecretsListenerOrPairingCode()
    {
        using var fixture = new BridgeResourceLeaseTests.Fixture();
        var paths = new LocalAppPathPolicy(fixture.Root);
        await using var selection = new PhotoshopHostSelectionService(paths, TimeProvider.System, new WindowsBridgePeerProcessResolver());
        var secrets = new PairingMemoryStore { Value = RandomNumberGenerator.GetBytes(32) };
        await using var factory = new BridgeSessionOwnerFactory(secrets, paths, TimeProvider.System, selection);
        Assert.Equal(PhotoshopConnectionCheckStatus.HostSelectionRequired, (await factory.CheckAsync(default)).Status);
        var presented = false;
        var pairFailure = await Assert.ThrowsAsync<InvalidOperationException>(() => factory.PairAsync((code, _) =>
        { presented |= !code.IsEmpty; return Task.CompletedTask; }, default));
        Assert.Equal("PHOTOSHOP_HOST_SELECTION_REQUIRED", pairFailure.Message);
        Assert.False(presented);
        var startFailure = await Assert.ThrowsAsync<InvalidOperationException>(() => factory.StartTaskOwnerAsync("task_no_selection", default));
        Assert.Equal("PHOTOSHOP_HOST_SELECTION_REQUIRED", startFailure.Message);
        Assert.Equal("PHOTOSHOP_HOST_SELECTION_USE_ASYNC_OWNER", Assert.Throws<InvalidOperationException>(() => factory.CreateTaskOwner("task_bypass")).Message);
        Assert.Equal(0, secrets.Reads);
        Assert.Equal(0, secrets.Saves);
        Assert.False(Directory.Exists(Path.Combine(fixture.Root, "render")));
    }

    [Fact]
    public async Task SelectedHostFactoryPreemptsCheckAndHoldsChoiceUntilAuthenticatedProductionOwnerCloses()
    {
        using var fixture = new BridgeResourceLeaseTests.Fixture();
        var paths = new LocalAppPathPolicy(fixture.Root);
        var path = Path.Combine(fixture.Root, "Photoshop.exe");
        var reader = new SelectedFactoryReader();
        var peers = new SelectedFactoryPeers(path);
        await using var selection = new PhotoshopHostSelectionService(paths, TimeProvider.System, reader, peers);
        await selection.SelectAsync(path, default);
        var secret = RandomNumberGenerator.GetBytes(32);
        var secrets = new PairingMemoryStore { Value = secret.ToArray() };
        await using var factory = new BridgeSessionOwnerFactory(secrets, paths, TimeProvider.System, selection);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var check = factory.CheckAsync(stop.Token);
        await WaitForConnectionCheckListener(stop.Token);
        Assert.Equal("PHOTOSHOP_HOST_SELECTION_BUSY", (await Assert.ThrowsAsync<InvalidOperationException>(() => selection.SelectAsync(path, stop.Token))).Message);
        await using var owner = await factory.StartTaskOwnerAsync("task_selected_production", stop.Token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => check);
        Assert.Equal(1, reader.Active);
        Assert.True((await selection.ReadAsync(stop.Token)).IsInUse);
        Assert.Equal(PhotoshopConnectionCheckStatus.Busy, (await factory.CheckAsync(stop.Token)).Status);
        await AuthenticateDevice(secret, () => { }, stop.Token);
        Assert.True((await owner.GetStatusAsync(stop.Token)).Ready);
        Assert.True(peers.Calls >= 5); // challenge, handshake and dynamic-port heartbeats all use selection
        Assert.Equal(0, secrets.Saves);
        await owner.DisposeAsync();
        Assert.Equal(0, reader.Active);
        Assert.Equal(PhotoshopHostSelectionStatus.Valid, (await selection.SelectAsync(path, stop.Token)).Status);
    }

    [Fact]
    public async Task SelectedHostFactoryReleasesLeaseOnPairCancellationAndProductionBindFailure()
    {
        using var fixture = new BridgeResourceLeaseTests.Fixture();
        var paths = new LocalAppPathPolicy(fixture.Root);
        var path = Path.Combine(fixture.Root, "Photoshop.exe");
        var reader = new SelectedFactoryReader();
        await using var selection = new PhotoshopHostSelectionService(paths, TimeProvider.System, reader, new SelectedFactoryPeers(path));
        await selection.SelectAsync(path, default);
        var secrets = new PairingMemoryStore { Value = RandomNumberGenerator.GetBytes(32) };
        await using var factory = new BridgeSessionOwnerFactory(secrets, paths, TimeProvider.System, selection);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var cancelPair = new CancellationTokenSource();
        var shown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pairing = factory.PairAsync(async (code, token) =>
        {
            if (code.IsEmpty) return;
            shown.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        }, cancelPair.Token);
        await shown.Task.WaitAsync(stop.Token);
        Assert.Equal(1, reader.Active);
        cancelPair.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pairing);
        Assert.Equal(0, reader.Active);
        Assert.Equal(0, secrets.Saves);
        await using (var occupied = CreateOwner(new byte[32]))
        {
            await occupied.StartAsync(stop.Token);
            Assert.Equal("BRIDGE_PORT_UNAVAILABLE", (await Assert.ThrowsAsync<IOException>(() => factory.StartTaskOwnerAsync("task_occupied", stop.Token))).Message);
            Assert.Equal(0, reader.Active);
            Assert.True((await occupied.GetStatusAsync(stop.Token)).Listening);
        }
        await using var next = await factory.StartTaskOwnerAsync("task_released", stop.Token);
        Assert.Equal(1, reader.Active);
        await next.DisposeAsync();
        Assert.Equal(0, reader.Active);
    }

    [Fact]
    public async Task SelectedHostFactoryRejectsChangedChoiceWithoutReadingPairingSecret()
    {
        using var fixture = new BridgeResourceLeaseTests.Fixture();
        var paths = new LocalAppPathPolicy(fixture.Root);
        var path = Path.Combine(fixture.Root, "Photoshop.exe");
        var reader = new SelectedFactoryReader();
        await using var selection = new PhotoshopHostSelectionService(paths, TimeProvider.System, reader, new SelectedFactoryPeers(path));
        await selection.SelectAsync(path, default);
        reader.Hash = new string('b', 64);
        var secrets = new PairingMemoryStore { Value = RandomNumberGenerator.GetBytes(32) };
        await using var factory = new BridgeSessionOwnerFactory(secrets, paths, TimeProvider.System, selection);
        Assert.Equal(PhotoshopConnectionCheckStatus.HostSelectionInvalid, (await factory.CheckAsync(default)).Status);
        Assert.Equal("PHOTOSHOP_HOST_SELECTION_INVALID", (await Assert.ThrowsAsync<InvalidOperationException>(() => factory.StartTaskOwnerAsync("task_changed_host", default))).Message);
        Assert.Equal(0, secrets.Reads);
        Assert.Equal(0, reader.Active);
        Assert.False(Directory.Exists(Path.Combine(fixture.Root, "render")));
    }

    private sealed class SelectedFactoryReader : ISelectedPhotoshopExecutableReader
    {
        public int Active;
        public string Hash { get; set; } = new('a', 64);
        public Task<SelectedPhotoshopExecutable> OpenAsync(string path, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Interlocked.Increment(ref Active);
            return Task.FromResult(new SelectedPhotoshopExecutable(path, "26.8.1.1", 123, Hash, new SelectedFactoryHold(this)));
        }
    }

    private sealed class SelectedFactoryHold(SelectedFactoryReader reader) : IDisposable
    {
        public void Dispose() => Interlocked.Decrement(ref reader.Active);
    }

    private sealed class SelectedFactoryPeers(string path) : IBridgePeerProcessResolver
    {
        public int Calls;
        public BridgePeerProcessIdentity Resolve(IPEndPoint localEndpoint, IPEndPoint remoteEndpoint)
        {
            Interlocked.Increment(ref Calls);
            return new(42, 100, path);
        }
    }
}
