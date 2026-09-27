using System.Text.Json;
using System.Text.Json.Nodes;
using Desktop.Application.Rendering;
using Desktop.Contracts.Bridge;
using Desktop.Contracts.Validation;
using Desktop.Infrastructure.Bridge;

namespace Desktop.Infrastructure.Tests;

public sealed partial class BridgeExecutionBrokerTests : IDisposable
{
    private readonly List<BridgeResourceLeaseTests.Fixture> _resources = [];
    [Theory]
    [InlineData("{")]
    [InlineData("{}")]
    [InlineData("null")]
    public async Task MalformedSubmissionDoesNotCreateAnExecution(string json)
    {
        var store = new MemoryJournal();
        using var broker = Create(store);
        await broker.InitializeAsync(CancellationToken.None);
        Assert.Equal(BridgePlanDelivery.NotSent, (await broker.SubmitAsync(json, CancellationToken.None)).Delivery);
        Assert.Null(store.Value);
    }

    [Fact]
    public async Task DuplicatePlanAndClaimCannotCreateASecondExecution()
    {
        var store = new MemoryJournal();
        using var broker = Create(store);
        await broker.InitializeAsync(CancellationToken.None);
        var request = await Prepare(broker);
        var first = await broker.SubmitAsync(request, CancellationToken.None);
        var second = await broker.SubmitAsync(request, CancellationToken.None);
        Assert.Equal(Execution(first), Execution(second));
        var claim = (await broker.ClaimAsync("session_test", CancellationToken.None)).Value!.Claim!;
        Assert.Equal("claimed", claim.State);
        Assert.NotNull(claim.Plan);
        Assert.Equal(claim.ClaimId, (await broker.ClaimAsync("session_test", CancellationToken.None)).Value!.Claim!.ClaimId);
        var ack = new AdapterClaimAcknowledgement(claim.ClaimId, claim.ExecutionId, claim.PlanDigest);
        var accepted = (await broker.AcknowledgeAsync("session_test", ack, CancellationToken.None)).Value!.Claim!;
        Assert.Equal("authorized", accepted.State);
        Assert.Null(accepted.Plan);
        Assert.Equal("authorized", (await broker.AcknowledgeAsync("session_test", ack, CancellationToken.None)).Value!.Claim!.State);
        Assert.Single(store.Value!.Executions);
        var journal = JsonSerializer.Serialize(store.Value);
        Assert.DoesNotContain(new string('T', 43), journal, StringComparison.Ordinal);
        Assert.DoesNotContain(new string('Z', 43), journal, StringComparison.Ordinal);
        Assert.DoesNotContain("render_plan", journal, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("accepted")]
    [InlineData("claimed")]
    [InlineData("authorized")]
    public async Task RestartMarksPossiblyDeliveredPlanUnknownAndNeverRequeues(string stage)
    {
        var store = new MemoryJournal();
        using var first = Create(store);
        await first.InitializeAsync(CancellationToken.None);
        var request = await Prepare(first);
        var id = Execution(await first.SubmitAsync(request, CancellationToken.None));
        if (stage != "accepted")
        {
            var claim = (await first.ClaimAsync("session_test", CancellationToken.None)).Value!.Claim!;
            if (stage == "authorized") await first.AcknowledgeAsync("session_test",
                new(claim.ClaimId, id, claim.PlanDigest), CancellationToken.None);
        }
        using var restarted = Create(store);
        await restarted.InitializeAsync(CancellationToken.None);
        Assert.Equal("unknown", (await restarted.QueryAsync(id, CancellationToken.None))!.State);
        Assert.Equal("BRIDGE_EXECUTION_UNKNOWN", (await restarted.ClaimAsync("session_test", CancellationToken.None)).Error!.Code);
        var replay = await restarted.SubmitAsync(request, CancellationToken.None);
        Assert.Equal(id, Execution(replay));
        using var response = JsonDocument.Parse(replay.ResponseJson!);
        Assert.Equal("unknown", response.RootElement.GetProperty("body").GetProperty("execution_status").GetString());
    }

    [Fact]
    public async Task WrongSessionAckDigestAndSecondPlanAreRejected()
    {
        var store = new MemoryJournal();
        using var broker = Create(store);
        await broker.InitializeAsync(CancellationToken.None);
        var request = await Prepare(broker);
        await broker.SubmitAsync(request, CancellationToken.None);
        Assert.False((await broker.ClaimAsync("another_session", CancellationToken.None)).IsSuccess);
        var claim = (await broker.ClaimAsync("session_test", CancellationToken.None)).Value!.Claim!;
        var badAck = await broker.AcknowledgeAsync("session_test",
            new(claim.ClaimId, claim.ExecutionId, "sha256:" + new string('f', 64)), CancellationToken.None);
        Assert.False(badAck.IsSuccess);
        var node = JsonNode.Parse(request)!;
        var plan = node["body"]!["payload"]!["render_plan"]!;
        node["body"]!["idempotency_key"] = "attempt_second";
        plan["idempotency_key"] = "attempt_second";
        plan["plan_id"] = "plan_second";
        plan["plan_digest"] = RenderPlanDigest.Compute(plan.ToJsonString());
        node["body"]!["payload"]!["plan_digest"] = plan["plan_digest"]!.DeepClone();
        var result = await broker.SubmitAsync(node.ToJsonString(), CancellationToken.None);
        using var response = JsonDocument.Parse(result.ResponseJson!);
        Assert.Equal("BRIDGE_BUSY", response.RootElement.GetProperty("body").GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task EventsAreOrderedDeduplicatedAndCannotForgeCompletion()
    {
        var store = new MemoryJournal();
        using var broker = Create(store);
        await broker.InitializeAsync(CancellationToken.None);
        await broker.SubmitAsync(await Prepare(broker), CancellationToken.None);
        var claim = (await broker.ClaimAsync("session_test", CancellationToken.None)).Value!.Claim!;
        await broker.AcknowledgeAsync("session_test", new(claim.ClaimId, claim.ExecutionId, claim.PlanDigest), CancellationToken.None);
        var start = Report(claim, 2, "page_started", "running", "page_01");
        Assert.True((await broker.ReportAsync("session_test", start, CancellationToken.None)).IsSuccess);
        Assert.True((await broker.ReportAsync("session_test", start, CancellationToken.None)).IsSuccess);
        Assert.False((await broker.ReportAsync("session_test", Report(claim, 4, "execution_completed", "completed"), CancellationToken.None)).IsSuccess);
        Assert.False((await broker.ReportAsync("session_test", Report(claim, 3, "execution_completed", "completed"), CancellationToken.None)).IsSuccess);
        Assert.False((await broker.ReportAsync("session_test", Report(claim, 3, "page_completed", "running", "another_page"), CancellationToken.None)).IsSuccess);
        Assert.True((await broker.ReportAsync("session_test", Report(claim, 3, "page_completed", "running", "page_01"), CancellationToken.None)).IsSuccess);
        Assert.True((await broker.ReportAsync("session_test", Report(claim, 4, "execution_completed", "completed"), CancellationToken.None)).IsSuccess);
        Assert.Equal(4, (await broker.QueryAsync(claim.ExecutionId, CancellationToken.None))!.EventSeq);
        Assert.False((await broker.ClaimAsync("session_test", CancellationToken.None)).Value!.HasWork);
    }

    [Fact]
    public async Task CancelBeforeAckPreventsExecutionAndFailureIsDurable()
    {
        var store = new MemoryJournal();
        using var broker = Create(store);
        await broker.InitializeAsync(CancellationToken.None);
        var id = Execution(await broker.SubmitAsync(await Prepare(broker), CancellationToken.None));
        var claim = (await broker.ClaimAsync("session_test", CancellationToken.None)).Value!.Claim!;
        await broker.CancelAsync(id, CancellationToken.None);
        var ack = (await broker.AcknowledgeAsync("session_test", new(claim.ClaimId, id, claim.PlanDigest), CancellationToken.None)).Value!.Claim!;
        Assert.Equal("claimed", ack.State);
        Assert.True(ack.CancelRequested);
        Assert.False((await broker.ReportAsync("session_test", Report(claim, 2, "page_started", "running", "page_01"), CancellationToken.None)).IsSuccess);
        Assert.True((await broker.ReportAsync("session_test", Report(claim, 2, "execution_cancelled", "cancelled"), CancellationToken.None)).IsSuccess);
        using var restarted = Create(store);
        await restarted.InitializeAsync(CancellationToken.None);
        Assert.Equal("cancelled", (await restarted.QueryAsync(id, CancellationToken.None))!.State);
    }

    [Fact]
    public async Task UncertainJournalWritePoisonsBrokerWithoutDelivery()
    {
        var store = new MemoryJournal { FailWrites = true };
        using var broker = Create(store);
        await broker.InitializeAsync(CancellationToken.None);
        var reply = await broker.SubmitAsync(await Prepare(broker), CancellationToken.None);
        Assert.Equal(BridgePlanDelivery.Uncertain, reply.Delivery);
        await Assert.ThrowsAsync<InvalidOperationException>(() => broker.ClaimAsync("session_test", CancellationToken.None));
        AssertSourcesUnlocked();
    }

    internal static BridgeExecutionBroker Create(IBridgeExecutionJournalStore journal)
    {
        var wire = new BridgeWireValidator(new JsonSchemaContractValidator());
        return new("batch_01JEXAMPLE", new(wire, new RenderPlanValidator(new JsonSchemaContractValidator())), wire, journal);
    }

    [Fact]
    public async Task WireValidBindingsWithoutOpenedResourcesAreRejectedBeforeAcceptance()
    {
        var store = new MemoryJournal();
        using var broker = Create(store);
        await broker.InitializeAsync(CancellationToken.None);
        var reply = await broker.SubmitAsync(Request(), CancellationToken.None);
        using var response = JsonDocument.Parse(reply.ResponseJson!);
        Assert.Equal("BRIDGE_RESOURCE_BINDING_INVALID", response.RootElement.GetProperty("body").GetProperty("error").GetProperty("code").GetString());
        Assert.Null(store.Value);
        var valid = await Prepare(broker);
        var altered = JsonNode.Parse(valid)!;
        altered["body"]!["payload"]!["resource_bindings"]![0]!["session_handle"] = new string('X', 43);
        reply = await broker.SubmitAsync(altered.ToJsonString(), CancellationToken.None);
        Assert.Contains("BRIDGE_RESOURCE_BINDING_INVALID", reply.ResponseJson!, StringComparison.Ordinal);
        Assert.Null(store.Value);
        Assert.NotNull(Execution(await broker.SubmitAsync(valid, CancellationToken.None)));
    }

    [Theory]
    [InlineData("session")]
    [InlineData("execution")]
    [InlineData("claim")]
    [InlineData("digest")]
    [InlineData("handle")]
    [InlineData("offset")]
    [InlineData("count")]
    [InlineData("range")]
    public async Task ReadsAreBoundToExactlyOneDeliveredResource(string mutation)
    {
        using var broker = Create(new MemoryJournal());
        await broker.InitializeAsync(CancellationToken.None);
        var request = await Prepare(broker);
        await broker.SubmitAsync(request, CancellationToken.None);
        var claim = (await broker.ClaimAsync("session_test", CancellationToken.None)).Value!.Claim!;
        var handle = JsonNode.Parse(request)!["body"]!["payload"]!["resource_bindings"]![0]!["session_handle"]!.GetValue<string>();
        var read = new AdapterResourceRead(claim.ClaimId, claim.ExecutionId, claim.PlanDigest, handle, 0, 8);
        var valid = (await broker.ReadResourceAsync("session_test", read, CancellationToken.None)).Value!;
        Assert.Equal(BridgeAdapterCryptography.Encode([56, 66, 80, 83, 0, 1, 0, 0]), valid.Data);
        read = mutation switch
        {
            "execution" => read with { ExecutionId = "execution_other" },
            "claim" => read with { ClaimId = "claim_other" },
            "digest" => read with { PlanDigest = "sha256:" + new string('0', 64) },
            "handle" => read with { SessionHandle = new string('X', 43) },
            "offset" => read with { Offset = -1 },
            "count" => read with { Count = BridgeAdapterProtocol.ResourceChunkByteLimit + 1 },
            "range" => read with { Offset = 1 },
            _ => read
        };
        Assert.Equal("BRIDGE_RESOURCE_BINDING_INVALID", (await broker.ReadResourceAsync(
            mutation == "session" ? "session_other" : "session_test", read, CancellationToken.None)).Error!.Code);
    }

    [Theory]
    [InlineData("cancel")]
    [InlineData("disconnect")]
    [InlineData("completed")]
    [InlineData("dispose")]
    [InlineData("journal_failure")]
    public async Task ResourceOwnershipEndsWithExecutionOrTransport(string ending)
    {
        var store = new MemoryJournal();
        using var broker = Create(store);
        await broker.InitializeAsync(CancellationToken.None);
        await broker.SubmitAsync(await Prepare(broker), CancellationToken.None);
        var before = _resources[0].Hashes();
        var claim = (await broker.ClaimAsync("session_test", CancellationToken.None)).Value!.Claim!;
        if (ending == "cancel") await broker.CancelAsync(claim.ExecutionId, CancellationToken.None);
        if (ending == "disconnect") await broker.DisconnectAsync(CancellationToken.None);
        if (ending == "dispose") broker.Dispose();
        if (ending == "completed")
        {
            await broker.AcknowledgeAsync("session_test", new(claim.ClaimId, claim.ExecutionId, claim.PlanDigest), CancellationToken.None);
            await broker.ReportAsync("session_test", Report(claim, 2, "page_started", "running", "page_01"), CancellationToken.None);
            await broker.ReportAsync("session_test", Report(claim, 3, "page_completed", "running", "page_01"), CancellationToken.None);
            await broker.ReportAsync("session_test", Report(claim, 4, "execution_completed", "completed"), CancellationToken.None);
        }
        if (ending == "journal_failure")
        {
            store.FailWrites = true;
            await Assert.ThrowsAsync<IOException>(() => broker.CancelAsync(claim.ExecutionId, CancellationToken.None));
        }
        AssertSourcesUnlocked();
        Assert.Equal(before, _resources[0].Hashes());
        if (ending is not ("dispose" or "journal_failure"))
        {
            var handle = claim.Plan!.Value.GetProperty("body").GetProperty("payload").GetProperty("resource_bindings")[0].GetProperty("session_handle").GetString()!;
            Assert.False((await broker.ReadResourceAsync("session_test", new(claim.ClaimId, claim.ExecutionId,
                claim.PlanDigest, handle, 0, 1), CancellationToken.None)).IsSuccess);
        }
    }

    [Theory]
    [InlineData("valid")]
    [InlineData("session")]
    [InlineData("claim")]
    [InlineData("digest")]
    [InlineData("handle")]
    [InlineData("range")]
    public async Task CancelledResourceReadOnlySignalsPendingForItsBoundRequest(string mutation)
    {
        using var broker = Create(new MemoryJournal());
        await broker.InitializeAsync(CancellationToken.None);
        var request = await Prepare(broker);
        await broker.SubmitAsync(request, CancellationToken.None);
        var claim = (await broker.ClaimAsync("session_test", CancellationToken.None)).Value!.Claim!;
        await broker.AcknowledgeAsync("session_test", new(claim.ClaimId, claim.ExecutionId, claim.PlanDigest), CancellationToken.None);
        var handle = JsonNode.Parse(request)!["body"]!["payload"]!["resource_bindings"]![0]!["session_handle"]!.GetValue<string>();
        var read = new AdapterResourceRead(claim.ClaimId, claim.ExecutionId, claim.PlanDigest, handle, 0, 8);
        await broker.CancelAsync(claim.ExecutionId, CancellationToken.None);
        AssertSourcesUnlocked();
        read = mutation switch
        {
            "claim" => read with { ClaimId = "claim_other" },
            "digest" => read with { PlanDigest = "sha256:" + new string('0', 64) },
            "handle" => read with { SessionHandle = new string('X', 43) },
            "range" => read with { Offset = 1 },
            _ => read
        };
        var result = await broker.ReadResourceAsync(mutation == "session" ? "session_other" : "session_test", read, CancellationToken.None);
        Assert.False(result.IsSuccess);
        Assert.Null(result.Value);
        Assert.Equal(mutation == "valid" ? "BRIDGE_CANCEL_PENDING" : "BRIDGE_RESOURCE_BINDING_INVALID", result.Error!.Code);
        Assert.Equal("authorized", (await broker.QueryAsync(claim.ExecutionId, CancellationToken.None))!.State);
        Assert.True((await broker.ReportAsync("session_test", Report(claim, 2, "execution_cancelled", "cancelled"), CancellationToken.None)).IsSuccess);
        Assert.Equal("cancelled", (await broker.QueryAsync(claim.ExecutionId, CancellationToken.None))!.State);
    }

    private async Task<string> Prepare(BridgeExecutionBroker broker)
    {
        var fixture = new BridgeResourceLeaseTests.Fixture();
        _resources.Add(fixture);
        var lease = await fixture.Open();
        await broker.RegisterResourcesAsync(lease, CancellationToken.None);
        return Request(plan: fixture.Plan, bindings: lease.Bindings);
    }

    internal static string Request(string session = "session_test", string? token = null,
        JsonNode? plan = null, IReadOnlyList<BridgeResourceBindingV1>? bindings = null)
    {
        plan ??= JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "render-plan-v1.valid.example.json")))!;
        bindings ??= plan["resources"]!.AsArray().Select((r, i) => new BridgeResourceBindingV1(r!["resource_id"]!.GetValue<string>(),
            new string((char)('A' + i), 43), r["sha256"]!.GetValue<string>(), r["role"]!.GetValue<string>(), true, r["byte_length"]!.GetValue<long>())).ToArray();
        var payload = new BridgePlanPayloadV1("page", JsonSerializer.SerializeToElement(plan), plan["plan_digest"]!.GetValue<string>(),
            bindings, [new(plan["output"]!["artifact_set_id"]!.GetValue<string>(), new string('Z', 43), "never", [])]);
        var body = new BridgeRequestBodyV1<BridgePlanPayloadV1>("request_plan", "corr_01JEXAMPLE", session, token ?? new string('T', 43),
            DateTimeOffset.UtcNow, plan["idempotency_key"]!.GetValue<string>(), "submit_plan", payload);
        return JsonSerializer.Serialize(new BridgeWireEnvelope<BridgeRequestBodyV1<BridgePlanPayloadV1>>(BridgeProtocol.Version, "request", body), BridgeProtocol.JsonOptions);
    }

