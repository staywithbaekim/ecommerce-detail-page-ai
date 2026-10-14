using Desktop.Application.Psd;
using Desktop.Domain.Templates;

namespace Desktop.Application.Templates;

public enum CalibrationUndoAction
{
    None = 0,
    Undo,
    Redo
}

public sealed record TemplateFramePreviewBounds(double Left, double Top, double Width, double Height);

public sealed record TemplateFrameProjection(
    string FrameId,
    string PageId,
    IReadOnlyList<int> LayerPath,
    int LayerId,
    string LayerName,
    string StructureSignature,
    int Left,
    int Top,
    int Width,
    int Height,
    bool HasMask,
    bool HasVectorMask,
    bool IsVisible,
    bool IsClipping,
    byte Opacity,
    string BlendMode,
    bool IsPreviewSupported,
    bool IsRegistered)
{
    public TemplateLayerKind LayerKind { get; init; } = TemplateLayerKind.SmartObject;
    // Derived from the imported clipping stack, never supplied by AI or names.
    public IReadOnlyList<SupersededLayerEvidence> SupersededLayers { get; init; } = [];

    public string TemplateSha256 { get; init; } = string.Empty;

    public bool IsSelected { get; init; }

    public bool HasPersistedBinding { get; init; }
    public string? PersistedFrameId { get; init; }
    // Calibrated document-space target; raw smart-object bounds can extend far
    // beyond its visible product slot. Presentation only, no geometry mutation.
    public TemplateFramePreviewBounds? PreviewBounds { get; init; }

    // Null preserves older inspection compatibility; false is positive evidence
    // that this visible-flagged layer is covered in the actual composite.
    public bool? HasCompositeContribution { get; init; }

    public string? BoundAssetId { get; init; }

    public double Scale { get; init; } = 1;

    public double OffsetX { get; init; }

    public double OffsetY { get; init; }

    public string SelectionRequirement { get; init; } = string.Empty;

    public string RegistrationLabel => IsRegistered ? "已绑定素材" : "未绑定，可跳过";

    public string ReviewStatusText => HasPersistedBinding
        ? "已确认规则"
        : IsRegistered ? "已绑定素材" : "未绑定，可跳过";

    public string StatusTone => HasPersistedBinding
        ? "Completed"
        : IsRegistered ? "Pending" : "Fixed";

    public string StructuralSummary => string.IsNullOrWhiteSpace(SelectionRequirement)
        ? PreviewLabel
        : SelectionRequirement;

    public string StatusColor => IsRegistered ? "#34C759" : "#8E8E93";

    public string PreviewLabel => IsPreviewSupported ? "可预览" : "预览受限";
}

public sealed record TemplateCalibrationAsset(
    string AssetId,
    string DisplayName,
    string SourceReference,
    int Width,
    int Height);

public interface ICalibrationAssetSourcePort
{
    Task<TemplateCalibrationAsset> InspectReadOnlyAsync(
        string sourcePath,
        CancellationToken cancellationToken);
}

public sealed record TemplateCalibrationDraftView(
    string RevisionId,
    string PageId,
    int CanvasWidth,
    int CanvasHeight,
    string? TemplatePreviewSourceReference,
    bool IsReadOnly,
    IReadOnlyList<TemplateFrameProjection> Frames,
    IReadOnlyList<TemplateCalibrationAsset> Assets,
    string? SelectedAssetId,
    string? SelectedFrameId,
    double SelectedScale,
    double SelectedOffsetX,
    double SelectedOffsetY,
    string SelectionRequirement,
    string StatusMessage,
    bool CanUndo,
    bool CanRedo,
    bool IsBlocked);

public sealed record TemplateCalibrationOperation(
    string OperationId,
    string FrameId,
    string? PreviousAssetId,
    string? NextAssetId,
    double PreviousScale,
    double NextScale,
    double PreviousOffsetX,
    double NextOffsetX,
    double PreviousOffsetY,
    double NextOffsetY);

public interface ITemplateCalibrationService
{
    Task<TemplateCalibrationDraftView> LoadAsync(
        TemplateDraftSession session,
        string pageId,
        CancellationToken cancellationToken);

    Task<TemplateCalibrationDraftView> BindAssetAsync(
        TemplateDraftSession session,
        string pageId,
        string frameId,
        string assetId,
        CancellationToken cancellationToken);

    Task<TemplateCalibrationDraftView> BindReadOnlyAssetAsync(
        TemplateDraftSession session,
        string pageId,
        string frameId,
        string sourcePath,
        CancellationToken cancellationToken);

    Task<TemplateCalibrationDraftView> ClearAssetAsync(
        TemplateDraftSession session,
        string pageId,
        string frameId,
        CancellationToken cancellationToken);

    Task<TemplateCalibrationDraftView> SetGeometryAsync(
        TemplateDraftSession session,
        string pageId,
        string frameId,
        double scale,
        double offsetX,
        double offsetY,
        CancellationToken cancellationToken);

    Task<TemplateCalibrationDraftView> SetSelectionRequirementAsync(
        TemplateDraftSession session,
        string pageId,
        string frameId,
        string selectionRequirement,
        CancellationToken cancellationToken);

    Task<TemplateCalibrationDraftView> ApplyHistoryAsync(
        TemplateDraftSession session,
        string pageId,
        CalibrationUndoAction action,
        CancellationToken cancellationToken);
}
