using System.Text.Json;
using System.Text.Json.Nodes;
using Desktop.Application.Psd;
using Desktop.Application.SizeTables;

namespace Desktop.Application.Rendering;

/// <summary>Absolute, axis-aligned placement of the entire source image. No renderer-specific commands.</summary>
public sealed record RenderTargetPlacement(int SourceWidth, int SourceHeight, decimal Left, decimal Top, decimal Width, decimal Height)
{
    public string CoordinateSpace { get; } = "document_pixels";
    public string Clipping { get; } = "preserve_template_masks_and_canvas";
}

/// <summary>Explicit migration requires resolved geometry and a new attempt; it never interprets a legacy intent string.</summary>
public sealed class RenderPlanPlacementMigration(IRenderPlanValidator validator)
{
    public const string Capability = "layout/absolute-placement-v1";
    public const string SizeTableCapability = "size-table/calibrated-target-v1";
    public const string SizeInsertionCapability = "size-table/insert-layer-v1";
    private static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    public string Migrate(string sourceJson, IReadOnlyDictionary<string, RenderTargetPlacement> placements,
        string planId, string idempotencyKey, string artifactSetId, DateTimeOffset createdAt)
    {
        if (!validator.Validate(sourceJson).IsValid) throw Invalid();
        var plan = JsonNode.Parse(sourceJson)!;
        var targets = plan["targets"]!.AsArray();
        if (targets.Count == 0 || placements.Count != targets.Count
            || planId == plan["plan_id"]!.GetValue<string>()
            || idempotencyKey == plan["idempotency_key"]!.GetValue<string>()
            || artifactSetId == plan["output"]!["artifact_set_id"]!.GetValue<string>()) throw Invalid();
        foreach (var target in targets)
        {
            if (!placements.TryGetValue(target!["frame_id"]!.GetValue<string>(), out var placement)
                || target["layout"]!["placement"] is not null) throw Invalid();
            target["layout"]!["placement"] = JsonSerializer.SerializeToNode(placement, Options);
        }
        var capabilities = plan["required_capabilities"]!.AsArray();
        if (!capabilities.Any(item => item!.GetValue<string>() == Capability)) capabilities.Add(Capability);
        plan["plan_id"] = planId;
        plan["idempotency_key"] = idempotencyKey;
        plan["output"]!["artifact_set_id"] = artifactSetId;
        plan["created_at"] = createdAt;
        plan["plan_digest"] = RenderPlanDigest.Compute(plan.ToJsonString());
        var migrated = plan.ToJsonString();
        if (!validator.Validate(migrated).IsValid) throw Invalid();
        return migrated;
    }

    public static bool IsExplicit(JsonElement plan) =>
        plan.GetProperty("required_capabilities").EnumerateArray().Any(item => item.GetString() == Capability)
        && (HasSizeInsertion(plan) || plan.GetProperty("targets").GetArrayLength() > 0
        && plan.GetProperty("targets").EnumerateArray().All(target => target.GetProperty("layout").TryGetProperty("placement", out _)));

    public static bool HasSizeInsertion(JsonElement plan) =>
        plan.GetProperty("required_capabilities").EnumerateArray().Any(item => item.GetString() == SizeInsertionCapability)
        && plan.GetProperty("size_table").ValueKind == JsonValueKind.Object
        && plan.GetProperty("size_table").TryGetProperty("insertion", out _);

    public string MigrateSizeTableInsertion(string sourceJson, SizeTableInsertionTarget target, PsdInspectionDocument inspection,
        string planId, string idempotencyKey, string artifactSetId, DateTimeOffset createdAt)
    {
        if (!validator.Validate(sourceJson).IsValid) throw Invalid();
        var plan = JsonNode.Parse(sourceJson)!;
        if (plan["document"]!["content_type"]!.GetValue<string>() != "dynamic_size_table"
            || plan["targets"]!.AsArray().Count != 0 || plan["size_table"]!["insertion"] is not null
            || target.TemplateRevisionId != plan["template_revision_id"]!.GetValue<string>()
            || target.PageId != plan["document"]!["page_id"]!.GetValue<string>()
            || !string.Equals(target.TemplateSha256, inspection.FileSha256, StringComparison.OrdinalIgnoreCase)
            || planId == plan["plan_id"]!.GetValue<string>() || idempotencyKey == plan["idempotency_key"]!.GetValue<string>()
            || artifactSetId == plan["output"]!["artifact_set_id"]!.GetValue<string>()) throw Invalid();
        plan["size_table"]!["insertion"] = JsonSerializer.SerializeToNode(target.Insertion, Options);
        foreach (var capability in new[] { Capability, SizeInsertionCapability })
            if (!plan["required_capabilities"]!.AsArray().Any(item => item!.GetValue<string>() == capability))
                plan["required_capabilities"]!.AsArray().Add(capability);
        plan["plan_id"] = planId; plan["idempotency_key"] = idempotencyKey;
        plan["output"]!["artifact_set_id"] = artifactSetId; plan["created_at"] = createdAt;
        plan["plan_digest"] = RenderPlanDigest.Compute(plan.ToJsonString());
        var migrated = plan.ToJsonString();
        if (!validator.Validate(migrated).IsValid || !RenderTemplateEvidenceValidator.Matches(migrated, inspection)) throw Invalid();
        return migrated;
    }

    /// <summary>Legacy empty-target size plans need an explicit binding and a new attempt; no PSD target is guessed.</summary>
    public string MigrateSizeTableTarget(string sourceJson, SizeTableRenderTarget target, PsdInspectionDocument inspection,
        string planId, string idempotencyKey, string artifactSetId, DateTimeOffset createdAt)
    {
        if (!validator.Validate(sourceJson).IsValid) throw Invalid();
        var plan = JsonNode.Parse(sourceJson)!;
        if (plan["document"]!["content_type"]!.GetValue<string>() != "dynamic_size_table"
            || plan["targets"]!.AsArray().Count != 0
            || target.TemplateRevisionId != plan["template_revision_id"]!.GetValue<string>()
            || target.Binding.PageId != plan["document"]!["page_id"]!.GetValue<string>()
            || !string.Equals(target.TemplateSha256, inspection.FileSha256, StringComparison.OrdinalIgnoreCase)
            || planId == plan["plan_id"]!.GetValue<string>()
            || idempotencyKey == plan["idempotency_key"]!.GetValue<string>()
            || artifactSetId == plan["output"]!["artifact_set_id"]!.GetValue<string>()) throw Invalid();
        plan["targets"]!.AsArray().Add(JsonSerializer.SerializeToNode(
            SizeTableRenderPlanBuilder.CreateTarget(target, plan["size_table"]!["render_resource_id"]!.GetValue<string>()), Options));
        var capabilities = plan["required_capabilities"]!.AsArray();
        foreach (var capability in new[] { Capability, SizeTableCapability })
            if (!capabilities.Any(item => item!.GetValue<string>() == capability)) capabilities.Add(capability);
        plan["plan_id"] = planId;
        plan["idempotency_key"] = idempotencyKey;
        plan["output"]!["artifact_set_id"] = artifactSetId;
        plan["created_at"] = createdAt;
        plan["plan_digest"] = RenderPlanDigest.Compute(plan.ToJsonString());
        var migrated = plan.ToJsonString();
        if (!validator.Validate(migrated).IsValid || !RenderTemplateEvidenceValidator.Matches(migrated, inspection)) throw Invalid();
        return migrated;
    }

    private static InvalidDataException Invalid() => new("RENDER_PLAN_PLACEMENT_INVALID");
}
