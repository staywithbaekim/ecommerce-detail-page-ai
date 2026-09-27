using System.Text.Json.Nodes;
using Desktop.Domain.SizeTables;

namespace Desktop.Application.Rendering;

/// <summary>R1 insertion-only fact whitelist. Journal ancestry and request replay remain the coordinator's responsibility.</summary>
public static class RenderPlanSizeTableRepair
{
    public static void ValidateOrigin(RenderSizeTableRepairOrigin origin) => ReadOrigin(origin);

    public static void Validate(string before, string after, RenderSizeTableRepairOrigin origin)
    {
        try
        {
            var (baseTable, table) = ReadOrigin(origin);
            using var oldDocument = SizeTableRepairData.ReadPlan(before);
            using var newDocument = SizeTableRepairData.ReadPlan(after);
            var oldRoot = oldDocument.RootElement;
            var newRoot = newDocument.RootElement;
            if (origin.BasePlanDigest != oldRoot.GetProperty("plan_digest").GetString()
                || !SizeTableRepairData.MatchesPlan(baseTable, oldRoot) || !SizeTableRepairData.MatchesPlan(table, newRoot)
                || !RenderPlanPlacementMigration.IsExplicit(oldRoot) || !RenderPlanPlacementMigration.HasSizeInsertion(oldRoot)
                || !RenderPlanPlacementMigration.IsExplicit(newRoot) || !RenderPlanPlacementMigration.HasSizeInsertion(newRoot)
                || oldRoot.GetProperty("targets").GetArrayLength() != 0 || newRoot.GetProperty("targets").GetArrayLength() != 0)
                throw Invalid();

            var oldPlan = JsonNode.Parse(before)!.AsObject();
            var newPlan = JsonNode.Parse(after)!.AsObject();
            foreach (var key in new[] { "plan_id", "idempotency_key", "plan_digest" })
            {
                if (JsonNode.DeepEquals(oldPlan[key], newPlan[key])) throw Invalid();
                oldPlan.Remove(key); newPlan.Remove(key);
            }
            var oldOutput = oldPlan["output"]!.AsObject();
            var newOutput = newPlan["output"]!.AsObject();
            if (string.Equals(oldOutput["artifact_set_id"]!.GetValue<string>(), newOutput["artifact_set_id"]!.GetValue<string>(),
                StringComparison.OrdinalIgnoreCase)) throw Invalid();
            oldOutput.Remove("artifact_set_id"); newOutput.Remove("artifact_set_id");
            var createdAt = SizeTableRepairData.Timestamp(newRoot.GetProperty("created_at"));
            if (createdAt < SizeTableRepairData.Timestamp(oldRoot.GetProperty("created_at")) || createdAt < table.UpdatedAt) throw Invalid();
            oldPlan.Remove("created_at"); newPlan.Remove("created_at");

            var oldSize = oldPlan["size_table"]!.AsObject();
            var newSize = newPlan["size_table"]!.AsObject();
            var oldResource = SizeResource(oldPlan, oldSize);
            var newResource = SizeResource(newPlan, newSize);
            // Content-owned PNG paths must never alias the old revision, even by Windows case folding.
            if (string.Equals(oldResource["resource_id"]!.GetValue<string>(), newResource["resource_id"]!.GetValue<string>(),
                StringComparison.OrdinalIgnoreCase)) throw Invalid();
            foreach (var key in new[] { "resource_id", "sha256", "byte_length" })
            { oldResource.Remove(key); newResource.Remove(key); }

            ValidatePlacement(newSize["insertion"]!.AsObject());
            foreach (var key in new[] { "columns", "rows", "structured_data_digest", "grid_digest", "render_resource_id" })
            { oldSize.Remove(key); newSize.Remove(key); }
            // Only the placement is removed; anchor, hidden layers, bounds, style and layout versions stay exact.
            oldSize["insertion"]!.AsObject().Remove("placement");
            newSize["insertion"]!.AsObject().Remove("placement");
            if (!JsonNode.DeepEquals(oldPlan, newPlan)) throw Invalid();
        }
        catch (Exception error) when (SizeTableRepairData.IsMalformed(error)) { throw Invalid(); }
    }

    private static (SizeTable Before, SizeTable After) ReadOrigin(RenderSizeTableRepairOrigin origin)
    {
        if (origin is null || !SizeTableRepairData.IsRequestId(origin.RequestId)
            || !SizeTableRepairData.IsDigest(origin.RequestDigest) || !SizeTableRepairData.IsDigest(origin.BasePlanDigest)
            || origin.ExpectedJournalRevision is < 1 or long.MaxValue || origin.BaseAttempt is < 1 or int.MaxValue) throw Invalid();
        var before = SizeTableRepairData.Deserialize(origin.BaseTableJson);
        var after = SizeTableRepairData.Deserialize(origin.TableJson);
        if (before.TableId != after.TableId || before.BatchId != after.BatchId || before.StyleId != after.StyleId
            || before.PageId != after.PageId || before.CandidateId != after.CandidateId || before.Source != after.Source
            || !before.CreatedAt.EqualsExact(after.CreatedAt) || after.UpdatedAt < before.UpdatedAt
            || after.Revision != checked(before.Revision + 1) || !SizeTableRepairData.ContentChanged(before, after)) throw Invalid();
        return (before, after);
    }

    private static JsonObject SizeResource(JsonObject plan, JsonObject size)
    {
        var resources = plan["resources"]!.AsArray().Where(resource => resource!["role"]!.GetValue<string>() == "deterministic_size_table").ToArray();
        if (resources.Length != 1) throw Invalid();
        var result = resources[0]!.AsObject();
        if (result["resource_id"]!.GetValue<string>() != size["render_resource_id"]!.GetValue<string>()
            || result["media_type"]!.GetValue<string>() != "image/png"
            || size["grid_digest"]!.GetValue<string>() != "sha256:" + result["sha256"]!.GetValue<string>()) throw Invalid();
        return result;
    }

    private static void ValidatePlacement(JsonObject insertion)
    {
        var bounds = insertion["safe_bounds"]!;
        var placement = insertion["placement"]!;
        var sourceWidth = placement["source_width"]!.GetValue<int>();
        var sourceHeight = placement["source_height"]!.GetValue<int>();
        // Match SizeTableCalibrationResolver's TemplateRect (double) -> decimal calculation exactly.
        var left = (decimal)bounds["left"]!.GetValue<double>();
        var top = (decimal)bounds["top"]!.GetValue<double>();
        var width = (decimal)(bounds["right"]!.GetValue<double>() - bounds["left"]!.GetValue<double>());
        var height = (decimal)(bounds["bottom"]!.GetValue<double>() - bounds["top"]!.GetValue<double>());
        if (width <= 0 || height <= 0 || sourceWidth <= 0 || sourceHeight <= 0) throw Invalid();
        var scale = Math.Min(width / sourceWidth, height / sourceHeight);
        var placedWidth = sourceWidth * scale;
        var placedHeight = sourceHeight * scale;
        if (placement["width"]!.GetValue<decimal>() != placedWidth || placement["height"]!.GetValue<decimal>() != placedHeight
            || placement["left"]!.GetValue<decimal>() != left + (width - placedWidth) / 2
            || placement["top"]!.GetValue<decimal>() != top + (height - placedHeight) / 2) throw Invalid();
    }

    private static InvalidDataException Invalid() => new("SIZE_REPAIR_PLAN_INVALID");
}
