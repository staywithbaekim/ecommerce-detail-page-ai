using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using Desktop.Application.Inputs;
using Desktop.Application.Ai;
using Desktop.Application.Rendering;
using System.Globalization;
using Desktop.Domain.Inputs;
using Desktop.Domain.Templates;

namespace Desktop.Application.Templates;

/// <summary>Additional E3 evidence. E0 input/identity and E1 body are never rewritten.</summary>
public sealed record TemplateInstanceProductionEvidence(int Version, string InputManifestDigest,
    TemplateInstanceProjectionSnapshot Snapshot, TemplateRuleColorFacts Colors,
    ImmutableArray<AssetManifestAsset> Assets, ImmutableArray<TemplateInstanceVariantAssets> Variants,
    ImmutableArray<TemplateInstanceFrameSelection> Selections, ImmutableArray<TemplateInstanceSelectionOverride> Overrides,
    ImmutableArray<TemplateRenderSubjectGeometry> Geometry, string SourceEvidenceDigest,
    ImmutableArray<TemplateInstanceSizePreparation> SizePages,
    TemplateInstanceProductionSourceVersion? SourceVersion = null)
{
    public TemplateInstanceInputFacts Facts() => new(1, Colors, Assets.Select(asset =>
        SourceAssetMetadata.CreateReadable(asset.AssetId, asset.StyleId, asset.SourceRelativePath, asset.FileName,
            asset.Length, asset.Sha256, asset.PixelWidth, asset.PixelHeight,
            Enum.Parse<SourceImageFormat>(asset.Format, true), asset.MimeType)).ToImmutableArray(), Variants, Selections, Overrides);

    public TemplateInstanceRenderPlanRequest PlanRequest(TemplateInstanceProjection instance, TemplateRevision revision,
        ImportedTemplateResource resource, TemplateRevision? origin, bool includeEditablePsd)
    {
        var page = revision.Pages.Single(page => page.PageId == instance.SourcePageId);
        var name = (page.Order + 1).ToString("D2", CultureInfo.InvariantCulture) + "-"
            + AiRenderPlanFactory.OutputNamePart(page.Name, 80, "页面") + "-实例"
            + (instance.Order + 1).ToString("D2", CultureInfo.InvariantCulture);
        var selected = instance.Frames.Select(frame => frame.AssetId).ToHashSet(StringComparer.Ordinal);
        var size = SizePages.SingleOrDefault(item => item.SourcePageId == instance.SourcePageId);
        if (size?.TableJson is not null) selected.Add(SizeTableRepairData.Deserialize(size.TableJson).Source.AssetId);
        return new(Snapshot, instance.PageInstanceId, revision, resource,
            Facts().Assets.Where(asset => selected.Contains(asset.AssetId)).ToArray(), includeEditablePsd,
            new(1, name, Geometry.Where(item => selected.Contains(item.AssetResourceId)).ToArray(), size), origin);
    }
}

public interface ITemplateInstanceProductionEvidenceStore
{
    Task<TemplateInstanceProductionEvidence?> ReadProductionEvidenceAsync(string batchId, string styleId, CancellationToken token);
    Task FreezeProductionEvidenceAsync(TemplateInstanceProductionEvidence evidence, CancellationToken token);
    Task<TemplateInstanceManualSelections?> ReadManualSelectionsAsync(string batchId, string styleId, CancellationToken token);
    Task SaveManualSelectionsAsync(string batchId, string styleId, TemplateInstanceManualSelections selections,
        long expectedRevision, CancellationToken token);
}

public sealed record TemplateInstanceProductionSourceVersion(string SessionId, long SessionRevision,
    long ManualRevision, string BaseFactsDigest);

public sealed record TemplateInstanceManualSelections(int Version, long Revision, string BaseFactsDigest,
    ImmutableArray<TemplateInstanceSelectionOverride> Overrides);

public sealed record TemplateInstanceSelectionTarget(string PageInstanceId, string Label, string? SelectedAssetId, long SelectionRevision);
public sealed record TemplateInstanceSelectionReview(string BatchId, string StyleId, string FrameId, string AssetId,
    long Revision, string BaseFactsDigest, IReadOnlyList<TemplateInstanceSelectionTarget> Targets);
public sealed record TemplateInstanceSelectionCommand(string BatchId, string StyleId, string FrameId, string AssetId,
    string PageInstanceId, long ExpectedRevision, string BaseFactsDigest);

/// <summary>Same production workflow; a read-only preview is not E1 or execution authorization.</summary>
public interface ITemplateInstanceSelectionWorkflow
{
    Task<TemplateInstanceSelectionReview?> ReviewInstanceSelectionAsync(string batchId, string styleId, string frameId,
        string assetId, CancellationToken token);
    Task LockInstanceSelectionAsync(TemplateInstanceSelectionCommand command, CancellationToken token);
}

public static class TemplateInstanceProductionEvidenceCodec
{
    private static readonly JsonSerializerOptions Json = new()
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, AllowDuplicateProperties = false,
        RespectRequiredConstructorParameters = true, RespectNullableAnnotations = true, MaxDepth = 64
    };
    public static string Serialize(TemplateInstanceProductionEvidence value)
    {
        Validate(value);
        return JsonSerializer.Serialize(value, Json);
    }
    public static TemplateInstanceProductionEvidence Deserialize(string json)
    {
        if (System.Text.Encoding.UTF8.GetByteCount(json) > 32 * 1024 * 1024) throw Invalid();
        var value = JsonSerializer.Deserialize<TemplateInstanceProductionEvidence>(json, Json) ?? throw Invalid();
        Validate(value);
        return value;
    }
    private static void Validate(TemplateInstanceProductionEvidence value)
    {
        if (value.Version != 1 || value.Assets.IsDefault || value.Geometry.IsDefault || value.SizePages.IsDefault
            || value.Assets.Any(asset => asset.Readability != "readable")
            || value.SizePages.Select(page => page.SourcePageId).Distinct(StringComparer.Ordinal).Count() != value.SizePages.Length) throw Invalid();
        TemplateInstanceProjectionSnapshotCodec.Validate(value.Snapshot);
        TemplateValueRules.Digest(value.InputManifestDigest, "TPL_INSTANCE_PRODUCTION_EVIDENCE_INVALID");
        TemplateValueRules.Digest(value.SourceEvidenceDigest, "TPL_INSTANCE_PRODUCTION_EVIDENCE_INVALID");
        if (value.SourceVersion is { } version)
        {
            if (version.SessionRevision < 0 || version.ManualRevision < 0 || string.IsNullOrWhiteSpace(version.SessionId)) throw Invalid();
            TemplateValueRules.Digest(version.BaseFactsDigest, "TPL_INSTANCE_PRODUCTION_EVIDENCE_INVALID");
        }
        if (TemplateInstanceProjector.ValidateAndComputeInputDigest(value.Facts()) != value.Snapshot.InputFactsDigest
            || value.Geometry.Select(item => item.AssetResourceId).Distinct(StringComparer.Ordinal).Count() != value.Geometry.Length
            || value.Geometry.Any(item => !item.IsValid || !value.Assets.Any(asset => asset.AssetId == item.AssetResourceId))) throw Invalid();
    }
    private static InvalidDataException Invalid() => new("TPL_INSTANCE_PRODUCTION_EVIDENCE_INVALID");
}
