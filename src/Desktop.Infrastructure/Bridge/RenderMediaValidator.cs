using System.Security.Cryptography;
using System.Text.Json;
using Desktop.Application.Files;
using Desktop.Application.Psd;
using Desktop.Application.Rendering;
using Desktop.Contracts.Validation;

namespace Desktop.Infrastructure.Bridge;

public sealed record RenderMediaFile(string Kind, long ByteLength, string Sha256);
public sealed record RenderMediaEvidence(string PlanDigest, RenderJpegEvidence Jpeg,
    int PsdLayerCount, int EditableTextLayerCount, IReadOnlyList<RenderMediaFile> Files)
{
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public RenderSizeTableEvidence? SizeTable { get; init; }
}

public sealed record RenderSizeTableEvidence(string SizeTableId, string StructuredDataDigest,
    string RasterSha256, string TemplateSha256, string PageId, int EmbeddedLayerId);

/// <summary>Validates JPG-only output independently; editable PSD plans retain full structural inspection.</summary>
public sealed class RenderMediaValidator(IPathPolicy paths, IPsdInspectionPort inspector)
{
    public async Task<RenderMediaEvidence> ValidateAsync(string planJson, string templateRelativePath,
        string jpegRelativePath, string? psdRelativePath, IReadOnlyList<RenderMediaFile> receipts,
        CancellationToken cancellationToken)
    {
        if (!new RenderPlanValidator(new JsonSchemaContractValidator()).Validate(planJson).IsValid) throw Invalid();
        using var document = JsonDocument.Parse(planJson);
        var plan = document.RootElement;
        var includePsd = plan.GetProperty("output").GetProperty("editable_psd").GetProperty("enabled").GetBoolean();
        const string psdKind = "editable_psd";
        if (plan.GetProperty("document").GetProperty("content_type").GetString() == "dynamic_size_table"
                && !RenderPlanPlacementMigration.HasSizeInsertion(plan)
                && !plan.GetProperty("required_capabilities").EnumerateArray()
                    .Any(item => item.GetString() == RenderPlanPlacementMigration.SizeTableCapability)
            || includePsd && (psdRelativePath is null || receipts.Count != 2 || receipts.Select(item => item.Kind).Distinct().Count() != 2)
            || !includePsd && (psdRelativePath is not null || receipts.Count != 1 || receipts.Any(item => item.Kind != "jpg"))
            || receipts.Any(item => item.Kind != "jpg" && item.Kind != psdKind)) throw Invalid();
        var jpgReceipt = receipts.Single(item => item.Kind == "jpg");
        using var jpg = Open(jpegRelativePath);
        await RenderPerformance.MeasureAsync("media.hash_jpg", () => VerifyBytes(jpg, jpgReceipt, cancellationToken)).ConfigureAwait(false);
        var canvas = plan.GetProperty("document").GetProperty("canvas");
        RenderJpegEvidence jpeg;
        using (RenderPerformance.Start("media.decode_jpg"))
        jpeg = RenderJpegValidator.Validate(jpg, canvas.GetProperty("width").GetInt32(),
            canvas.GetProperty("height").GetInt32(), cancellationToken);
        if (!includePsd) return new(plan.GetProperty("plan_digest").GetString()!, jpeg, 0, 0, receipts.ToArray());

        using var template = Open(templateRelativePath);
        using var psd = Open(psdRelativePath!);
        var psdReceipt = receipts.Single(item => item.Kind == psdKind);
        await RenderPerformance.MeasureAsync("media.hash_psd", () => VerifyBytes(psd, psdReceipt, cancellationToken)).ConfigureAwait(false);
        var baseline = await RenderPerformance.MeasureAsync("media.inspect_baseline", () => inspector.InspectAsync(new($"render_baseline_{Guid.NewGuid():N}",
            templateRelativePath, IncludeRenderContentEvidence: true), cancellationToken)).ConfigureAwait(false);
        if (!baseline.IsSuccess) throw new InvalidDataException("RENDER_BASELINE_INSPECTION_FAILED:" + baseline.Error!.Code);
        if (!RenderTemplateEvidenceValidator.Matches(planJson, baseline.Inspection!)) throw new InvalidDataException("RENDER_BASELINE_MISMATCH");
        var output = await RenderPerformance.MeasureAsync("media.inspect_output", () => inspector.InspectAsync(new($"render_output_{Guid.NewGuid():N}",
            psdRelativePath!, IncludeRenderContentEvidence: true), cancellationToken)).ConfigureAwait(false);
        using var evidencePerformance = RenderPerformance.Start("media.compare_evidence");
        if (!output.IsSuccess) throw new InvalidDataException("RENDER_OUTPUT_INSPECTION_FAILED:" + output.Error!.Code);
        if (output.Inspection!.SizeBytes != psdReceipt.ByteLength
            || !string.Equals(output.Inspection.FileSha256, psdReceipt.Sha256, StringComparison.OrdinalIgnoreCase)
            || !RenderOutputEvidenceValidator.MatchesPsd(planJson, baseline.Inspection!, output.Inspection)) throw Invalid();
        await RenderPerformance.MeasureAsync("media.hash_template", () => VerifyBytes(template,
            new("template", baseline.Inspection!.SizeBytes, baseline.Inspection.FileSha256), cancellationToken))
            .ConfigureAwait(false);
        return new(plan.GetProperty("plan_digest").GetString()!, jpeg, output.Inspection.LayerCount,
            output.Inspection.Layers.Count(layer => layer.Kind == "type"), receipts.ToArray())
        {
            SizeTable = plan.GetProperty("size_table").ValueKind == JsonValueKind.Null ? null
                : new(plan.GetProperty("size_table").GetProperty("size_table_id").GetString()!,
                    plan.GetProperty("size_table").GetProperty("structured_data_digest").GetString()!,
                    plan.GetProperty("size_table").GetProperty("grid_digest").GetString()![7..],
                    baseline.Inspection!.FileSha256, plan.GetProperty("document").GetProperty("page_id").GetString()!,
                    RenderPlanPlacementMigration.HasSizeInsertion(plan)
                        ? output.Inspection.Layers.Single(layer => !baseline.Inspection.Layers.Any(old => old.LayerId == layer.LayerId)).LayerId
                        : plan.GetProperty("targets")[0].GetProperty("expected_layer").GetProperty("layer_id").GetInt32())
        };
    }

    private FileStream Open(string relativePath)
    {
        var path = paths.ResolveOwnedPath(relativePath);
        BridgeOutputReservation.ValidatePath(path);
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        try
        {
            BridgeResourceLease.VerifyOpenedPath(stream.SafeFileHandle, path);
            BridgeOutputReservation.ValidatePath(path);
            return stream;
        }
        catch { stream.Dispose(); throw; }
    }

    private static async Task VerifyBytes(Stream stream, RenderMediaFile receipt, CancellationToken cancellationToken)
    {
        if (receipt.ByteLength is < 1 or > 2147483648 || stream.Length != receipt.ByteLength
            || receipt.Sha256.Length != 64 || receipt.Sha256.Any(c => !char.IsAsciiHexDigit(c))) throw Invalid();
        stream.Position = 0;
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
        if (!string.Equals(hash, receipt.Sha256, StringComparison.OrdinalIgnoreCase)) throw Invalid();
    }

    private static InvalidDataException Invalid() => new("RENDER_OUTPUT_INVALID");
}
