using System.Text.Json.Nodes;
using Desktop.Contracts.Validation;

namespace Desktop.Contracts.Tests;

public sealed class JsonSchemaContractValidatorTests
{
    private readonly JsonSchemaContractValidator _validator = new();

    [Fact]
    public void AcceptsFrozenRenderPlanExample()
    {
        var result = _validator.Validate(ContractDocumentKind.RenderPlan, LoadValidRenderPlan());

        Assert.True(result.IsValid);
        Assert.Empty(result.Errors);
    }

    [Theory]
    [InlineData("absolute-path")]
    [InlineData("analysis-copy")]
    [InlineData("superseded-paths")]
    [InlineData("photoshop-command")]
    [InlineData("ordinary-page-without-target")]
    public void RejectsForbiddenRenderPlanShape(string mutation)
    {
        var root = JsonNode.Parse(LoadValidRenderPlan())!.AsObject();
        switch (mutation)
        {
            case "absolute-path":
                root["output"]!["jpg"]!["file_name"] = @"C:\outside\result.jpg";
                break;
            case "analysis-copy":
                root["resources"]![1]!["role"] = "analysis_copy";
                break;
            case "superseded-paths":
                root["targets"]![0]!["superseded_paths"] = new JsonArray("old.psd");
                break;
            case "photoshop-command":
                root["photoshop_command"] = "batchPlay(...)";
                break;
            case "ordinary-page-without-target":
                root["targets"] = new JsonArray();
                break;
            default:
                throw new InvalidOperationException("Unknown mutation.");
        }

        var result = _validator.Validate(ContractDocumentKind.RenderPlan, root.ToJsonString());

        Assert.False(result.IsValid);
        var error = Assert.Single(result.Errors);
        Assert.Equal("CONTRACT_SCHEMA_REJECTED", error.Code);
        Assert.False(error.Retryable);
        Assert.Equal("contract", error.Scope);
        Assert.NotEmpty(error.UserAction);
    }

    [Fact]
    public void AcceptsBridgeHandshakeEnvelope()
    {
        const string envelope = """
            {
              "protocol_version": "ps-bridge/v1",
              "envelope_kind": "handshake_request",
              "body": {
                "request_id": "request-0001",
                "correlation_id": "corr-001",
                "sent_at": "2026-08-27T12:00:00Z",
                "session_id": "session-001",
                "nonce": "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA",
                "client": {
                  "app_version": "1.0.0",
                  "protocol_version": "ps-bridge/v1",
                  "requested_capabilities": ["smart-object/replace"]
                },
                "host": {
                  "plugin_manifest_version": "1.0.0",
                  "photoshop_build": "26.8.1"
                },
                "pairing_proof": "AAAAAAAAAAAAAAAA"
              }
            }
            """;

        var result = _validator.Validate(ContractDocumentKind.BridgeEnvelope, envelope);

        Assert.True(result.IsValid);
    }

    [Fact]
    public void InvalidJsonReturnsSanitizedErrorShape()
    {
        var result = _validator.Validate(ContractDocumentKind.BridgeEnvelope, "{not-json");

        var error = Assert.Single(result.Errors);
        Assert.Equal("CONTRACT_INVALID_JSON", error.Code);
        Assert.DoesNotContain("not-json", error.UserAction, StringComparison.Ordinal);
    }

    private static string LoadValidRenderPlan() => File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "render-plan-v1.valid.example.json"));
}
