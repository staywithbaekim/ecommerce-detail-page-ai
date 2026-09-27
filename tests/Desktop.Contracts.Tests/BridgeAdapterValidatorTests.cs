using System.Text.Json;
using System.Text.Json.Nodes;
using Desktop.Contracts.Bridge;
using Desktop.Contracts.Validation;

namespace Desktop.Contracts.Tests;

public sealed class BridgeAdapterValidatorTests
{
    private readonly BridgeAdapterValidator _validator = new(new JsonSchemaContractValidator(), new(new JsonSchemaContractValidator()));

    [Theory]
    [InlineData("output_write")]
    [InlineData("output_seal")]
    [InlineData("output_receipt")]
    public void OutputTransportHasStrictBoundedFieldsAndDoesNotExposePaths(string kind)
    {
        object value = kind switch
        {
            "output_write" => new AdapterOutputWrite("claim_test", "execution_test", "sha256:" + new string('a', 64),
                new string('A', 43), "artifact_test", "jpg", 1, 0, 1, new string('b', 64), "AA"),
            "output_seal" => new AdapterOutputSeal("claim_test", "execution_test", "sha256:" + new string('a', 64),
                new string('A', 43), "artifact_test", "jpg", 1, new string('b', 64)),
            _ => new AdapterOutputReceipt("execution_test", "artifact_test", "jpg", 1, true)
        };
        var node = JsonSerializer.SerializeToNode(new AdapterEnvelope<object>(BridgeAdapterProtocol.Version, kind, value), BridgeProtocol.JsonOptions)!;
        Assert.True(_validator.Validate(node.ToJsonString(), kind).IsValid);
        node["body"]!["output_kind"] = "verification_psd";
        Assert.True(_validator.Validate(node.ToJsonString(), kind).IsValid);
        node["body"]!["path"] = "private-path";
        Assert.False(_validator.Validate(node.ToJsonString(), kind).IsValid);
        node["body"]!.AsObject().Remove("path");
        node["body"]!["output_kind"] = "../../other";
        Assert.False(_validator.Validate(node.ToJsonString(), kind).IsValid);
        node["body"]!["output_kind"] = "jpg";
        if (kind == "output_write")
        {
            node["body"]!["count"] = 131073;
            Assert.False(_validator.Validate(node.ToJsonString(), kind).IsValid);
        }
    }

    [Fact]
    public void ChallengeIsSeparateFromFrozenBridgeWire()
    {
        var json = Challenge().ToJsonString();
        Assert.True(_validator.Validate(json, "challenge_request").IsValid);
        Assert.False(new BridgeWireValidator(new JsonSchemaContractValidator()).Validate(json, BridgeEndpoint.Handshake).IsValid);
        Assert.False(_validator.Validate(json, "challenge_response").IsValid);
    }

    [Theory]
    [InlineData("version")]
    [InlineData("plugin")]
    [InlineData("extra")]
    [InlineData("nested_extra")]
    [InlineData("missing_capability")]
    [InlineData("duplicate_capability")]
    [InlineData("missing_nonce")]
    public void RejectsUnnegotiatedChallengeShape(string mutation)
    {
        var node = Challenge();
        var body = node["body"]!;
        switch (mutation)
        {
            case "version": node["transport_version"] = "ps-adapter/v2"; break;
            case "plugin": body["plugin_id"] = "local.ecommerce.uxp-validation"; break;
            case "extra": node["extra"] = true; break;
            case "nested_extra": body["path"] = @"C:\private\source.psd"; break;
            case "missing_capability": body["capabilities"] = new JsonArray("other/capability"); break;
            case "duplicate_capability": body["capabilities"]!.AsArray().Add(BridgeAdapterProtocol.Capability); break;
            case "missing_nonce": body.AsObject().Remove("client_nonce"); break;
        }
        var result = _validator.Validate(node.ToJsonString(), "challenge_request");
        Assert.False(result.IsValid);
        Assert.DoesNotContain("private", JsonSerializer.Serialize(result), StringComparison.Ordinal);
    }

    [Fact]
    public void RejectsDuplicateJsonKeysBeforeInterpretation()
    {
        var json = Challenge().ToJsonString().Replace("\"transport_version\":", "\"transport_version\":\"ps-adapter/v2\",\"transport_version\":", StringComparison.Ordinal);
        Assert.False(_validator.Validate(json, "challenge_request").IsValid);
    }