    internal static string Execution(BridgePlanDeliveryResult reply) => JsonDocument.Parse(reply.ResponseJson!).RootElement.GetProperty("body").GetProperty("execution_id").GetString()!;

    private static AdapterEventReport Report(AdapterClaim claim, int sequence, string type, string state, string? page = null)
    {
        var item = new BridgeEventV1($"event_{claim.ExecutionId}_{sequence}", sequence, DateTimeOffset.UnixEpoch, type, claim.ExecutionId, state, page);
        return new(claim.ClaimId, JsonSerializer.SerializeToElement(new BridgeWireEnvelope<BridgeEventV1>(BridgeProtocol.Version, "event", item), BridgeProtocol.JsonOptions));
    }

    private void AssertSourcesUnlocked()
    {
        foreach (var fixture in _resources)
            foreach (var source in fixture.Sources)
                using (File.Open(Path.Combine(fixture.Root, source.OwnedRelativePath), FileMode.Open, FileAccess.ReadWrite)) { }
    }

    public void Dispose()
    {
        foreach (var fixture in _resources) fixture.Dispose();
    }

    internal sealed class MemoryJournal : IBridgeExecutionJournalStore
    {
        public BridgeExecutionJournal? Value { get; private set; }
        public bool FailWrites { get; set; }
        public Task<BridgeExecutionJournal?> LoadAsync(CancellationToken cancellationToken) => Task.FromResult(Value);
        public Task SaveAsync(BridgeExecutionJournal journal, CancellationToken cancellationToken)
        {
            if (FailWrites) throw new IOException("simulated write failure");
            Value = JsonSerializer.Deserialize<BridgeExecutionJournal>(JsonSerializer.Serialize(journal));
            return Task.CompletedTask;
        }
    }
}
