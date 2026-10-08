using System.Text.Json;
using System.Text.Json.Nodes;
using Desktop.Application.Rendering;

namespace Desktop.Application.Templates;

public sealed partial class TemplateInstanceRenderPlanFactory
{
    internal static bool IsSizeBusinessReason(string reason) => reason is "SIZE_TABLE_SOURCE_MISSING" or "SIZE_TABLE_SOURCE_CONFLICT"
        or "SIZE_TABLE_REVIEW_REQUIRED" or "SIZE_TABLE_INVALID" or "SIZE_TABLE_SOURCE_CHANGE_REVIEW_REQUIRED"
        or "SIZE_TABLE_RENDER_TARGET_REQUIRED" or "SIZE_TABLE_RELIABLE_CANDIDATE_MISSING";

    internal static void ValidateSizePreparation(TemplateInstanceRenderPlanRequest request, TemplateInstanceProjection instance)
    {
        var size = request.Production?.Size ?? throw Invalid("TPL_INSTANCE_SIZE_VALIDATION_REQUIRED");
        if (size.SourcePageId != instance.SourcePageId || request.Production!.Version != 1)
            throw Invalid("TPL_INSTANCE_SIZE_EVIDENCE_MISMATCH");
        if (size.BusinessReason is not null)
        {
            if (!IsSizeBusinessReason(size.BusinessReason) || size.BasePlanJson is not null || size.TableJson is not null)
                throw Invalid("TPL_INSTANCE_SIZE_EVIDENCE_MISMATCH");
            return;
        }
        if (size.TableJson is null || size.BasePlanJson is null) throw Invalid("TPL_INSTANCE_SIZE_EVIDENCE_MISMATCH");
        var table = SizeTableRepairData.Deserialize(size.TableJson);
        var asset = request.Assets.SingleOrDefault(asset => asset.AssetId == table.Source.AssetId);
        using var json = JsonDocument.Parse(size.BasePlanJson);
        var plan = json.RootElement;
        if (!SizeTableRepairData.MatchesPlan(table, size.BasePlanJson)
            || table.BatchId != instance.BatchId || table.StyleId != instance.StyleId || table.PageId != instance.SourcePageId
            || asset is null || asset.StyleId != instance.StyleId || table.Source.ContentDigest != "sha256:" + asset.Sha256.ToLowerInvariant()
            || plan.GetProperty("batch_id").GetString() != instance.BatchId || plan.GetProperty("product_id").GetString() != instance.StyleId
            || plan.GetProperty("template_revision_id").GetString() != instance.RevisionId
            || plan.GetProperty("document").GetProperty("page_id").GetString() != instance.SourcePageId
            || plan.GetProperty("document").GetProperty("content_type").GetString() != "dynamic_size_table"
            || plan.GetProperty("document").GetProperty("template_resource_id").GetString() != instance.TemplateResourceId
            || plan.GetProperty("size_table").GetProperty("structured_data_digest").GetString() != table.DataDigest
            || plan.GetProperty("output").GetProperty("editable_psd").GetProperty("enabled").GetBoolean() != request.IncludeEditablePsd)
            throw Invalid("TPL_INSTANCE_SIZE_EVIDENCE_MISMATCH");
        var template = plan.GetProperty("resources").EnumerateArray().Single(item => item.GetProperty("role").GetString() == "template_copy");
        if (template.GetProperty("sha256").GetString() != request.TemplateResource.Sha256
            || template.GetProperty("byte_length").GetInt64() != request.TemplateResource.Length)
            throw Invalid("TPL_INSTANCE_SIZE_EVIDENCE_MISMATCH");
    }

    private TemplateInstanceRenderPlanResult BuildSize(TemplateInstanceRenderPlanRequest request, TemplateInstanceProjection instance)
    {
        ValidateSizePreparation(request, instance);
        var size = request.Production!.Size!;
        if (size.BusinessReason is not null || !validator.Validate(size.BasePlanJson!).IsValid
            || !instance.Frames.IsEmpty || instance.BlockingReasons.Length != 1
            || instance.BlockingReasons[0].Code != "TPL_INSTANCE_SIZE_VALIDATION_REQUIRED")
            throw Invalid("TPL_INSTANCE_SIZE_EVIDENCE_MISMATCH");
        var node = JsonNode.Parse(size.BasePlanJson!)!;
        var digest = Hash(new { schema = "template-instance-size-plan/v1", instance.PageInstanceId,
            request.Snapshot.ProjectionDigest, request.Production });
        node["plan_id"] = "plan_" + digest[7..31];
        node["idempotency_key"] = "render/instance/" + digest[7..55];
        node["correlation_id"] = "corr_" + digest[31..55];
        node["output"]!["artifact_set_id"] = "artifact_" + digest[40..64];
        node["output"]!["jpg"]!["file_name"] = request.Production.OutputBaseName + ".jpg";
        if (request.IncludeEditablePsd) node["output"]!["editable_psd"]!["file_name"] = request.Production.OutputBaseName + ".psd";
        node["required_capabilities"]!.AsArray().Add(RenderPlanInstanceIdentity.Capability);
        node["document"]!["instance_identity"] = JsonSerializer.SerializeToNode(new
        {
            page_instance_id = instance.PageInstanceId, snapshot_id = instance.SnapshotId,
            rule_digest = instance.RuleDigest, input_facts_digest = instance.InputFactsDigest,
            projection_digest = request.Snapshot.ProjectionDigest, source_page_id = instance.SourcePageId,
            branch_id = instance.BranchId, recipe_id = instance.RecipeId, repetition_index = instance.RepetitionIndex,
            stable_order = instance.Order,
            slots = instance.Slots.Select(slot => new { slot_id = slot.SlotId, variant_id = slot.VariantId }),
            frames = Array.Empty<object>()
        });
        node["plan_digest"] = RenderPlanDigest.Compute(node.ToJsonString());
        var result = node.ToJsonString();
        if (!validator.Validate(result).IsValid) throw Invalid("TPL_INSTANCE_SIZE_EVIDENCE_MISMATCH");
        return new(instance.PageInstanceId, node["plan_id"]!.GetValue<string>(), node["plan_digest"]!.GetValue<string>(), result);
    }
}
