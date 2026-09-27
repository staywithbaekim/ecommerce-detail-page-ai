using System.Text.Json;
using System.Text.Json.Nodes;
using Desktop.Application.Rendering;
using Desktop.Contracts.Bridge;
using Desktop.Contracts.Validation;

namespace Desktop.Application.Tests;

public sealed class BridgePlanSubmissionValidatorTests
{
    private readonly BridgePlanSubmissionValidator _validator = new(
        new BridgeWireValidator(new JsonSchemaContractValidator()),
        new RenderPlanValidator(new JsonSchemaContractValidator()));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AcceptsClosedOrdinaryAndSyntheticDynamicPlans(bool dynamicPage)
    {
        var request = Request(dynamicPage);
        Assert.True(_validator.Validate(request.ToJsonString()).IsValid);
        var payload = request["body"]!["payload"]!;
        payload["plan_digest"] = "sha256:" + payload["plan_digest"]!.GetValue<string>()[7..].ToUpperInvariant();
        Assert.True(_validator.Validate(request.ToJsonString()).IsValid);
    }

    [Theory]
    [InlineData("digest")]
    [InlineData("idempotency")]
    [InlineData("correlation")]
    [InlineData("changed_plan")]
    [InlineData("absolute_output")]
    [InlineData("untrusted_error_member")]
    [InlineData("command")]
    [InlineData("duplicate_resource")]
    [InlineData("ordinary_with_size_table")]
    [InlineData("dynamic_without_size_table")]
    public void RejectsInvalidPlanBeforeAnyPhotoshopOrFileAccess(string mutation)
    {
        var request = Request(mutation == "dynamic_without_size_table");
        var body = request["body"]!;
        var payload = body["payload"]!;
        var plan = payload["render_plan"]!;
        switch (mutation)
        {
            case "digest": payload["plan_digest"] = "sha256:" + new string('f', 64); break;
            case "idempotency": body["idempotency_key"] = "different_page_attempt"; break;
            case "correlation": body["correlation_id"] = "different_correlation"; break;
            case "changed_plan": plan["document"]!["canvas"]!["width"] = 800; break;
            case "absolute_output": plan["output"]!["jpg"]!["file_name"] = @"C:\private\output.jpg"; break;
            // Regression: the wire validator used to recurse into this unknown plan member and throw.
            case "untrusted_error_member": plan["error"] = new JsonObject { ["private"] = "must-not-escape" }; break;
            case "command": plan["photoshop_command"] = "must-not-execute"; break;
            case "duplicate_resource": plan["resources"]!.AsArray().Add(plan["resources"]![0]!.DeepClone()); break;
            case "ordinary_with_size_table": plan["size_table"] = Request(true)["body"]!["payload"]!["render_plan"]!["size_table"]!.DeepClone(); break;
            case "dynamic_without_size_table": plan["size_table"] = null; break;
        }
        var result = _validator.Validate(request.ToJsonString());
        Assert.Equal("BRIDGE_PLAN_INVALID", Assert.Single(result.Errors).Code);
        Assert.DoesNotContain("private", JsonSerializer.Serialize(result), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("extra")]
    [InlineData("wrong_id")]
    [InlineData("duplicate_id")]
    [InlineData("duplicate_handle")]
    [InlineData("hash")]
    [InlineData("length")]
    [InlineData("missing_length")]
    [InlineData("purpose")]
    [InlineData("artifact")]
    [InlineData("output_handle_collision")]
    [InlineData("overwrite_mismatch")]
    [InlineData("unexpected_confirmation")]
    public void RejectsResourceAndOutputBindingsThatDoNotCloseAgainstThePlan(string mutation)
    {
        var request = Request();
        var payload = request["body"]!["payload"]!;
        var bindings = payload["resource_bindings"]!.AsArray();
        var first = bindings[0]!;
        var output = payload["output_bindings"]![0]!;
        switch (mutation)
        {
            case "missing": bindings.RemoveAt(1); break;
            case "extra": bindings.Add(first.DeepClone()); break;
            case "wrong_id": first["resource_id"] = "resource_other"; break;
            case "duplicate_id": bindings[1]!["resource_id"] = first["resource_id"]!.DeepClone(); break;
            case "duplicate_handle": bindings[1]!["session_handle"] = first["session_handle"]!.DeepClone(); break;
            case "hash": first["sha256"] = new string('f', 64); break;
            case "length": first["byte_length"] = 1; break;
            case "missing_length": first.AsObject().Remove("byte_length"); break;
            case "purpose": first["purpose"] = "high_resolution_render_source"; break;
            case "artifact": output["artifact_set_id"] = "other_artifact"; break;
            case "output_handle_collision": output["output_root_handle"] = first["session_handle"]!.DeepClone(); break;
            case "overwrite_mismatch": output["overwrite_policy"] = "confirmed_targets_only"; break;
            case "unexpected_confirmation": output["confirmed_target_ids"] = new JsonArray("unconfirmed_target"); break;
        }
        Assert.Equal("BRIDGE_RESOURCE_BINDING_INVALID", Assert.Single(_validator.Validate(request.ToJsonString()).Errors).Code);
    }

    [Theory]
    [InlineData("analysis_copy")]
    [InlineData("market_size_image")]
    [InlineData("writable")]
    [InlineData("path_handle")]
    public void RejectsForbiddenResourceBindingAtTheWireBoundary(string mutation)
    {
        var request = Request();
        var binding = request["body"]!["payload"]!["resource_bindings"]![0]!;
        if (mutation == "writable") binding["read_only"] = false;
        else if (mutation == "path_handle") binding["session_handle"] = @"C:\private\source.psd";
        else binding["purpose"] = mutation;
        Assert.Equal("BRIDGE_PROTOCOL_MISMATCH", Assert.Single(_validator.Validate(request.ToJsonString()).Errors).Code);
    }

    internal static JsonNode Request(bool dynamicPage = false)
    {
        var plan = JsonNode.Parse(File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory, "Fixtures", "render-plan-v1.valid.example.json")))!;
        if (dynamicPage)
        {
            // Synthetic protocol-only fixture. No market image, raster bytes, or real output claim.
            plan["document"]!["content_type"] = "dynamic_size_table";
            plan["targets"] = new JsonArray();
            plan["resources"]![1]!["role"] = "deterministic_size_table";
            plan["resources"]![1]!["media_type"] = "image/png";
            plan["size_table"] = new JsonObject
            {
                ["size_table_id"] = "size_synthetic", ["grid_digest"] = "sha256:" + plan["resources"]![1]!["sha256"]!.GetValue<string>(),
                ["style_version"] = "size-style-v1", ["layout_version"] = "size-layout-v1",
                ["columns"] = new JsonArray(new JsonObject { ["column_id"] = "size", ["label"] = "尺码", ["index"] = 0 }),
                ["rows"] = new JsonArray(new JsonObject { ["row_id"] = "row_01", ["index"] = 0, ["cells"] = new JsonObject { ["size"] = "/" } }),
                ["render_resource_id"] = plan["resources"]![1]!["resource_id"]!.DeepClone(),
                ["structured_data_digest"] = "sha256:" + new string('c', 64), ["embed_as"] = "smart_object"
            };
            plan["plan_digest"] = RenderPlanDigest.Compute(plan.ToJsonString());
        }
        var resources = plan["resources"]!.AsArray().Select((resource, index) => new BridgeResourceBindingV1(
            resource!["resource_id"]!.GetValue<string>(), new string((char)('A' + index), 43),
            resource["sha256"]!.GetValue<string>(), resource["role"]!.GetValue<string>(), true,
            resource["byte_length"]!.GetValue<long>())).ToArray();
        var payload = new BridgePlanPayloadV1("page", JsonSerializer.SerializeToElement(plan),
            plan["plan_digest"]!.GetValue<string>(), resources,
            [new(plan["output"]!["artifact_set_id"]!.GetValue<string>(), new string('Z', 43), "never", [])]);
        var body = new BridgeRequestBodyV1<BridgePlanPayloadV1>("request_01", plan["correlation_id"]!.GetValue<string>(),
            "session_01", new string('T', 43), DateTimeOffset.UnixEpoch,
            plan["idempotency_key"]!.GetValue<string>(), "submit_plan", payload);
        return JsonSerializer.SerializeToNode(new BridgeWireEnvelope<BridgeRequestBodyV1<BridgePlanPayloadV1>>(
            BridgeProtocol.Version, "request", body), BridgeProtocol.JsonOptions)!;
    }
}
