using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Security.Cryptography;
using Desktop.Contracts.Bridge;
using Desktop.Contracts.Validation;
using Desktop.Infrastructure.Bridge;
using Desktop.Infrastructure.Files;
using Desktop.Application.Rendering;
using Desktop.Application.Psd;

namespace Desktop.Infrastructure.Tests;

public sealed partial class BridgeLoopbackHostTests
{
    // These tests share this class with the existing listener test because all use the fixed rendezvous port.
    [Fact]
    public async Task SessionOwnerReportsAuthenticatedReadinessAndReleasesListenerOnStop()
    {
        using var resources = new BridgeResourceLeaseTests.Fixture();
        resources.Plan["batch_id"] = "task_test";
        resources.Rehash();
        var templateEvidence = new PsdInspectionDocument(resources.Plan["resources"]![0]!["sha256"]!.GetValue<string>(), 8,
            new(790, 1252, 8, "RGB"), 1, 1, 0, 0, 0,
            resources.Plan["targets"]![0]!["expected_layer"]!["structure_signature"]!.GetValue<string>(),
            new(1, 1, 2), 1024, null,
            [new([1], 257, "product_frame", "smartobject", false, true, true, false, 255, 255, "norm",
                new(0, 0, 790, 1252), false, false, null, new("test", "embedded.jpg", "data", "jpg", "jpg", 8, false, []))]);
        var paths = new LocalAppPathPolicy(resources.Root);
        using var output = BridgeOutputReservation.Create(resources.Plan.ToJsonString(), "outputs", paths,
            new RenderPlanValidator(new JsonSchemaContractValidator()));
        var secret = Enumerable.Range(0, 32).Select(value => (byte)value).ToArray();
        var required = resources.Plan["required_capabilities"]!.AsArray().Select(item => item!.GetValue<string>())
            .Append(BridgeAdapterProtocol.Capability).ToArray();
        await using var owner = new BridgeSessionOwner(secret, OwnerPolicy() with { Capabilities = required }, new BridgeExecutionBrokerTests.MemoryJournal());
        var submissions = 0;
        Assert.Equal("BRIDGE_DISCONNECTED", (await owner.SubmitPageAsync(resources.Plan.ToJsonString(),
            resources.Sources, paths, output, templateEvidence, () => submissions++, CancellationToken.None)).Error!.Code);
        Assert.Equal(0, submissions);
        Assert.Equal(new BridgeSessionSnapshot(false, false, false, false, 0), await owner.GetStatusAsync(CancellationToken.None));
        await owner.StartAsync(CancellationToken.None);
        Assert.Equal(new BridgeSessionSnapshot(true, false, false, false, 0), await owner.GetStatusAsync(CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => owner.StartAsync(CancellationToken.None));
        using var client = new HttpClient(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false })
            { Timeout = TimeSpan.FromSeconds(5) };
        const string root = "http://127.0.0.1:17842";
        var request = new AdapterChallengeRequest("request_owner", BridgeAdapterCryptography.RandomToken(), "instance_owner",
            BridgeAdapterProtocol.PluginId, BridgeAdapterProtocol.PluginVersion, "26.8.1 test-build", "uxp_test",
            required);
        using var challengeResponse = await client.PostAsync(root + "/adapter/v1/challenges", Content(JsonSerializer.Serialize(
            new AdapterEnvelope<AdapterChallengeRequest>(BridgeAdapterProtocol.Version, "challenge_request", request), BridgeProtocol.JsonOptions)));
        Assert.Equal(HttpStatusCode.OK, challengeResponse.StatusCode);
        var challenge = JsonSerializer.Deserialize<AdapterEnvelope<AdapterChallenge>>(
            await challengeResponse.Content.ReadAsStringAsync(), BridgeProtocol.JsonOptions)!.Body;
        var handshake = new BridgeHandshakeBodyV1(challenge.RequestId, "corr_owner", DateTimeOffset.UtcNow,
            challenge.SessionId, challenge.Nonce, new(challenge.AppVersion, BridgeProtocol.Version, challenge.Capabilities),
            new(challenge.PluginVersion, challenge.PhotoshopBuild, challenge.UxpRuntimeVersion), "");
        handshake = handshake with { PairingProof = BridgeAdapterCryptography.PairingProof(secret, handshake) };
        var json = BridgeAdapterSessionsTests.Fixture.SerializeHandshake(handshake);
        using var pairingRequest = new HttpRequestMessage(HttpMethod.Post, root + "/handshake") { Content = Content(json) };
        pairingRequest.Headers.Add("X-Adapter-Handshake-Proof", BridgeAdapterCryptography.HandshakeContextProof(secret, challenge, json));
        using var pairingResponse = await client.SendAsync(pairingRequest);
        Assert.Equal(HttpStatusCode.OK, pairingResponse.StatusCode);
        var paired = JsonSerializer.Deserialize<BridgeWireEnvelope<BridgeHandshakeResultV1>>(
            await pairingResponse.Content.ReadAsStringAsync(), BridgeProtocol.JsonOptions)!.Body;
        var key = BridgeAdapterCryptography.SessionKey(secret, challenge);
        Assert.False((await owner.GetStatusAsync(CancellationToken.None)).Ready);
        Task<bool>? release = null;
        using var releaseDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        for (var sequence = 1; sequence <= 3; sequence++)
        {
            var heartbeat = new BridgeRequestBodyV1<BridgeHeartbeatPayloadV1>($"request_owner_{sequence}", "corr_owner",
                paired.SessionId!, paired.Token!, DateTimeOffset.UtcNow, "heartbeat_owner", "heartbeat",
                sequence == 1 ? new("blocked", "unknown", 0) : new("ready", "idle", sequence == 3 ? 4 : 0));
            var body = JsonSerializer.Serialize(new BridgeWireEnvelope<BridgeRequestBodyV1<BridgeHeartbeatPayloadV1>>(
                BridgeProtocol.Version, "request", heartbeat), BridgeProtocol.JsonOptions);
            var auth = new AdapterRequestAuthentication(heartbeat.RequestId, paired.SessionId!, request.PluginInstanceId,
                sequence, paired.Token!, "");
            auth = auth with { Mac = BridgeAdapterCryptography.RequestMac(key, auth, "POST", "/heartbeat", body) };
            using var heartbeatRequest = Authenticated($"http://127.0.0.1:{paired.ExecutionPort}", auth, body, "/heartbeat");
            using var heartbeatResponse = await client.SendAsync(heartbeatRequest);
            Assert.Equal(HttpStatusCode.OK, heartbeatResponse.StatusCode);
            if (sequence == 2)
            {
                release = owner.WaitForReleaseAsync(4, 1, releaseDeadline.Token);
                Assert.False(release.IsCompleted); // Ready without the completion sequence is not a release ACK.
            }
            if (sequence == 1)
            {
                Assert.Equal("BRIDGE_HOST_BLOCKED", (await owner.SubmitPageAsync(resources.Plan.ToJsonString(),
                    resources.Sources, paths, output, templateEvidence, () => submissions++, default)).Error!.Code);
                Assert.Equal(0, submissions);
                Assert.Empty(await owner.ListExecutionsAsync(default));
            }
        }
        var status = await owner.GetStatusAsync(CancellationToken.None);
        Assert.True(status.Listening && status.Paired && status.Connected && status.Ready);
        Assert.Equal(3, status.Heartbeats);
        Assert.Equal("ready", status.HostState);
        Assert.Equal("idle", status.ModalState);
        Assert.NotNull(status.LastHeartbeatAt);
        Assert.True(await release!);
        // An ACK that arrived before the waiter starts must not require another heartbeat.
        Assert.True(await owner.WaitForReleaseAsync(4, 2, releaseDeadline.Token));
        foreach (var boundary in new[] { (Sequence: 4, Baseline: 3L), (Sequence: 5, Baseline: 2L) })
        {
            using var cancelledRelease = new CancellationTokenSource();
            var pending = owner.WaitForReleaseAsync(boundary.Sequence, boundary.Baseline, cancelledRelease.Token);
            Assert.False(pending.IsCompleted); // Neither an old heartbeat nor a different event can release this page.
            cancelledRelease.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        }
        Assert.DoesNotContain(paired.Token!, JsonSerializer.Serialize(status), StringComparison.Ordinal);
        Assert.DoesNotContain(paired.SessionId!, status.ToString(), StringComparison.Ordinal);
        Assert.Equal("BRIDGE_PLAN_INVALID", (await owner.SubmitPageAsync(resources.Plan.ToJsonString(), resources.Sources,
            paths, output, templateEvidence with { StructureSignature = new string('0', 64) }, CancellationToken.None)).Error!.Code);
        var unnegotiated = resources.Plan.DeepClone();
        unnegotiated["required_capabilities"]!.AsArray().Add("future/unsupported-v1");
        unnegotiated["plan_digest"] = RenderPlanDigest.Compute(unnegotiated.ToJsonString());
        Assert.Equal("BRIDGE_PLAN_INVALID", (await owner.SubmitPageAsync(unnegotiated.ToJsonString(), resources.Sources,
            paths, output, templateEvidence, CancellationToken.None)).Error!.Code);
        var submitted = await owner.SubmitPageAsync(resources.Plan.ToJsonString(), resources.Sources, paths, output, templateEvidence,
            () => submissions++, CancellationToken.None);
        Assert.True(submitted.IsSuccess, submitted.Error?.Code);
        Assert.Equal(1, submissions);
        var executionId = submitted.Value!.ExecutionId!;
        Assert.Equal("accepted", (await owner.QueryAsync(executionId, CancellationToken.None))!.State);
        Assert.Equal("BRIDGE_EXECUTION_UNKNOWN", (await owner.SubmitPageAsync(resources.Plan.ToJsonString(),
            resources.Sources, paths, output, templateEvidence, () => submissions++, CancellationToken.None)).Error!.Code);
        Assert.Equal(1, submissions);
        var claimJson = JsonSerializer.Serialize(new AdapterEnvelope<AdapterClaimRequest>(BridgeAdapterProtocol.Version,
            "claim_request", new()), BridgeProtocol.JsonOptions);
        var claimAuth = new AdapterRequestAuthentication("request_owner_claim", paired.SessionId!, request.PluginInstanceId,
            4, paired.Token!, "");
        claimAuth = claimAuth with { Mac = BridgeAdapterCryptography.RequestMac(key, claimAuth, "POST", "/adapter/v1/claims", claimJson) };
        using var claimRequest = Authenticated($"http://127.0.0.1:{paired.ExecutionPort}", claimAuth, claimJson);
        using var claimResponse = await client.SendAsync(claimRequest);
        Assert.Equal(HttpStatusCode.OK, claimResponse.StatusCode);
        var claim = JsonSerializer.Deserialize<AdapterEnvelope<AdapterClaimResponse>>(
            await claimResponse.Content.ReadAsStringAsync(), BridgeProtocol.JsonOptions)!.Body.Claim!;
        Assert.Equal(executionId, claim.ExecutionId);
        Assert.Equal("claimed", claim.State);
        Assert.DoesNotContain(resources.Root, claim.Plan!.Value.GetRawText(), StringComparison.Ordinal);
        await owner.CancelAsync(executionId, CancellationToken.None);
        Assert.True((await owner.QueryAsync(executionId, CancellationToken.None))!.CancelRequested);
        using (File.Open(Path.Combine(resources.Root, resources.Sources[0].OwnedRelativePath), FileMode.Open, FileAccess.Write)) { }
        await owner.DisposeAsync();
        await owner.DisposeAsync();
        Assert.False(await owner.WaitForReleaseAsync(4, 2, CancellationToken.None));
        Assert.Equal(new BridgeSessionSnapshot(false, false, false, false, 0), await owner.GetStatusAsync(CancellationToken.None));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => owner.StartAsync(CancellationToken.None));
        await using var next = CreateOwner(secret);
        await next.StartAsync(CancellationToken.None);
    }

    [Fact]
    public async Task SessionOwnerStartupFailureDoesNotStopTheExistingOwnerOrRemainRestartable()
    {
        var secret = new byte[32];
        await using var active = CreateOwner(secret);
        await active.StartAsync(CancellationToken.None);
        await using var occupied = CreateOwner(secret);
        var failure = await Assert.ThrowsAsync<IOException>(() => occupied.StartAsync(CancellationToken.None));
        Assert.Equal("BRIDGE_PORT_UNAVAILABLE", failure.Message);
        Assert.True((await active.GetStatusAsync(CancellationToken.None)).Listening);
        Assert.False((await occupied.GetStatusAsync(CancellationToken.None)).Listening);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => occupied.StartAsync(CancellationToken.None));
        await occupied.DisposeAsync();
    }

    [Fact]
    public async Task SessionOwnerInvalidJournalFailsBeforeListeningAndPreservesItsData()
    {
        var journal = new BridgeExecutionBrokerTests.MemoryJournal();
        await journal.SaveAsync(new(99, []), CancellationToken.None);
        await using var invalid = new BridgeSessionOwner(new byte[32], OwnerPolicy(), journal);
        var failure = await Assert.ThrowsAsync<InvalidDataException>(() => invalid.StartAsync(CancellationToken.None));
        Assert.Equal("BRIDGE_JOURNAL_INVALID", failure.Message);
        Assert.Equal(99, journal.Value!.Version);
        Assert.False((await invalid.GetStatusAsync(CancellationToken.None)).Listening);
        await using var valid = CreateOwner(new byte[32]);
        await valid.StartAsync(CancellationToken.None);
    }

    [Fact]
    public async Task SessionOwnerCancelledStartAndDisposalBeforeStartDoNotOpenAListener()
    {
        await using var owner = CreateOwner(new byte[32]);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => owner.StartAsync(cancelled.Token));
        Assert.False((await owner.GetStatusAsync(CancellationToken.None)).Listening);
        await owner.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => owner.StartAsync(CancellationToken.None));
        await using var next = CreateOwner(new byte[32]);
        await next.StartAsync(CancellationToken.None);
    }

    private static BridgeSessionOwner CreateOwner(byte[] secret) => new(secret, OwnerPolicy(), new BridgeExecutionBrokerTests.MemoryJournal());
    private static BridgeAdapterSessionPolicy OwnerPolicy() => new("task_test", "0.1.0", "26.8.1 test-build",
        [BridgeAdapterProtocol.Capability], DateTimeOffset.UtcNow.AddMinutes(5));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RealLoopbackEnforcesPortAuthenticationAndClaimReplay(bool socketCarrier)
    {
        using var fixture = new BridgeAdapterSessionsTests.Fixture();
        using var resources = new BridgeResourceLeaseTests.Fixture();
        using var broker = BridgeExecutionBrokerTests.Create(new BridgeExecutionBrokerTests.MemoryJournal());
        var adapter = new BridgeAdapterValidator(new JsonSchemaContractValidator(), fixture.Wire);
        await using var host = new BridgeLoopbackHost(fixture.Sessions, broker, adapter, fixture.Wire);
        await host.StartAsync(CancellationToken.None);
        using var client = new HttpClient(socketCarrier ? new SocketHttpHandler()
            : new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(5) };
        var discovery = $"http://127.0.0.1:{BridgeProtocol.RendezvousPort}";
        var execute = $"http://127.0.0.1:{host.ExecutionPort}";
        Assert.InRange(host.ExecutionPort, BridgeProtocol.MinExecutionPort, BridgeProtocol.MaxExecutionPort);
        Assert.NotEqual(BridgeProtocol.RendezvousPort, host.ExecutionPort);
        var request = new AdapterChallengeRequest("request_http", BridgeAdapterCryptography.RandomToken(), "instance_http",
            BridgeAdapterProtocol.PluginId, BridgeAdapterProtocol.PluginVersion, "26.8.1 test-build", "uxp_test", [BridgeAdapterProtocol.Capability, "output/jpeg-srgb-icc"]);
        var challengeJson = JsonSerializer.Serialize(new AdapterEnvelope<AdapterChallengeRequest>(BridgeAdapterProtocol.Version,
            "challenge_request", request), BridgeProtocol.JsonOptions);
        using (var rejected = await client.PostAsync(execute + "/adapter/v1/challenges", Content(challengeJson)))
            Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        using (var origin = new HttpRequestMessage(HttpMethod.Post, discovery + "/adapter/v1/challenges") { Content = Content(challengeJson) })
        {
            origin.Headers.Add("Origin", "http://untrusted.invalid");
            using var rejected = await client.SendAsync(origin);
            Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        }
        AdapterChallenge challenge;
        using (var reply = await client.PostAsync(discovery + "/adapter/v1/challenges", Content(challengeJson)))
        {
            Assert.Equal(HttpStatusCode.OK, reply.StatusCode);
            challenge = JsonSerializer.Deserialize<AdapterEnvelope<AdapterChallenge>>(await reply.Content.ReadAsStringAsync(), BridgeProtocol.JsonOptions)!.Body;
        }
        var json = BridgeAdapterSessionsTests.Fixture.SerializeHandshake(fixture.Handshake(challenge));
        using var handshake = new HttpRequestMessage(HttpMethod.Post, discovery + "/handshake") { Content = Content(json) };
        handshake.Headers.Add("X-Adapter-Handshake-Proof", BridgeAdapterCryptography.HandshakeContextProof(fixture.Secret, challenge, json));
        using var paired = await client.SendAsync(handshake);
        Assert.Equal(HttpStatusCode.OK, paired.StatusCode);
        var key = BridgeAdapterCryptography.SessionKey(fixture.Secret, challenge);
        var pairedJson = await paired.Content.ReadAsStringAsync();
        Assert.True(BridgeAdapterCryptography.FixedEquals(BridgeAdapterCryptography.HandshakeResponseProof(key, challenge.RequestId, pairedJson),
            paired.Headers.GetValues("X-Adapter-Mac").Single()));
        var session = (await fixture.Sessions.GetCurrentAsync(CancellationToken.None))!;
        var heartbeat = new BridgeRequestBodyV1<BridgeHeartbeatPayloadV1>("request_heartbeat", "corr_heartbeat",
            session.SessionId, session.Token, DateTimeOffset.UtcNow, "heartbeat_once", "heartbeat", new("ready", "idle", 0));
        var heartbeatJson = JsonSerializer.Serialize(new BridgeWireEnvelope<BridgeRequestBodyV1<BridgeHeartbeatPayloadV1>>(
            BridgeProtocol.Version, "request", heartbeat), BridgeProtocol.JsonOptions);
        var heartbeatAuth = new AdapterRequestAuthentication("request_heartbeat", session.SessionId, session.PluginInstanceId, 1, session.Token, "");
        heartbeatAuth = heartbeatAuth with { Mac = BridgeAdapterCryptography.RequestMac(key, heartbeatAuth, "POST", "/heartbeat", heartbeatJson) };
        using (var heartbeatRequest = Authenticated(execute, heartbeatAuth, heartbeatJson, "/heartbeat"))
        using (var heartbeatReply = await client.SendAsync(heartbeatRequest))
            Assert.Equal(HttpStatusCode.OK, heartbeatReply.StatusCode);
        var lease = await resources.Open(session.SessionId);
        using var output = BridgeOutputReservation.Create(resources.Plan.ToJsonString(), "outputs",
            new LocalAppPathPolicy(resources.Root), new RenderPlanValidator(new JsonSchemaContractValidator()));
        await broker.RegisterResourcesAsync(lease, CancellationToken.None, output);
        var binding = lease.Bindings[0];
        var planRequest = JsonNode.Parse(BridgeExecutionBrokerTests.Request(session.SessionId, session.Token,
            resources.Plan, lease.Bindings))!;
        planRequest["body"]!["payload"]!["output_bindings"]![0]!["output_root_handle"] = output.Binding.OutputRootHandle;
        var submitted = await broker.SubmitAsync(planRequest.ToJsonString(), CancellationToken.None);
        var claimJson = JsonSerializer.Serialize(new AdapterEnvelope<AdapterClaimRequest>(BridgeAdapterProtocol.Version, "claim_request", new()), BridgeProtocol.JsonOptions);
        var auth = new AdapterRequestAuthentication("request_claim", session.SessionId, session.PluginInstanceId, 2, session.Token, "");
        auth = auth with { Mac = BridgeAdapterCryptography.RequestMac(key, auth, "POST", "/adapter/v1/claims", claimJson) };
        using var firstRequest = Authenticated(execute, auth, claimJson);
        using var first = await client.SendAsync(firstRequest);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var result = await first.Content.ReadAsStringAsync();
        Assert.True(adapter.Validate(result, "claim_response").IsValid);
        using var retryRequest = Authenticated(execute, auth, claimJson);
        using var retry = await client.SendAsync(retryRequest);
        Assert.Equal(result, await retry.Content.ReadAsStringAsync());
        using var tamperedRequest = Authenticated(execute, auth, claimJson + " ");
        using var tampered = await client.SendAsync(tamperedRequest);
        Assert.Equal(HttpStatusCode.Unauthorized, tampered.StatusCode);
        var queryPath = $"/executions/{BridgeExecutionBrokerTests.Execution(submitted)}?after_seq=0";
        var queryAuth = new AdapterRequestAuthentication("request_query", session.SessionId, session.PluginInstanceId, 3, session.Token, "",
            new(BridgeProtocol.Version, "corr_query", DateTimeOffset.UtcNow.ToString("O"), "query_execution_once"));
        queryAuth = queryAuth with { Mac = BridgeAdapterCryptography.RequestMac(key, queryAuth, "GET", queryPath, "") };
        using (var altered = Authenticated(execute, queryAuth with { Query = queryAuth.Query! with { CorrelationId = "corr_altered" } }, "", queryPath))
        using (var rejectedQuery = await client.SendAsync(altered))
            Assert.Equal(HttpStatusCode.Unauthorized, rejectedQuery.StatusCode);
        using (var queryRequest = Authenticated(execute, queryAuth, "", queryPath))
        using (var queryReply = await client.SendAsync(queryRequest))
        {
            Assert.Equal(HttpStatusCode.OK, queryReply.StatusCode);
            Assert.True(fixture.Wire.Validate(await queryReply.Content.ReadAsStringAsync(), BridgeEndpoint.Execution, true).IsValid);
        }
        var claim = JsonSerializer.Deserialize<AdapterEnvelope<AdapterClaimResponse>>(result, BridgeProtocol.JsonOptions)!.Body.Claim!;
        const string resourcePath = "/adapter/v1/resources/read";
        var readJson = JsonSerializer.Serialize(new AdapterEnvelope<AdapterResourceRead>(BridgeAdapterProtocol.Version, "resource_read",
            new(claim.ClaimId, claim.ExecutionId, claim.PlanDigest, binding.SessionHandle, 0, 8)), BridgeProtocol.JsonOptions);
        var readAuth = new AdapterRequestAuthentication("request_resource", session.SessionId, session.PluginInstanceId, 4, session.Token, "");
        readAuth = readAuth with { Mac = BridgeAdapterCryptography.RequestMac(key, readAuth, "POST", resourcePath, readJson) };
        using (var readRequest = Authenticated(execute, readAuth, readJson, resourcePath))
        using (var readReply = await client.SendAsync(readRequest))
        {
            Assert.Equal(HttpStatusCode.OK, readReply.StatusCode);
            var bytesJson = await readReply.Content.ReadAsStringAsync();
            Assert.True(adapter.Validate(bytesJson, "resource_chunk").IsValid);
            Assert.True(BridgeAdapterCryptography.FixedEquals(BridgeAdapterCryptography.ResponseMac(key, readAuth, 200, bytesJson),
                readReply.Headers.GetValues("X-Adapter-Mac").Single()));
            var chunk = JsonSerializer.Deserialize<AdapterEnvelope<AdapterResourceChunk>>(bytesJson, BridgeProtocol.JsonOptions)!.Body;
            Assert.Equal(binding.Sha256, chunk.Sha256, ignoreCase: true);
            Assert.Equal(BridgeAdapterCryptography.Encode([56, 66, 80, 83, 0, 1, 0, 0]), chunk.Data);
            Assert.DoesNotContain(resources.Root, bytesJson, StringComparison.Ordinal);
        }
        long nextSequence = 5;
        async Task<string> SendAdapter<T>(string path, string kind, T body, string responseKind)
        {
            var payload = JsonSerializer.Serialize(new AdapterEnvelope<T>(BridgeAdapterProtocol.Version, kind, body), BridgeProtocol.JsonOptions);
            var sequence = nextSequence++;
            var authentication = new AdapterRequestAuthentication($"request_output_{sequence}", session.SessionId,
                session.PluginInstanceId, sequence, session.Token, "");
            authentication = authentication with { Mac = BridgeAdapterCryptography.RequestMac(key, authentication, "POST", path, payload) };
            using var message = Authenticated(execute, authentication, payload, path);
            using var reply = await client.SendAsync(message);
            Assert.Equal(HttpStatusCode.OK, reply.StatusCode);
            var response = await reply.Content.ReadAsStringAsync();
            Assert.True(adapter.Validate(response, responseKind).IsValid);
            Assert.True(BridgeAdapterCryptography.FixedEquals(BridgeAdapterCryptography.ResponseMac(key, authentication, 200, response),
                reply.Headers.GetValues("X-Adapter-Mac").Single()));
            Assert.DoesNotContain(resources.Root, response, StringComparison.Ordinal);
            return response;
        }
        await SendAdapter($"/adapter/v1/claims/{claim.ClaimId}/ack", "claim_ack",
            new AdapterClaimAcknowledgement(claim.ClaimId, claim.ExecutionId, claim.PlanDigest), "claim_response");
        var started = new BridgeEventV1("event_output_started", 2, DateTimeOffset.UtcNow, "page_started", claim.ExecutionId, "running", "page_01");
        await SendAdapter("/adapter/v1/events", "event_report", new AdapterEventReport(claim.ClaimId,
            JsonSerializer.SerializeToElement(new BridgeWireEnvelope<BridgeEventV1>(BridgeProtocol.Version, "event", started), BridgeProtocol.JsonOptions)), "event_receipt");
        // Cross both carriers' frame/body boundaries; these are transport bytes, not a valid rendered JPG.
        var outputBytes = RandomNumberGenerator.GetBytes(BridgeAdapterProtocol.ResourceChunkByteLimit + 7);
        for (var offset = 0; offset < outputBytes.Length;)
        {
            var count = Math.Min(BridgeAdapterProtocol.ResourceChunkByteLimit, outputBytes.Length - offset);
            var chunk = outputBytes.AsSpan(offset, count).ToArray();
            var receiptJson = await SendAdapter("/adapter/v1/outputs/write", "output_write",
                new AdapterOutputWrite(claim.ClaimId, claim.ExecutionId, claim.PlanDigest, output.Binding.OutputRootHandle,
                    output.Binding.ArtifactSetId, "jpg", outputBytes.Length, offset, count,
                    Convert.ToHexStringLower(SHA256.HashData(chunk)), BridgeAdapterCryptography.Encode(chunk)), "output_receipt");
            var receipt = JsonSerializer.Deserialize<AdapterEnvelope<AdapterOutputReceipt>>(receiptJson, BridgeProtocol.JsonOptions)!.Body;
            Assert.Equal(offset + count, receipt.NextOffset);
            Assert.False(receipt.Sealed);
            offset += count;
        }
        var sealedJson = await SendAdapter("/adapter/v1/outputs/seal", "output_seal",
            new AdapterOutputSeal(claim.ClaimId, claim.ExecutionId, claim.PlanDigest, output.Binding.OutputRootHandle,
                output.Binding.ArtifactSetId, "jpg", outputBytes.Length, Convert.ToHexStringLower(SHA256.HashData(outputBytes))), "output_receipt");
        Assert.True(JsonSerializer.Deserialize<AdapterEnvelope<AdapterOutputReceipt>>(sealedJson, BridgeProtocol.JsonOptions)!.Body.Sealed);
        Assert.Equal("running", (await broker.QueryAsync(claim.ExecutionId, CancellationToken.None))!.State);
        Assert.False(Directory.Exists(Path.Combine(resources.Root, "outputs", output.Binding.ArtifactSetId)));
        fixture.Time.Now += TimeSpan.FromSeconds(15);
        await Task.Delay(1500);
        Assert.Equal("unknown", (await broker.QueryAsync(BridgeExecutionBrokerTests.Execution(submitted), CancellationToken.None))!.State);
        using (File.Open(Path.Combine(resources.Root, resources.Sources[0].OwnedRelativePath), FileMode.Open, FileAccess.Write)) { }
        var receivedPath = Assert.Single(Directory.GetFiles(Path.Combine(resources.Root, "outputs"), "*.part", SearchOption.AllDirectories));
        Assert.Equal(outputBytes, await File.ReadAllBytesAsync(receivedPath));
        using (File.Open(receivedPath, FileMode.Open, FileAccess.Write)) { }
        using var occupiedBroker = BridgeExecutionBrokerTests.Create(new BridgeExecutionBrokerTests.MemoryJournal());
        await using var occupied = new BridgeLoopbackHost(fixture.Sessions, occupiedBroker, adapter, fixture.Wire);
        var error = await Assert.ThrowsAsync<IOException>(() => occupied.StartAsync(CancellationToken.None));
        Assert.Equal("BRIDGE_PORT_UNAVAILABLE", error.Message);
    }

    private static StringContent Content(string json) => new(json, Encoding.UTF8, "application/json");

    private static HttpRequestMessage Authenticated(string root, AdapterRequestAuthentication auth, string json, string path = "/adapter/v1/claims")
    {
        var request = new HttpRequestMessage(auth.Query is null ? HttpMethod.Post : HttpMethod.Get, root + path)
            { Content = auth.Query is null ? Content(json) : null };
        request.Headers.Add("Authorization", "Bearer " + auth.Token);
        request.Headers.Add("X-Adapter-Version", BridgeAdapterProtocol.Version);
        request.Headers.Add("X-Bridge-Request-Id", auth.RequestId);
        request.Headers.Add("X-Bridge-Session-Id", auth.SessionId);
        request.Headers.Add("X-Adapter-Instance-Id", auth.PluginInstanceId);
        request.Headers.Add("X-Adapter-Sequence", auth.Sequence.ToString(System.Globalization.CultureInfo.InvariantCulture));
        request.Headers.Add("X-Adapter-Mac", auth.Mac);
        if (auth.Query is not null)
        {
            request.Headers.Add("X-Bridge-Protocol-Version", auth.Query.ProtocolVersion);
            request.Headers.Add("X-Bridge-Correlation-Id", auth.Query.CorrelationId);
            request.Headers.Add("X-Bridge-Sent-At", auth.Query.SentAt);
            request.Headers.Add("X-Bridge-Idempotency-Key", auth.Query.IdempotencyKey);
        }
        return request;
    }
}
