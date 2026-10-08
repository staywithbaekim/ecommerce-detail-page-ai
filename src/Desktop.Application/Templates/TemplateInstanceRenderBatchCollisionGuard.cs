using System.Collections.Immutable;
using System.Text.Json;

namespace Desktop.Application.Templates;

/// <summary>
/// Cross-plan checks on actual factory output, also usable for inspecting a candidate set.
/// This is not an alternative admission path: it returns diagnostics, never executable plans.
/// Immutable input resources may be shared only with identical descriptors; outputs are exclusive.
/// </summary>
public static class TemplateInstanceRenderBatchCollisionGuard
{
    public static ImmutableArray<TemplateInstanceBatchDiagnostic> Validate(IReadOnlyList<TemplateInstanceBatchPlan> plans)
    {
        ArgumentNullException.ThrowIfNull(plans);
        var claims = new List<(string Kind, string Key, string Value, TemplateInstanceBatchPlan Plan)>();
        foreach (var plan in plans)
        {
            claims.Add(("PLAN_ID", plan.PlanId, "", plan));
            claims.Add(("ARTIFACT_SET_ID", plan.ArtifactSetId, "", plan));
            using var document = JsonDocument.Parse(plan.RenderPlanJson);
            var root = document.RootElement;
            claims.Add(("IDEMPOTENCY_KEY", root.GetProperty("idempotency_key").GetString()!, "", plan));
            foreach (var format in new[] { "jpg", "editable_psd" })
            {
                var output = root.GetProperty("output").GetProperty(format);
                if (output.GetProperty("enabled").GetBoolean())
                    claims.Add(("OUTPUT_NAME", output.GetProperty("file_name").GetString()!.ToUpperInvariant(), "", plan));
            }
            foreach (var resource in root.GetProperty("resources").EnumerateArray())
            {
                var descriptor = JsonSerializer.Serialize(new
                {
                    role = resource.GetProperty("role").GetString(),
                    sha256 = resource.GetProperty("sha256").GetString()!.ToLowerInvariant(),
                    length = resource.GetProperty("byte_length").GetInt64(),
                    mediaType = resource.GetProperty("media_type").GetString()
                });
                claims.Add(("RESOURCE_IDENTITY", resource.GetProperty("resource_id").GetString()!, descriptor, plan));
            }
        }
        return claims.GroupBy(claim => (claim.Kind, claim.Key))
            .Where(group => group.Count() > 1 && (group.Key.Kind != "RESOURCE_IDENTITY"
                || group.Select(claim => claim.Value).Distinct(StringComparer.Ordinal).Skip(1).Any()))
            .SelectMany(group => group.Select(claim => new TemplateInstanceBatchDiagnostic(
                "TPL_INSTANCE_BATCH_" + group.Key.Kind + "_COLLISION", claim.Plan.PageInstanceId,
                claim.Plan.SourcePageId, claim.Plan.RecipeId, claim.Plan.RepetitionIndex)))
            .Distinct().OrderBy(item => item.PageInstanceId, StringComparer.Ordinal)
            .ThenBy(item => item.Code, StringComparer.Ordinal).ToImmutableArray();
    }
}
