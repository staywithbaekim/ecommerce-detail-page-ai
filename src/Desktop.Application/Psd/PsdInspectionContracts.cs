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
    string? PreviewRelativePath = null);

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
    IReadOnlyList<PsdLayerInspection> Layers);

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
    PsdLayerBounds? CompositeContributionBounds);

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
