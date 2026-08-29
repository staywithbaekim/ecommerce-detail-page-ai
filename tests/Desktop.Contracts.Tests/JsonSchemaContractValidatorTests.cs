using System.Text.Json;
using System.Text.Json.Nodes;
using Desktop.Contracts.Ai;
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

    [Fact]
    public void AcceptsFrozenTemplateRevisionManifestExample()
    {
        var result = _validator.Validate(
            ContractDocumentKind.TemplateRevisionManifest,
            LoadValidTemplateRevisionManifest());

        Assert.True(result.IsValid);
        Assert.Empty(result.Errors);
    }

    [Theory]
    [InlineData("unknown-version")]
    [InlineData("absolute-path")]
    [InlineData("path-traversal")]
    [InlineData("missing-structure-signature")]
    [InlineData("capacity-outside-combination")]
    [InlineData("unknown-field")]
    public void RejectsInvalidTemplateRevisionManifest(string mutation)
    {
        var root = JsonNode.Parse(LoadValidTemplateRevisionManifest())!.AsObject();
        switch (mutation)
        {
            case "unknown-version":
                root["schema_version"] = "template-revision/v2";
                break;
            case "absolute-path":
                root["files"]![0]!["relative_path"] = @"C:\customer\template.psd";
                break;
            case "path-traversal":
                root["files"]![0]!["relative_path"] = "../template.psd";
                break;
            case "missing-structure-signature":
                root["pages"]![0]!["frame_bindings"]![0]!["expected_layer"]!
                    .AsObject().Remove("structure_signature");
                break;
            case "capacity-outside-combination":
                root["pages"]![0]!["variant_capacity"] = 4;
                break;
            case "unknown-field":
                root["photoshop_command"] = "batchPlay(...)";
                break;
            default:
                throw new InvalidOperationException("Unknown mutation.");
        }

        var result = _validator.Validate(ContractDocumentKind.TemplateRevisionManifest, root.ToJsonString());

        Assert.False(result.IsValid);
        Assert.Equal("CONTRACT_SCHEMA_REJECTED", Assert.Single(result.Errors).Code);
    }

    [Fact]
    public void AiAnalysisSchemaAcceptsVersionedRequestAndResult()
    {
        Assert.True(_validator.Validate(
            ContractDocumentKind.AiAnalysis,
            LoadValidAiRequest()).IsValid);
        Assert.True(_validator.Validate(
            ContractDocumentKind.AiAnalysis,
            LoadValidAiResult()).IsValid);
    }

    [Fact]
    public void AiAnalysisResultRoundTripsRequestIdAndRequiresIt()
    {
        var result = JsonSerializer.Deserialize<AiAnalysisResultV1>(LoadValidAiResult());

        Assert.NotNull(result);
        Assert.Equal("request_0001", result!.RequestId);
        var serialized = JsonSerializer.Serialize(result);
        Assert.Contains("\"request_id\":\"request_0001\"", serialized, StringComparison.Ordinal);

        var missingRequestId = JsonNode.Parse(LoadValidAiResult())!.AsObject();
        missingRequestId.Remove("request_id");
        Assert.False(_validator.Validate(
            ContractDocumentKind.AiAnalysis,
            missingRequestId.ToJsonString()).IsValid);
    }

    [Theory]
    [InlineData("secret-field")]
    [InlineData("unsupported-analysis-kind")]
    [InlineData("absolute-analysis-copy")]
    [InlineData("out-of-bounds-geometry")]
    [InlineData("raw-provider-response")]
    public void AiAnalysisSchemaRejectsUnsafeShapes(string mutation)
    {
        var root = JsonNode.Parse(LoadValidAiRequest())!.AsObject();
        switch (mutation)
        {
            case "secret-field":
                root["api_key"] = "secret";
                break;
            case "unsupported-analysis-kind":
                root["analysis_kind"] = "front_view";
                break;
            case "absolute-analysis-copy":
                root["assets"]![0]!["analysis_copy_ref"] = @"C:\source.jpg";
                break;
            case "out-of-bounds-geometry":
                root = JsonNode.Parse(LoadValidAiResult())!.AsObject();
                root["subject_geometry"]![0]!["right"] = 1.5;
                break;
            case "raw-provider-response":
                root = JsonNode.Parse(LoadValidAiResult())!.AsObject();
                root["provider_raw_response"] = new JsonObject();
                break;
            default:
                throw new InvalidOperationException("Unknown mutation.");
        }

        var result = _validator.Validate(ContractDocumentKind.AiAnalysis, root.ToJsonString());

        Assert.False(result.IsValid);
        Assert.Equal("CONTRACT_SCHEMA_REJECTED", Assert.Single(result.Errors).Code);
    }

    [Fact]
    public void TemplatePackageSchemaAcceptsV1AndRejectsTraversalOrUnknownVersion()
    {
        var valid = LoadValidTemplatePackageManifest();
        Assert.True(_validator.Validate(ContractDocumentKind.TemplatePackageManifest, valid).IsValid);

        var traversal = JsonNode.Parse(valid)!.AsObject();
        traversal["entries"]![0]!["relativePath"] = "../customer.psd";
        Assert.False(_validator.Validate(
            ContractDocumentKind.TemplatePackageManifest,
            traversal.ToJsonString()).IsValid);

        var version = JsonNode.Parse(valid)!.AsObject();
        version["protocol"] = "template-package/v2";
        Assert.False(_validator.Validate(
            ContractDocumentKind.TemplatePackageManifest,
            version.ToJsonString()).IsValid);

        var missingStructureEvidence = JsonNode.Parse(valid)!.AsObject();
        missingStructureEvidence["pages"]![0]!["inspection"] = new JsonObject();
        Assert.False(_validator.Validate(
            ContractDocumentKind.TemplatePackageManifest,
            missingStructureEvidence.ToJsonString()).IsValid);

        var adapterLeak = JsonNode.Parse(valid)!.AsObject();
        adapterLeak["pages"]![0]!["inspection"]!["photoshopCommand"] = "batchPlay(...)";
        Assert.False(_validator.Validate(
            ContractDocumentKind.TemplatePackageManifest,
            adapterLeak.ToJsonString()).IsValid);
    }

    [Fact]
    public void AssetManifestSchemaAcceptsUnicodeRelativeSources()
    {
        var result = _validator.Validate(ContractDocumentKind.AssetManifest, LoadValidAssetManifest());

        Assert.True(result.IsValid);
        Assert.Empty(result.Errors);
    }

    [Theory]
    [InlineData("unknown-version")]
    [InlineData("absolute-path")]
    [InlineData("path-traversal")]
    [InlineData("missing-hash")]
    [InlineData("missing-dimensions")]
    [InlineData("unknown-field")]
    public void AssetManifestSchemaRejectsUnsafeOrIncompleteSources(string mutation)
    {
        var root = JsonNode.Parse(LoadValidAssetManifest())!.AsObject();
        switch (mutation)
        {
            case "unknown-version":
                root["schema_version"] = "asset-manifest/v2";
                break;
            case "absolute-path":
                root["assets"]![0]!["source_relative_path"] = @"C:\customer\source.jpg";
                break;
            case "path-traversal":
                root["assets"]![0]!["source_relative_path"] = "../source.jpg";
                break;
            case "missing-hash":
                root["assets"]![0]!.AsObject().Remove("sha256");
                break;
            case "missing-dimensions":
                root["assets"]![0]!.AsObject().Remove("pixel_width");
                break;
            case "unknown-field":
                root["source_root"] = @"C:\customer";
                break;
            default:
                throw new InvalidOperationException("Unknown mutation.");
        }

        var result = _validator.Validate(ContractDocumentKind.AssetManifest, root.ToJsonString());

        Assert.False(result.IsValid);
        Assert.Equal("CONTRACT_SCHEMA_REJECTED", Assert.Single(result.Errors).Code);
    }

    private static string LoadValidRenderPlan() => File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "render-plan-v1.valid.example.json"));

    private static string LoadValidTemplateRevisionManifest() => File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "template-revision-v1.valid.example.json"));

    private static string LoadValidTemplatePackageManifest() => File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "template-package-v1.valid.example.json"));

    private static string LoadValidAssetManifest() => File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "asset-manifest-v1.valid.example.json"));

    private static string LoadValidAiRequest() => File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "ai-analysis-v1.valid-request.example.json"));

    private static string LoadValidAiResult() => File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "ai-analysis-v1.valid-result.example.json"));
}
