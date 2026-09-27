using System.Text.Json;
using Desktop.Contracts.Bridge;
using Desktop.Contracts.Validation;
using Desktop.Infrastructure.Bridge;

namespace Desktop.Infrastructure.Tests;

public sealed class BridgeAdapterSessionsTests
{
    [Theory]
    [InlineData("0.1.0")]
    [InlineData("0.1.2")]
    public async Task DifferentPluginPatchVersionIsRejectedBeforeAuthentication(string version)
    {
        using var fixture = new Fixture();
        var request = new AdapterChallengeRequest("request_version", BridgeAdapterCryptography.RandomToken(), "instance_version",
            BridgeAdapterProtocol.PluginId, version, "26.8.1 test-build", "uxp_test", [BridgeAdapterProtocol.Capability]);
        var result = await fixture.Sessions.ChallengeAsync(JsonSerializer.Serialize(
            new AdapterEnvelope<AdapterChallengeRequest>(BridgeAdapterProtocol.Version, "challenge_request", request), BridgeProtocol.JsonOptions), default);
        Assert.False(result.IsSuccess);
        Assert.Null(await fixture.Sessions.GetCurrentAsync(default));
    }

    [Theory]
    [InlineData("/adapter/v1/resources/read")]
    [InlineData("/adapter/v1/events")]
    public async Task OversizedAuthenticatedResponseInvalidatesSessionBeforeSigning(string path)
    {
        using var fixture = new Fixture();
        var (_, _, key) = await fixture.Pair();
        var session = (await fixture.Sessions.GetCurrentAsync(CancellationToken.None))!;
        var auth = Fixture.Authentication(session, key, 1, path);
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Sessions.ExecuteAsync(auth, "POST", path, "{}",
            _ => Task.FromResult(new BridgeHttpResponse(200, new string('x', BridgeProtocol.StatusByteLimit + 1))), CancellationToken.None));
        Assert.Null(await fixture.Sessions.GetCurrentAsync(CancellationToken.None));
    }
    [Fact]
    public async Task OnlyAuthenticatedFreshHeartbeatsAllowNewWork()
    {
        using var fixture = new Fixture();
        var (_, _, key) = await fixture.Pair();
        var session = (await fixture.Sessions.GetCurrentAsync(CancellationToken.None))!;
        Assert.False((await fixture.Sessions.GetHealthAsync(CancellationToken.None)).Ready);
        const string body = """{"body":{"payload":{"host_state":"ready","modal_state":"idle","last_event_seq":4}}}""";
        var auth = Fixture.Authentication(session, key, 1, "/heartbeat");
        auth = auth with { Mac = BridgeAdapterCryptography.RequestMac(key, auth, "POST", "/heartbeat", body) };
        await fixture.Sessions.ExecuteAsync(auth, "POST", "/heartbeat", body,
            _ => Task.FromResult(new BridgeHttpResponse(200, "{}")), CancellationToken.None);
        var heartbeatAt = fixture.Time.Now;
        Assert.Equal(new BridgeSessionHealth(true, true, 1, 4, "ready", "idle", heartbeatAt),
            await fixture.Sessions.GetHealthAsync(CancellationToken.None));
        fixture.Time.Now += TimeSpan.FromSeconds(14);
        await fixture.Sessions.ExecuteAsync(auth, "POST", "/heartbeat", body,
            _ => throw new InvalidOperationException("replay must not dispatch"), CancellationToken.None);
        fixture.Time.Now += TimeSpan.FromSeconds(1);
        Assert.Equal(new BridgeSessionHealth(false, false, 1, 4, LastHeartbeatAt: heartbeatAt),
            await fixture.Sessions.GetHealthAsync(CancellationToken.None));
        Assert.NotNull(await fixture.Sessions.GetCurrentAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData("busy", "running")]
    [InlineData("blocked", "unknown")]
    public async Task ModalObservationRequiresAcceptedAuthenticationAndExpires(string hostState, string modalState)
    {
        using var fixture = new Fixture();
        var (_, _, key) = await fixture.Pair();
        var session = (await fixture.Sessions.GetCurrentAsync(CancellationToken.None))!;
        var initial = await fixture.Sessions.GetHealthAsync(CancellationToken.None);
        Assert.Null(initial.HostState);
        Assert.Null(initial.ModalState);
        Assert.Null(initial.LastHeartbeatAt);
        var body = JsonSerializer.Serialize(new { body = new { payload = new {
            host_state = hostState, modal_state = modalState, last_event_seq = 2 } } });
        var auth = Fixture.Authentication(session, key, 1, "/heartbeat");
        auth = auth with { Mac = BridgeAdapterCryptography.RequestMac(key, auth, "POST", "/heartbeat", body) };
        var rejected = await fixture.Sessions.ExecuteAsync(auth with { Mac = BridgeAdapterCryptography.RandomToken() },
            "POST", "/heartbeat", body, _ => throw new InvalidOperationException("must not dispatch"), CancellationToken.None);
        Assert.Equal(401, rejected.StatusCode);
        Assert.Equal(initial, await fixture.Sessions.GetHealthAsync(CancellationToken.None));
        // A signed request rejected by the heartbeat validator also must not change observed state.
        await fixture.Sessions.ExecuteAsync(auth, "POST", "/heartbeat", body,
            _ => Task.FromResult(new BridgeHttpResponse(400, "{}")), CancellationToken.None);
        Assert.Equal(initial, await fixture.Sessions.GetHealthAsync(CancellationToken.None));
        auth = Fixture.Authentication(session, key, 2, "/heartbeat");
        auth = auth with { Mac = BridgeAdapterCryptography.RequestMac(key, auth, "POST", "/heartbeat", body) };
        await fixture.Sessions.ExecuteAsync(auth, "POST", "/heartbeat", body,
            _ => Task.FromResult(new BridgeHttpResponse(200, "{}")), CancellationToken.None);
        var observedAt = fixture.Time.Now;
        Assert.Equal(new BridgeSessionHealth(true, false, 1, 2, hostState, modalState, observedAt),
            await fixture.Sessions.GetHealthAsync(CancellationToken.None));
        fixture.Time.Now += TimeSpan.FromSeconds(15);
        Assert.Equal(new BridgeSessionHealth(false, false, 1, 2, LastHeartbeatAt: observedAt),
            await fixture.Sessions.GetHealthAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShortLivedPairingCodeCannotBeUsedAfterSuccessOrExpiry(bool expired)
    {
        using var fixture = new Fixture(oneTime: true);
        var challenge = await fixture.Challenge();
        if (expired) fixture.Time.Now += TimeSpan.FromMinutes(5);
        var json = Fixture.SerializeHandshake(fixture.Handshake(challenge));
        var reply = await fixture.Sessions.PairAsync(json,
            BridgeAdapterCryptography.HandshakeContextProof(fixture.Secret, challenge, json), 51000, CancellationToken.None);
        Assert.Equal(expired ? 400 : 200, reply.StatusCode);
        var request = new AdapterChallengeRequest("request_again", BridgeAdapterCryptography.RandomToken(), "instance_again",
            BridgeAdapterProtocol.PluginId, BridgeAdapterProtocol.PluginVersion, "26.8.1 test-build", "uxp_test",
            [BridgeAdapterProtocol.Capability, "output/jpeg-srgb-icc"]);
        var result = await fixture.Sessions.ChallengeAsync(JsonSerializer.Serialize(new AdapterEnvelope<AdapterChallengeRequest>(
            BridgeAdapterProtocol.Version, "challenge_request", request), BridgeProtocol.JsonOptions), CancellationToken.None);
        Assert.Equal("BRIDGE_PAIRING_REJECTED", result.Error!.Code);
    }

    [Fact]
    public async Task SchemaRejectedHandshakeAlsoConsumesIdentifiedChallenge()
    {
        using var fixture = new Fixture();
        var challenge = await fixture.Challenge();
        var json = Fixture.SerializeHandshake(fixture.Handshake(challenge));
        var invalid = System.Text.Json.Nodes.JsonNode.Parse(json)!;
        invalid["body"]!["unknown"] = true;
        Assert.Equal(400, (await fixture.Sessions.PairAsync(invalid.ToJsonString(), "", 51000, CancellationToken.None)).StatusCode);
        Assert.Equal(400, (await fixture.Sessions.PairAsync(json,
            BridgeAdapterCryptography.HandshakeContextProof(fixture.Secret, challenge, json), 51000, CancellationToken.None)).StatusCode);
    }

    [Fact]
    public async Task AuthenticatedPairingBindsHostAndServerResponse()
    {
        using var fixture = new Fixture();
        var (challenge, reply, key) = await fixture.Pair();
        Assert.Equal(200, reply.StatusCode);
        Assert.True(BridgeAdapterCryptography.FixedEquals(
            BridgeAdapterCryptography.ChallengeProof(fixture.Secret, challenge), challenge.ServerProof));
        Assert.True(BridgeAdapterCryptography.FixedEquals(
            BridgeAdapterCryptography.HandshakeResponseProof(key, challenge.RequestId, reply.Body), reply.Mac!));
        Assert.True(fixture.Wire.Validate(reply.Body, BridgeEndpoint.Handshake, true).IsValid);
        var session = await fixture.Sessions.GetCurrentAsync(CancellationToken.None);
        Assert.Equal(challenge.SessionId, session!.SessionId);
        Assert.DoesNotContain(session.Token, session.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(session.Token, reply.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("client_proof")]
    [InlineData("context_proof")]
    [InlineData("host")]
    [InlineData("capability")]
    [InlineData("nonce")]
    [InlineData("expired")]
    [InlineData("port")]
    public async Task RejectedHandshakeConsumesItsChallenge(string mutation)
    {
        using var fixture = new Fixture();
        var challenge = await fixture.Challenge();
        var valid = fixture.Handshake(challenge);
        var request = mutation switch
        {
            "client_proof" => valid with { PairingProof = BridgeAdapterCryptography.Encode(new byte[32]) },
            "host" => valid with { Host = valid.Host with { PhotoshopBuild = "other_build" } },
            "capability" => valid with { Client = valid.Client with { RequestedCapabilities = [] } },
            "nonce" => valid with { Nonce = BridgeAdapterCryptography.Encode(new byte[32]) },
            _ => valid
        };
        if (mutation == "expired") fixture.Time.Now += TimeSpan.FromSeconds(30);
        var json = Fixture.SerializeHandshake(request);
        var proof = mutation == "context_proof" ? BridgeAdapterCryptography.Encode(new byte[32])
            : BridgeAdapterCryptography.HandshakeContextProof(fixture.Secret, challenge, json);
        var rejected = await fixture.Sessions.PairAsync(json, proof, mutation == "port" ? 80 : 51000, CancellationToken.None);
        Assert.Equal(400, rejected.StatusCode);
        Assert.Null(await fixture.Sessions.GetCurrentAsync(CancellationToken.None));
        var replay = Fixture.SerializeHandshake(valid);
        rejected = await fixture.Sessions.PairAsync(replay,
            BridgeAdapterCryptography.HandshakeContextProof(fixture.Secret, challenge, replay), 51000, CancellationToken.None);
        Assert.Equal(400, rejected.StatusCode);
    }

    [Fact]
    public async Task ASecondAuthenticatedInstanceInvalidatesOldTokenAndKey()
    {
        using var fixture = new Fixture();
        var (_, _, oldKey) = await fixture.Pair();
        var oldSession = await fixture.Sessions.GetCurrentAsync(CancellationToken.None);
        await fixture.Pair(instance: "instance_two");
        var auth = Fixture.Authentication(oldSession!, oldKey, 1);
        var calls = 0;
        var result = await fixture.Sessions.ExecuteAsync(auth, "POST", "/adapter/v1/claims", "{}",
            _ => { calls++; return Task.FromResult(new BridgeHttpResponse(200, "{}")); }, CancellationToken.None);
        Assert.Equal(401, result.StatusCode);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task ExactRequestReplayReturnsSameSignedResponseWithoutDispatch()
    {
        using var fixture = new Fixture();
        var (_, _, key) = await fixture.Pair();
        var auth = Fixture.Authentication((await fixture.Sessions.GetCurrentAsync(CancellationToken.None))!, key, 1);
        var calls = 0;
        Task<BridgeHttpResponse> Operation(CancellationToken _) { calls++; return Task.FromResult(new BridgeHttpResponse(200, "{\"state\":\"claimed\"}")); }
        var first = await fixture.Sessions.ExecuteAsync(auth, "POST", "/adapter/v1/claims", "{}", Operation, CancellationToken.None);
        var replay = await fixture.Sessions.ExecuteAsync(auth, "POST", "/adapter/v1/claims", "{}", Operation, CancellationToken.None);
        Assert.Equal(first, replay);
        Assert.Equal(1, calls);
        Assert.True(BridgeAdapterCryptography.FixedEquals(
            BridgeAdapterCryptography.ResponseMac(key, auth, first.StatusCode, first.Body), first.Mac!));
    }

    [Theory]
    [InlineData("token")]
    [InlineData("mac")]
    [InlineData("instance")]
    [InlineData("body")]
    [InlineData("method")]
    [InlineData("path")]
    [InlineData("sequence")]
    [InlineData("request_id")]
    public async Task TamperingNeverReachesOperation(string mutation)
    {
        using var fixture = new Fixture();
        var (_, _, key) = await fixture.Pair();
        var auth = Fixture.Authentication((await fixture.Sessions.GetCurrentAsync(CancellationToken.None))!, key, 1);
        auth = mutation switch
        {
            "token" => auth with { Token = BridgeAdapterCryptography.RandomToken() },
            "mac" => auth with { Mac = BridgeAdapterCryptography.RandomToken() },
            "instance" => auth with { PluginInstanceId = "other_instance" },
            "sequence" => auth with { Sequence = 2 },
            "request_id" => auth with { RequestId = "other_request" },
            _ => auth
        };
        var calls = 0;
        var result = await fixture.Sessions.ExecuteAsync(auth,
            mutation == "method" ? "GET" : "POST", mutation == "path" ? "/release" : "/adapter/v1/claims",
            mutation == "body" ? "{ }" : "{}",
            _ => { calls++; return Task.FromResult(new BridgeHttpResponse(200, "{}")); }, CancellationToken.None);
        Assert.Equal(401, result.StatusCode);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task SignedSequenceGapAndOldNonlatestReplayAreRejected()
    {
        using var fixture = new Fixture();
        var (_, _, key) = await fixture.Pair();
        var session = (await fixture.Sessions.GetCurrentAsync(CancellationToken.None))!;
        var calls = 0;
        Task<BridgeHttpResponse> Operation(CancellationToken _) { calls++; return Task.FromResult(new BridgeHttpResponse(200, "{}")); }
        foreach (var (seq, status) in new (long, int)[] { (2, 401), (1, 200), (2, 200), (1, 401) })
        {
            var reply = await fixture.Sessions.ExecuteAsync(Fixture.Authentication(session, key, seq),
                "POST", "/adapter/v1/claims", "{}", Operation, CancellationToken.None);
            Assert.Equal(status, reply.StatusCode);
        }
        Assert.Equal(2, calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IdleAndAbsoluteExpiryRejectPreviouslyValidToken(bool absolute)
    {
        using var fixture = new Fixture();
        var (_, _, key) = await fixture.Pair();
        var session = (await fixture.Sessions.GetCurrentAsync(CancellationToken.None))!;
        var sequence = 1L;
        if (absolute)
        {
            for (var minutes = 10; minutes < 480; minutes += 10)
            {
                fixture.Time.Now += TimeSpan.FromMinutes(10);
                var reply = await fixture.Sessions.ExecuteAsync(Fixture.Authentication(session, key, sequence++),
                    "POST", "/adapter/v1/claims", "{}", _ => Task.FromResult(new BridgeHttpResponse(200, "{}")), CancellationToken.None);
                Assert.Equal(200, reply.StatusCode);
            }
            fixture.Time.Now += TimeSpan.FromMinutes(10);
        }
        else fixture.Time.Now += TimeSpan.FromMinutes(15);
        var result = await fixture.Sessions.ExecuteAsync(Fixture.Authentication(session, key, sequence),
            "POST", "/adapter/v1/claims", "{}", _ => throw new InvalidOperationException("must-not-call"), CancellationToken.None);
        Assert.Equal(401, result.StatusCode);
        Assert.Null(await fixture.Sessions.GetCurrentAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ReleaseInvalidatesAndDispatchFailureCannotReplaySideEffects()
    {
        using var fixture = new Fixture();
        var (_, _, key) = await fixture.Pair();
        var session = (await fixture.Sessions.GetCurrentAsync(CancellationToken.None))!;
        var auth = Fixture.Authentication(session, key, 1, path: "/release");
        var released = await fixture.Sessions.ExecuteAsync(auth, "POST", "/release", "{}",
            _ => Task.FromResult(new BridgeHttpResponse(200, "{}")), CancellationToken.None);
        Assert.Equal(200, released.StatusCode);
        Assert.Null(await fixture.Sessions.GetCurrentAsync(CancellationToken.None));
        (_, _, key) = await fixture.Pair();
        session = (await fixture.Sessions.GetCurrentAsync(CancellationToken.None))!;
        auth = Fixture.Authentication(session, key, 1);
        await Assert.ThrowsAsync<IOException>(() => fixture.Sessions.ExecuteAsync(auth, "POST", "/adapter/v1/claims", "{}",
            _ => throw new IOException("test failure"), CancellationToken.None));
        Assert.Null(await fixture.Sessions.GetCurrentAsync(CancellationToken.None));
    }

    internal sealed class Fixture : IDisposable
    {
        public byte[] Secret { get; } = Enumerable.Range(0, 32).Select(x => (byte)x).ToArray();
        public TestClock Time { get; } = new();
        public BridgeWireValidator Wire { get; } = new(new JsonSchemaContractValidator());
        public BridgeAdapterSessions Sessions { get; }
        private int _request;

        public Fixture(bool oneTime = false) => Sessions = new(Secret, new("task_test", "0.1.0", "26.8.1 test-build",
            [BridgeAdapterProtocol.Capability, "output/jpeg-srgb-icc"], oneTime ? Time.Now.AddMinutes(5) : null),
            Wire, new(new JsonSchemaContractValidator(), Wire), Time);

        public async Task<AdapterChallenge> Challenge(string instance = "instance_one")
        {
            var request = new AdapterChallengeRequest($"request_{++_request:D4}", BridgeAdapterCryptography.RandomToken(), instance,
                BridgeAdapterProtocol.PluginId, BridgeAdapterProtocol.PluginVersion, "26.8.1 test-build", "uxp_test",
                [BridgeAdapterProtocol.Capability, "output/jpeg-srgb-icc"]);
            var result = await Sessions.ChallengeAsync(JsonSerializer.Serialize(new AdapterEnvelope<AdapterChallengeRequest>(
                BridgeAdapterProtocol.Version, "challenge_request", request), BridgeProtocol.JsonOptions), CancellationToken.None);
            Assert.True(result.IsSuccess);
            return result.Value!;
        }

        public BridgeHandshakeBodyV1 Handshake(AdapterChallenge challenge)
        {
            var body = new BridgeHandshakeBodyV1(challenge.RequestId, "corr_test", Time.Now, challenge.SessionId, challenge.Nonce,
                new(challenge.AppVersion, BridgeProtocol.Version, challenge.Capabilities),
                new(challenge.PluginVersion, challenge.PhotoshopBuild, challenge.UxpRuntimeVersion), "");
            return body with { PairingProof = BridgeAdapterCryptography.PairingProof(Secret, body) };
        }

        public async Task<(AdapterChallenge Challenge, BridgeHttpResponse Reply, byte[] Key)> Pair(string instance = "instance_one")
        {
            var challenge = await Challenge(instance);
            var json = SerializeHandshake(Handshake(challenge));
            var response = await Sessions.PairAsync(json, BridgeAdapterCryptography.HandshakeContextProof(Secret, challenge, json),
                51000, CancellationToken.None);
            return (challenge, response, BridgeAdapterCryptography.SessionKey(Secret, challenge));
        }

        public static string SerializeHandshake(BridgeHandshakeBodyV1 body) => JsonSerializer.Serialize(
            new BridgeWireEnvelope<BridgeHandshakeBodyV1>(BridgeProtocol.Version, "handshake_request", body), BridgeProtocol.JsonOptions);

        public static AdapterRequestAuthentication Authentication(BridgePairedSession session, byte[] key, long sequence,
            string path = "/adapter/v1/claims")
        {
            var auth = new AdapterRequestAuthentication($"request_{sequence:D4}", session.SessionId,
                session.PluginInstanceId, sequence, session.Token, "");
            return auth with { Mac = BridgeAdapterCryptography.RequestMac(key, auth, "POST", path, "{}") };
        }

        public void Dispose() => Sessions.Dispose();
    }

    internal sealed class TestClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.FromUnixTimeSeconds(1800316800);
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
