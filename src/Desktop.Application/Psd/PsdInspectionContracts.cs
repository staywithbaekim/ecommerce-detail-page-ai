using System.Text.Json;
using System.Text.Json.Serialization;

namespace Desktop.Application.Psd;

public static class PsdInspectionProtocol
{
    public const string Version = "psd-inspection/v1";
}

public interface IPsdInspectionPort
{
    Task<PsdInspectionResult> InspectAsync(
        PsdInspectionRequest request,
        CancellationToken cancellationToken);
}

public sealed record PsdInspectionRequest(
    string RequestId,
    string InternalPsdRelativePath,
    string? PreviewRelativePath = null,
    bool IncludeRenderContentEvidence = false);

public sealed record PsdInspectionResult(
    PsdInspectionDocument? Inspection,
    PsdInspectionError? Error)
{
    public bool IsSuccess => Inspection is not null && Error is null;

    public static PsdInspectionResult Success(PsdInspectionDocument inspection) =>
        new(inspection, null);

    public static PsdInspectionResult Failure(PsdInspectionError error) =>
        new(null, error);
}

public sealed record PsdInspectionError(
    string Code,
    string Message,
    string UserAction,
    bool Retryable);

public sealed record PsdInspectionDocument(
    string FileSha256,
    long SizeBytes,
    PsdCanvasInspection Canvas,
    int LayerCount,
    int SmartObjectCount,
    int MaskCount,
    int VectorMaskCount,
    int ClippingCount,
    string StructureSignature,
    PsdInspectionTiming Timing,
    long ProcessPeakWorkingSetBytes,
    PsdPreviewInspection? Preview,
    IReadOnlyList<PsdLayerInspection> Layers)
{
    [JsonIgnore]
    public bool NeedsRasterImageEvidence => Layers.Any(layer => layer.Kind == "pixel"
        && layer.PixelEvidence.RasterImage is null);

    // Enrich derived evidence only. Existing structural/content evidence,
    // preview and identities remain frozen, including evidence from older workers.
    public PsdInspectionDocument WithRasterImageEvidenceFrom(PsdInspectionDocument inspected)
    {
        static string Digest(string value) => (value.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)
            ? value[7..] : value).ToLowerInvariant();
        static object Structure(PsdInspectionDocument document) => new
        {
            FileSha256 = Digest(document.FileSha256), document.SizeBytes, document.Canvas, document.LayerCount,
            document.SmartObjectCount, document.MaskCount, document.VectorMaskCount,
            document.ClippingCount, StructureSignature = Digest(document.StructureSignature),
            Layers = document.Layers.Select(layer => layer with
            {
                PixelEvidence = new(false, 0, 0, null), ContentEvidence = null,
                SmartObject = layer.SmartObject is { } smart ? smart with
                { EmbeddedContentSha256 = null, EmbeddedContentAvailable = false } : null
            })
        };
        if (inspected.NeedsRasterImageEvidence
            || JsonSerializer.Serialize(Structure(this)) != JsonSerializer.Serialize(Structure(inspected)))
            throw new InvalidDataException("TPL_PSD_INSPECTION_CACHE_MISMATCH");
        return this with
        {
            Layers = Layers.Select((layer, index) => layer.Kind == "pixel"
                && layer.PixelEvidence.RasterImage is null
                    ? layer with { PixelEvidence = inspected.Layers[index].PixelEvidence }
                    : layer).ToArray()
        };
    }
}

public sealed record PsdCanvasInspection(
    int Width,
    int Height,
    int Depth,
    string ColorMode);

public sealed record PsdInspectionTiming(
    double OpenMilliseconds,
    double InspectMilliseconds,
    double TotalMilliseconds);

public sealed record PsdPreviewInspection(
    int Width,
    int Height,
    string Mode,
    string Sha256,
    long SizeBytes);

