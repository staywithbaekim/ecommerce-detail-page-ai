using System.Text.Json;
using System.Text.Json.Nodes;
using Desktop.Contracts.Bridge;
using Desktop.Contracts.Validation;

namespace Desktop.Contracts.Tests;

public sealed class BridgeWireValidatorTests
{
    private readonly BridgeWireValidator _validator = new(new JsonSchemaContractValidator());
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-19T01:00:00Z", System.Globalization.CultureInfo.InvariantCulture);

    [Fact]
    public void HandshakeDtoMatchesFrozenSchemaWithoutExposingProofInToString()
    {
        var body = Handshake();
        var json = Serialize("handshake_request", body);
        Assert.True(_validator.Validate(json, BridgeEndpoint.Handshake).IsValid);
        Assert.DoesNotContain(body.PairingProof, body.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(body.Nonce, body.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("version")]
    [InlineData("nested_unknown")]
    [InlineData("top_unknown")]
    [InlineData("token_on_handshake")]
    [InlineData("envelope_kind")]
    [InlineData("missing_proof")]
    public void RejectsUnnegotiatedOrAmbiguousHandshake(string mutation)
    {
        var root = JsonNode.Parse(Serialize("handshake_request", Handshake()))!;
        switch (mutation)
        {
            case "version": root["protocol_version"] = "ps-bridge/v2"; break;
            case "nested_unknown": root["body"]!["host"]!["command"] = "untrusted"; break;
            case "top_unknown": root["extra"] = true; break;
            case "token_on_handshake": root["body"]!["token"] = new string('T', 43); break;
            case "envelope_kind": root["envelope_kind"] = "request"; break;
            case "missing_proof": root["body"]!.AsObject().Remove("pairing_proof"); break;
        }
        Assert.False(_validator.Validate(root.ToJsonString(), BridgeEndpoint.Handshake).IsValid);
    }

    [Fact]
    public void RejectsDuplicateJsonKeysEvenWhenSchemaWouldUseTheLastValue()
    {
        var json = Serialize("handshake_request", Handshake());
        json = json.Replace("\"protocol_version\":\"ps-bridge/v1\"",
            "\"protocol_version\":\"ps-bridge/v0\",\"protocol_version\":\"ps-bridge/v1\"", StringComparison.Ordinal);
        Assert.False(_validator.Validate(json, BridgeEndpoint.Handshake).IsValid);
    }

    [Theory]
    [InlineData(BridgeEndpoint.Handshake, false, 1048576)]
    [InlineData(BridgeEndpoint.Plans, false, 8388608)]
    [InlineData(BridgeEndpoint.Plans, true, 1048576)]
    [InlineData(BridgeEndpoint.Heartbeat, false, 262144)]
    [InlineData(BridgeEndpoint.Execution, true, 262144)]
    public void RejectsOversizeBeforeParsingAndCountsUtf8(BridgeEndpoint endpoint, bool response, int limit)
    {
        Assert.Equal(limit, BridgeWireValidator.ByteLimit(endpoint, response));
        var result = _validator.Validate(new string('中', limit / 3 + 1), endpoint, response);
        Assert.Equal("BRIDGE_REQUEST_TOO_LARGE", Assert.Single(result.Errors).Code);
    }

    [Fact]
    public void AcceptsPairedAndRejectsContradictorySecretBearingRejection()
    {
        var result = new BridgeHandshakeResultV1(true, "request_01", "corr_01", "paired", Now,
            "session_01", 50000, new string('T', 43), Now.AddMinutes(15), []);
        Assert.True(_validator.Validate(Serialize("handshake_response", result), BridgeEndpoint.Handshake, true).IsValid);
        Assert.DoesNotContain(result.Token!, result.ToString(), StringComparison.Ordinal);
        var rejected = result with { Ok = false, Status = "rejected", Error = BridgeWireErrors.Create("BRIDGE_PAIRING_REJECTED") };
        Assert.False(_validator.Validate(Serialize("handshake_response", rejected), BridgeEndpoint.Handshake, true).IsValid);
    }

    [Theory]
    [InlineData("path")]
    [InlineData("raw_message")]
    [InlineData("retryable")]
    public void ErrorsDoNotEchoUntrustedDetails(string mutation)
    {
        var error = BridgeWireErrors.Create("BRIDGE_PROTOCOL_MISMATCH");
        var response = new BridgeHandshakeResultV1(false, "request_01", "corr_01", "rejected", Now, Error: error);
        var json = Serialize("handshake_response", response);
        Assert.True(_validator.Validate(json, BridgeEndpoint.Handshake, true).IsValid);
        var root = JsonNode.Parse(json)!;
        if (mutation == "path") root["body"]!["error"]!["details"] = new JsonObject { ["actual"] = "C:\\private\\secret.psd" };
        if (mutation == "raw_message") root["body"]!["error"]!["user_action"] = "C:\\private\\secret.psd";
        if (mutation == "retryable") root["body"]!["error"]!["retryable"] = true;
        var validation = _validator.Validate(root.ToJsonString(), BridgeEndpoint.Handshake, true);
        Assert.False(validation.IsValid);
        Assert.DoesNotContain("private", JsonSerializer.Serialize(validation), StringComparison.Ordinal);
    }

    [Fact]
    public void RequestCannotBeRoutedToAnotherEndpoint()
    {
        var body = new BridgeRequestBodyV1<BridgeEmptyPayloadV1>("request_01", "corr_01", "session_01",
            new string('T', 43), Now, "release_01", "release", new());
        var json = Serialize("request", body);
        Assert.True(_validator.Validate(json, BridgeEndpoint.Release).IsValid);
        Assert.False(_validator.Validate(json, BridgeEndpoint.Heartbeat).IsValid);
    }

    [Theory]
    [InlineData("handshake_response")]
    [InlineData("response")]
    public void RejectedResponsesAreValidEvenThoughTheirBodyShapesOverlap(string kind)
    {
        var body = new BridgeHandshakeResultV1(false, "request_01", "corr_01", "rejected", Now,
            Error: BridgeWireErrors.Create("BRIDGE_PROTOCOL_MISMATCH"));
        var json = Serialize(kind, body);
        Assert.True(new JsonSchemaContractValidator().Validate(ContractDocumentKind.BridgeEnvelope, json).IsValid);
        Assert.True(_validator.Validate(json,
            kind == "handshake_response" ? BridgeEndpoint.Handshake : BridgeEndpoint.Plans, true).IsValid);
    }

    [Theory]
    [InlineData("handshake_request")]
    [InlineData("request")]
    [InlineData("event")]
    public void BodyUnionStillRequiresTheSchemaSelectedByEnvelopeKind(string kind)
    {
        var body = new BridgeHandshakeResultV1(false, "request_01", "corr_01", "rejected", Now,
            Error: BridgeWireErrors.Create("BRIDGE_PAIRING_REJECTED"));
        Assert.False(new JsonSchemaContractValidator().Validate(
            ContractDocumentKind.BridgeEnvelope, Serialize(kind, body)).IsValid);
    }

    [Theory]
    [InlineData(BridgeEndpoint.Plans)]
    [InlineData(BridgeEndpoint.Execution)]
    [InlineData(BridgeEndpoint.Cancel)]
    public void ExecutionResponseRequiresIdentityAndOrderedEvents(BridgeEndpoint endpoint)
    {
        var response = ExecutionResponse();
        Assert.True(_validator.Validate(Serialize("response", response), endpoint, true).IsValid);
        Assert.False(_validator.Validate(Serialize("response", response with { ExecutionId = null }), endpoint, true).IsValid);
        Assert.False(_validator.Validate(Serialize("response", response with { EventSeq = 1 }), endpoint, true).IsValid);
        Assert.False(_validator.Validate(Serialize("response", response with { ExecutionStatus = null }), endpoint, true).IsValid);
    }

    [Theory]
    [InlineData("duplicate_sequence")]
    [InlineData("reverse_sequence")]
    [InlineData("duplicate_event")]
    [InlineData("other_execution")]
    [InlineData("untrusted_event_error")]
    [InlineData("success_with_error")]
    public void RejectsContradictoryOrUntrustedExecutionResponses(string mutation)
    {
        var root = JsonNode.Parse(Serialize("response", ExecutionResponse()))!;
        var body = root["body"]!;
        var second = body["events"]![1]!;
        switch (mutation)
        {
            case "duplicate_sequence": second["event_seq"] = 1; break;
            case "reverse_sequence": body["events"]![0]!["event_seq"] = 2; second["event_seq"] = 1; break;
            case "duplicate_event": second["event_id"] = "event_01"; break;
            case "other_execution": second["execution_id"] = "execution_other"; break;
            case "untrusted_event_error":
                second["error"] = JsonSerializer.SerializeToNode(BridgeWireErrors.Create("BRIDGE_HOST_BLOCKED"), BridgeProtocol.JsonOptions);
                second["error"]!["user_action"] = @"C:\private\source.psd";
                break;
            case "success_with_error":
                body["error"] = JsonSerializer.SerializeToNode(BridgeWireErrors.Create("BRIDGE_BUSY"), BridgeProtocol.JsonOptions);
                break;
        }
        Assert.False(_validator.Validate(root.ToJsonString(), BridgeEndpoint.Execution, true).IsValid);
    }

    [Fact]
    public void EventEnvelopeIsValidatedWithoutReadingNonexistentResponseFields()
    {
        var json = Serialize("event", ExecutionResponse().Events![0]);
        Assert.True(_validator.Validate(json, BridgeEndpoint.Event).IsValid);
        Assert.False(_validator.Validate(json, BridgeEndpoint.Event, response: true).IsValid);
    }

    [Fact]
    public void ProtocolVersionDetailsAreAllowedButOtherErrorDetailsAreNot()
    {
        var error = BridgeWireErrors.Create("BRIDGE_PROTOCOL_MISMATCH") with
        {
            Details = new Dictionary<string, string> { ["expected"] = "ps-bridge/v1", ["actual"] = "ps-bridge/v0" }
        };
        var body = new BridgeHandshakeResultV1(false, "request_01", "corr_01", "rejected", Now, Error: error);
        Assert.True(_validator.Validate(Serialize("handshake_response", body), BridgeEndpoint.Handshake, true).IsValid);
        Assert.False(_validator.Validate(Serialize("handshake_response", body with
        {
            Error = error with { Details = new Dictionary<string, string> { ["unknown"] = "ps-bridge/v1" } }
        }), BridgeEndpoint.Handshake, true).IsValid);
    }

    private static BridgeResponseBodyV1 ExecutionResponse() => new(true, "request_01", "corr_01", "ok", Now,
        "execution_01", "running", 2,
        [new("event_01", 1, Now, "execution_accepted", "execution_01", "accepted"),
         new("event_02", 2, Now.AddSeconds(1), "page_started", "execution_01", "running", "page_01")]);

    private static BridgeHandshakeBodyV1 Handshake() => new("request_01", "corr_01", Now,
        "session_01", new string('N', 43), new("1.0.0", BridgeProtocol.Version),
        new("0.1.0", "26.8.1 20250624.r.8 5e0d05d"), new string('P', 43));

    private static string Serialize<T>(string kind, T body) => JsonSerializer.Serialize(
        new BridgeWireEnvelope<T>(BridgeProtocol.Version, kind, body), BridgeProtocol.JsonOptions);
}
