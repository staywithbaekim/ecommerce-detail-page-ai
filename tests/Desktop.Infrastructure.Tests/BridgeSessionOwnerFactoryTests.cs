using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Desktop.Application.Rendering;
using Desktop.Contracts.Bridge;
using Desktop.Infrastructure.Bridge;
using Desktop.Infrastructure.Files;

namespace Desktop.Infrastructure.Tests;

public sealed partial class BridgeLoopbackHostTests
{
    [Fact]
    public async Task DevicePairingStoresOnlyAfterThreeAuthenticatedReadyHeartbeatsAndClearsDisplay()
    {
        using var fixture = new BridgeResourceLeaseTests.Fixture();
        var store = new PairingMemoryStore();
        await using var factory = new BridgeSessionOwnerFactory(store, new LocalAppPathPolicy(fixture.Root), TimeProvider.System);
        Assert.False(await factory.IsConfiguredAsync(default));
        Assert.Throws<InvalidOperationException>(() => factory.CreateTaskOwner("task_new"));
        ReadOnlyMemory<byte> displayed = default;
        var clearCount = 0;
        await factory.PairAsync(async (bytes, token) =>
        {
            if (bytes.IsEmpty) { clearCount++; return; }
            displayed = bytes;
            Assert.Equal(0, store.Saves);
            await AuthenticateDevice(bytes.ToArray(), () => Assert.Equal(0, store.Saves), token);
        }, default);
        Assert.Equal(1, store.Saves);
        Assert.True(await factory.IsConfiguredAsync(default));
        Assert.Equal(1, clearCount);
        Assert.True(displayed.Span.ToArray().All(value => value == 0));
        // Setup released the fixed listener. A production owner uses the same implementation.
        await using var owner = factory.CreateTaskOwner("task_new");
        await owner.StartAsync(default);
        Assert.False((await owner.GetStatusAsync(default)).Paired);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancelOrDisposeDuringPairingKeepsExistingProtectedValueAndReleasesListener(bool dispose)
    {
        using var fixture = new BridgeResourceLeaseTests.Fixture();
        var store = new PairingMemoryStore { Value = new byte[32] };
        await using var factory = new BridgeSessionOwnerFactory(store, new LocalAppPathPolicy(fixture.Root), TimeProvider.System);
        using var stop = new CancellationTokenSource();
        var shown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleared = false;
        ReadOnlyMemory<byte> displayed = default;
        var pairing = factory.PairAsync((bytes, _) =>
        {
            if (bytes.IsEmpty) cleared = true;
            else { displayed = bytes; shown.TrySetResult(); }
            return Task.CompletedTask;
        }, stop.Token);
        await shown.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAsync<InvalidOperationException>(() => factory.PairAsync((_, _) => Task.CompletedTask, default));
        if (dispose) await factory.DisposeAsync(); else stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pairing);
        Assert.True(cleared);
        Assert.True(displayed.Span.ToArray().All(value => value == 0));
        Assert.Equal(0, store.Saves);
        Assert.True(store.Value!.All(value => value == 0));
        await using var next = CreateOwner(new byte[32]);
        await next.StartAsync(default);
    }

    [Fact]
    public async Task OccupiedRendezvousNeverDisplaysOrStoresNewPairingMaterial()
    {
        using var fixture = new BridgeResourceLeaseTests.Fixture();
        var store = new PairingMemoryStore();
        await using var owner = CreateOwner(new byte[32]);
        await owner.StartAsync(default);
        await using var factory = new BridgeSessionOwnerFactory(store, new LocalAppPathPolicy(fixture.Root), TimeProvider.System);
        var shown = false;
        await Assert.ThrowsAsync<IOException>(() => factory.PairAsync((bytes, _) =>
        { shown |= !bytes.IsEmpty; return Task.CompletedTask; }, default));
        Assert.False(shown);
        Assert.Equal(0, store.Saves);
    }