public sealed record PsdLayerInspection(
    IReadOnlyList<int> Path,
    int LayerId,
    string Name,
    string Kind,
    bool IsGroup,
    bool Visible,
    bool EffectiveVisible,
    bool Clipping,
    int Opacity,
    int FillOpacity,
    string BlendMode,
    PsdLayerBounds Bounds,
    bool HasMask,
    bool HasVectorMask,
    PsdClippingBaseInspection? ClippingBase,
    PsdSmartObjectInspection? SmartObject)
{
    /// <summary>
    /// Rendered-pixel evidence produced inside the isolated worker. Older
    /// workers leave this unavailable so learning fails closed instead of
    /// treating visibility flags or layer bounds as final-output proof.
    /// </summary>
    public PsdLayerPixelEvidence PixelEvidence { get; init; } =
        new(false, 0, 0, null);
    // Optional inspection evidence; old workers remain readable but cannot validate render output.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PsdLayerContentEvidence? ContentEvidence { get; init; }
}

public sealed record PsdLayerContentEvidence(bool Available, string? PixelSha256, string? TextSha256,
    string? MaskSha256, string? VectorMaskSha256, string? EffectsSha256)
{
    // Older workers remain readable but cannot authorize editable text preservation.
    public string? TextStyleSha256 { get; init; }
    public string? AdjustmentSha256 { get; init; }
}

public sealed record PsdLayerBounds(
    int Left,
    int Top,
    int Right,
    int Bottom);

public sealed record PsdLayerPixelEvidence(
    bool Available,
    long RenderedPixelCount,
    long CompositeContributionPixelCount,
    PsdLayerBounds? CompositeContributionBounds)
{
    [JsonIgnore]
    public bool HasMeaningfulCompositeContribution => Available && RenderedPixelCount > 0
        && CompositeContributionPixelCount >= 64
        && CompositeContributionBounds is { } bounds
        && (long)bounds.Right - bounds.Left >= 4 && (long)bounds.Bottom - bounds.Top >= 4
        && ((double)bounds.Right - bounds.Left) * ((double)bounds.Bottom - bounds.Top) >= 64;

    [JsonIgnore]
    public bool HasReplaceableRasterContent => HasMeaningfulCompositeContribution
        && RasterImage?.HasPhotographicContent == true
        && CompositeContributionPixelCount >= ((double)CompositeContributionBounds!.Right - CompositeContributionBounds.Left)
            * ((double)CompositeContributionBounds.Bottom - CompositeContributionBounds.Top) * .02;

    // Additive evidence: old workers remain readable, but raster targets fail closed.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PsdRasterImageEvidence? RasterImage { get; init; }
}

public sealed record PsdRasterImageEvidence(bool Available, int ColorCount, double DetailPixelRatio)
{
    // A conservative content gate, not a claim that every textured layer is a product.
    [JsonIgnore]
    public bool HasPhotographicContent => Available && ColorCount >= 128
        && double.IsFinite(DetailPixelRatio) && DetailPixelRatio is >= .02 and <= 1;
}

public sealed record PsdClippingBaseInspection(
    IReadOnlyList<int> Path,
    int LayerId,
    string Name,
    string Kind,
    bool Visible,
    bool EffectiveVisible,
    PsdLayerBounds Bounds,
    bool HasMask,
    bool HasVectorMask,
    int Opacity,
    string BlendMode);

public sealed record PsdSmartObjectInspection(
    string UniqueId,
    string Filename,
    string Kind,
    string? FileType,
    string DetectedFileType,
    long FileSize,
    bool IsPsd,
    IReadOnlyList<double> TransformBox)
{
    /// <summary>
    /// SHA-256 of the embedded payload when the smart object is an embedded
    /// data resource.  External links deliberately return <c>null</c> so a
    /// caller cannot accidentally dereference an untrusted path.
    /// </summary>
    public string? EmbeddedContentSha256 { get; init; }

    /// <summary>
    /// True only when the worker was able to inspect embedded bytes.  This is
    /// separate from <see cref="Kind"/> to keep old worker responses readable.
    /// </summary>
    public bool EmbeddedContentAvailable { get; init; }
}
