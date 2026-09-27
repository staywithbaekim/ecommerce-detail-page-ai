using System.Text.Json.Nodes;

namespace Desktop.Application.Rendering;

/// <summary>A new execution identity for the same frozen rendering facts; never changes the original plan.</summary>
public static class RenderPlanRegeneration
{
    public static string Create(string source, DateTimeOffset createdAt)
    {
        var plan = JsonNode.Parse(source)!;
        var identity = Guid.NewGuid().ToString("N");
        plan["plan_id"] = "plan_" + identity;
        plan["idempotency_key"] = "regenerate_" + identity;
        plan["output"]!["artifact_set_id"] = "artifact_" + identity;
        plan["created_at"] = createdAt;
        plan["plan_digest"] = RenderPlanDigest.Compute(plan.ToJsonString());
        return plan.ToJsonString();
    }

    public static bool PreservesFacts(string source, string candidate)
    {
        var before = JsonNode.Parse(source)!.AsObject();
        var after = JsonNode.Parse(candidate)!.AsObject();
        foreach (var key in new[] { "plan_id", "idempotency_key", "plan_digest" })
        {
            if (JsonNode.DeepEquals(before[key], after[key])) return false;
            before.Remove(key); after.Remove(key);
        }
        if (JsonNode.DeepEquals(before["output"]!["artifact_set_id"], after["output"]!["artifact_set_id"])) return false;
        before["output"]!.AsObject().Remove("artifact_set_id"); after["output"]!.AsObject().Remove("artifact_set_id");
        before.Remove("created_at"); after.Remove("created_at");
        return JsonNode.DeepEquals(before, after);
    }
}