    [Fact]
    public void EmptyClaimCannotPretendToContainWork()
    {
        var node = JsonSerializer.SerializeToNode(new AdapterEnvelope<AdapterClaimResponse>(
            BridgeAdapterProtocol.Version, "claim_response", new(false)), BridgeProtocol.JsonOptions)!;
        Assert.True(_validator.Validate(node.ToJsonString(), "claim_response").IsValid);
        node["body"]!["has_work"] = true;
        Assert.False(_validator.Validate(node.ToJsonString(), "claim_response").IsValid);
    }

    [Fact]
    public void EventReportsRequireAValidatedWireEvent()
    {
        var value = new BridgeEventV1("event_0001", 1, DateTimeOffset.UnixEpoch, "page_started", "execution_1", "running", "page_1");
        var wire = JsonSerializer.SerializeToElement(new BridgeWireEnvelope<BridgeEventV1>(BridgeProtocol.Version, "event", value), BridgeProtocol.JsonOptions);
        var node = JsonSerializer.SerializeToNode(new AdapterEnvelope<AdapterEventReport>(BridgeAdapterProtocol.Version,
            "event_report", new("claim_1", wire)), BridgeProtocol.JsonOptions)!;
        Assert.True(_validator.Validate(node.ToJsonString(), "event_report").IsValid);
        node["body"]!["event"]!["body"]!["command"] = "must-not-execute";
        Assert.False(_validator.Validate(node.ToJsonString(), "event_report").IsValid);
    }

    private static JsonNode Challenge() => JsonSerializer.SerializeToNode(new AdapterEnvelope<AdapterChallengeRequest>(
        BridgeAdapterProtocol.Version, "challenge_request", new("request_01", new string('A', 43), "instance_1",
            BridgeAdapterProtocol.PluginId, "0.1.0", "26.8.1 test", "uxp_test", [BridgeAdapterProtocol.Capability])),
        BridgeProtocol.JsonOptions)!;

    [Theory]
    [InlineData("valid", true)]
    [InlineData("extra", false)]
    [InlineData("handle", false)]
    [InlineData("negative_offset", false)]
    [InlineData("unsafe_offset", false)]
    [InlineData("oversize", false)]
    [InlineData("zero", false)]
    [InlineData("digest", false)]
    public void ResourceReadsHaveStrictBoundedShape(string mutation, bool valid)
    {
        var node = JsonSerializer.SerializeToNode(new AdapterEnvelope<AdapterResourceRead>(BridgeAdapterProtocol.Version,
            "resource_read", new("claim_1", "execution_1", "sha256:" + new string('a', 64), new string('A', 43), 0, 131072)), BridgeProtocol.JsonOptions)!;
        var body = node["body"]!;
        switch (mutation)
        {
            case "extra": body["path"] = "private/source.psd"; break;
            case "handle": body["session_handle"] = "private/source.psd"; break;
            case "negative_offset": body["offset"] = -1; break;
            case "unsafe_offset": body["offset"] = 9007199254740992; break;
            case "oversize": body["count"] = 131073; break;
            case "zero": body["count"] = 0; break;
            case "digest": body["plan_digest"] = "sha256:unknown"; break;
        }
        Assert.Equal(valid, _validator.Validate(node.ToJsonString(), "resource_read").IsValid);
    }

    [Fact]
    public void LargestResourceChunkFitsStatusResponseLimitWithoutExposingItsDataInToString()
    {
        var chunk = new AdapterResourceChunk("execution_1", "sha256:" + new string('a', 64), new string('A', 43),
            0, 131072, new string('a', 64), new string('A', 174763));
        var json = JsonSerializer.Serialize(new AdapterEnvelope<AdapterResourceChunk>(BridgeAdapterProtocol.Version, "resource_chunk", chunk), BridgeProtocol.JsonOptions);
        Assert.True(_validator.Validate(json, "resource_chunk").IsValid);
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(json) < BridgeProtocol.StatusByteLimit);
        Assert.DoesNotContain(chunk.Data, chunk.ToString(), StringComparison.Ordinal);
        Assert.False(_validator.Validate(json.Replace(chunk.Data, chunk.Data + "A", StringComparison.Ordinal), "resource_chunk").IsValid);
    }
}