    [Fact]
    public async Task ProtectedStoreFailureDoesNotReportSetupSuccessOrRetainDisplayedCode()
    {
        using var fixture = new BridgeResourceLeaseTests.Fixture();
        var old = RandomNumberGenerator.GetBytes(32);
        var store = new PairingMemoryStore { Value = old, FailSave = true };
        await using var factory = new BridgeSessionOwnerFactory(store, new LocalAppPathPolicy(fixture.Root), TimeProvider.System);
        ReadOnlyMemory<byte> displayed = default;
        var cleared = false;
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => factory.PairAsync(async (bytes, token) =>
        {
            if (bytes.IsEmpty) { cleared = true; return; }
            displayed = bytes;
            await AuthenticateDevice(bytes.ToArray(), () => Assert.Equal(0, store.Saves), token);
        }, default));
        Assert.Equal("BRIDGE_PAIRING_STORAGE_UNAVAILABLE", error.Message);
        Assert.True(cleared);
        Assert.True(displayed.Span.ToArray().All(value => value == 0));
        Assert.Equal(old, store.Value);
        await using var next = CreateOwner(new byte[32]);
        await next.StartAsync(default);
        CryptographicOperations.ZeroMemory(old);
    }

    [Fact]
    public async Task DeviceSetupRejectsPluginWithoutProtectedStorageCapability()
    {
        using var fixture = new BridgeResourceLeaseTests.Fixture();
        var store = new PairingMemoryStore();
        await using var factory = new BridgeSessionOwnerFactory(store, new LocalAppPathPolicy(fixture.Root), TimeProvider.System);
        using var stop = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => factory.PairAsync(async (bytes, token) =>
        {
            if (bytes.IsEmpty) return;
            await AuthenticateDevice(bytes.ToArray(), () => { }, token, protectedStorage: false);
            stop.Cancel();
        }, stop.Token));
        Assert.Equal(0, store.Saves);
        Assert.Null(store.Value);
    }

    [Fact]
    public async Task ExpiredIntroductionClearsDisplayAndNeverSaves()
    {
        using var fixture = new BridgeResourceLeaseTests.Fixture();
        var store = new PairingMemoryStore();
        var time = new BridgeAdapterSessionsTests.TestClock();
        await using var factory = new BridgeSessionOwnerFactory(store, new LocalAppPathPolicy(fixture.Root), time);
        var cleared = false;
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => factory.PairAsync((bytes, _) =>
        {
            if (bytes.IsEmpty) cleared = true;
            else time.Now += TimeSpan.FromMinutes(6);
            return Task.CompletedTask;
        }, default));
        Assert.Equal("BRIDGE_PAIRING_EXPIRED", error.Message);
        Assert.True(cleared);
        Assert.Equal(0, store.Saves);
    }

    private static async Task AuthenticateDevice(byte[] secret, Action beforeThirdHeartbeat, CancellationToken token,
        bool protectedStorage = true, int heartbeatCount = 3, string hostState = "ready", string modalState = "idle",
        string? photoshopBuild = null, byte[]? wrongHandshakeSecret = null, bool useSocket = true,
        bool instanceIdentity = false, bool expectCapabilityRejection = false)
    {
        using var client = new HttpClient(useSocket ? new SocketHttpHandler() : new HttpClientHandler { UseProxy = false })
            { Timeout = TimeSpan.FromSeconds(5) };
        const string root = "http://127.0.0.1:17842";
        var request = new AdapterChallengeRequest("request_setup", BridgeAdapterCryptography.RandomToken(), "instance_setup",
            BridgeAdapterProtocol.PluginId, BridgeAdapterProtocol.PluginVersion, BridgeSessionOwnerFactory.SupportedPhotoshopBuild, "uxp_test",
            [BridgeAdapterProtocol.Capability, "psd-layer-path/psd-bottom-to-top", "smart-object/replace",
                "output/jpeg-srgb-icc", RenderPlanPlacementMigration.Capability, RenderOutputEvidenceValidator.VerificationPsdCapability,
                BridgeSessionOwnerFactory.ProtectedPairingCapability, RenderPlanPlacementMigration.SizeTableCapability,
                RenderPlanPlacementMigration.SizeInsertionCapability, RenderPlanVariantIdentity.Capability]);
        if (!protectedStorage) request = request with { Capabilities = request.Capabilities
            .Where(capability => capability != BridgeSessionOwnerFactory.ProtectedPairingCapability).ToArray() };
        if (photoshopBuild is not null) request = request with { PhotoshopBuild = photoshopBuild };
        if (instanceIdentity) request = request with { Capabilities = [.. request.Capabilities, RenderPlanInstanceIdentity.Capability] };
        byte[]? key = null;
        try
        {
            using var challengeResponse = await client.PostAsync(root + "/adapter/v1/challenges", Content(JsonSerializer.Serialize(
                new AdapterEnvelope<AdapterChallengeRequest>(BridgeAdapterProtocol.Version, "challenge_request", request), BridgeProtocol.JsonOptions)), token);
            if (!protectedStorage || photoshopBuild is not null || expectCapabilityRejection)
            {
                Assert.NotEqual(HttpStatusCode.OK, challengeResponse.StatusCode);
                return;
            }
            Assert.Equal(HttpStatusCode.OK, challengeResponse.StatusCode);
            var challenge = JsonSerializer.Deserialize<AdapterEnvelope<AdapterChallenge>>(
                await challengeResponse.Content.ReadAsStringAsync(token), BridgeProtocol.JsonOptions)!.Body;
            Assert.True(BridgeAdapterCryptography.FixedEquals(
                BridgeAdapterCryptography.ChallengeProof(secret, challenge), challenge.ServerProof));
            var handshake = new BridgeHandshakeBodyV1(challenge.RequestId, "corr_setup", DateTimeOffset.UtcNow,
                challenge.SessionId, challenge.Nonce, new(challenge.AppVersion, BridgeProtocol.Version, challenge.Capabilities),
                new(challenge.PluginVersion, challenge.PhotoshopBuild, challenge.UxpRuntimeVersion), "");
            handshake = handshake with { PairingProof = BridgeAdapterCryptography.PairingProof(wrongHandshakeSecret ?? secret, handshake) };
            var json = BridgeAdapterSessionsTests.Fixture.SerializeHandshake(handshake);
            using var pairingRequest = new HttpRequestMessage(HttpMethod.Post, root + "/handshake") { Content = Content(json) };
            pairingRequest.Headers.Add("X-Adapter-Handshake-Proof", BridgeAdapterCryptography.HandshakeContextProof(wrongHandshakeSecret ?? secret, challenge, json));
            using var response = await client.SendAsync(pairingRequest, token);
            if (wrongHandshakeSecret is not null)
            {
                Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
                return;
            }
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var paired = JsonSerializer.Deserialize<BridgeWireEnvelope<BridgeHandshakeResultV1>>(
                await response.Content.ReadAsStringAsync(token), BridgeProtocol.JsonOptions)!.Body;
            key = BridgeAdapterCryptography.SessionKey(secret, challenge);
            for (var seq = 1; seq <= heartbeatCount; seq++)
            {
                beforeThirdHeartbeat();
                var heartbeat = new BridgeRequestBodyV1<BridgeHeartbeatPayloadV1>($"request_setup_{seq}", "corr_setup",
                    paired.SessionId!, paired.Token!, DateTimeOffset.UtcNow, "heartbeat_setup", "heartbeat", new(hostState, modalState, 0));
                var body = JsonSerializer.Serialize(new BridgeWireEnvelope<BridgeRequestBodyV1<BridgeHeartbeatPayloadV1>>(
                    BridgeProtocol.Version, "request", heartbeat), BridgeProtocol.JsonOptions);
                var auth = new AdapterRequestAuthentication(heartbeat.RequestId, paired.SessionId!, request.PluginInstanceId, seq, paired.Token!, "");
                auth = auth with { Mac = BridgeAdapterCryptography.RequestMac(key, auth, "POST", "/heartbeat", body) };
                using var message = Authenticated($"http://127.0.0.1:{paired.ExecutionPort}", auth, body, "/heartbeat");
                using var result = await client.SendAsync(message, token);
                Assert.Equal(HttpStatusCode.OK, result.StatusCode);
            }
        }
        finally { CryptographicOperations.ZeroMemory(secret); if (key is not null) CryptographicOperations.ZeroMemory(key); }
    }

    private sealed class PairingMemoryStore : IBridgePairingSecretStore
    {
        public byte[]? Value { get; set; }
        public int Saves { get; private set; }
        public bool FailSave { get; init; }
        public bool FailRead { get; init; }
        public byte[]? LastRead { get; private set; }
        public int Reads { get; private set; }
        public byte[]? Read()
        {
            Reads++;
            if (FailRead) throw new InvalidOperationException("BRIDGE_PAIRING_STORAGE_UNAVAILABLE");
            return LastRead = Value?.ToArray();
        }
        public void Save(ReadOnlySpan<byte> secret)
        {
            if (FailSave) throw new InvalidOperationException("BRIDGE_PAIRING_STORAGE_UNAVAILABLE");
            Saves++;
            Value = secret.ToArray();
        }
    }
}
