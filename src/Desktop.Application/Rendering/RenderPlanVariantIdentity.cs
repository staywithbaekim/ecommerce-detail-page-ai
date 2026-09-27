using System.Text.Json;
using System.Text.Json.Nodes;

namespace Desktop.Application.Rendering;

public static class RenderPlanVariantIdentity
{
    public const string Capability = "output/variant-identity-v1";

    public static string? Read(JsonElement plan) => plan.GetProperty("document")
        .TryGetProperty("output_variant_id", out var variant) ? variant.GetString() : null;

    internal static string AddToNewPlan(string json, string? variantId)
    {
        if (variantId is null) return json;
        var plan = JsonNode.Parse(json)!;
        plan["document"]!["output_variant_id"] = variantId;
        plan["required_capabilities"]!.AsArray().Add(Capability);
        plan["plan_digest"] = RenderPlanDigest.Compute(plan.ToJsonString());
        return plan.ToJsonString();
    }
}
