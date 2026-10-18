using System.Collections.Immutable;
using System.Text.RegularExpressions;

namespace Desktop.Domain.Templates;

public enum TemplateGroupStatus
{
    Active = 0,
    Archived
}

public enum TemplateRevisionState
{
    Draft = 0,
    ImportingCopy,
    DuplicateReview,
    StructureInspecting,
    FrameBinding,
    Calibrating,
    AwaitingReview,
    Previewing,
    Committing,
    Committed,
    ImportFailed,
    InspectionBlocked,
    CommitFailed,
    Abandoned
}

public enum TemplatePageContentType
{
    NormalComposition = 0,
    DynamicSizeTable
}

public enum TemplatePageTypeRecognition
{
    OrdinaryPage = 0,
    SizeTablePage
}

public enum TemplateGenerationMode
{
    OncePerProduct = 0,
    OncePerVariant,
    VariantCapacityCombination
}

public enum TemplateLayerKind
{
    SmartObject = 0,
    Pixel = 1
}

public enum TemplatePreviewStatus
{
    NotPreviewed = 0,
    Succeeded,
    Failed
}

public enum TemplatePreviewEvidenceKind
{
    LocalRenderer = 0,
    PhotoshopAccurate
}

public enum CalibrationSampleSource
{
    UserSelected = 0
}

public static partial class TemplateValueRules
{
    [GeneratedRegex("^sha256:[0-9a-fA-F]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex StructureSignatureRegex();

    public static string Identifier(string value, string code)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128
            || value.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '_' and not '-'))
        {
            throw new TemplateRuleViolationException(code);
        }

        return value;
    }

    public static string RequiredText(string value, int maxLength, string code)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maxLength)
        {
            throw new TemplateRuleViolationException(code);
        }

        return value.Trim();
    }

    public static string? OptionalText(string? value, int maxLength, string code)
    {
        if (value is null)
        {
            return null;
        }

        if (value.Length > maxLength)
        {
            throw new TemplateRuleViolationException(code);
        }

        var normalized = value.Trim();
        return normalized.Length == 0 ? null : normalized;
    }

    public static string Digest(string value, string code)
    {
        var normalized = RequiredText(value, 128, code);
        if (!StructureSignatureRegex().IsMatch(normalized))
        {
            throw new TemplateRuleViolationException(code);
        }

        return normalized.ToLowerInvariant();
    }

    public static string RelativeOwnedPath(string value, string code)
    {
        var normalized = RequiredText(value, 512, code);
        var segments = normalized.Split('/');
        if (normalized.StartsWith('/')
            || normalized.Contains('\\')
            || normalized.Contains(':')
            || segments.Any(segment => segment is "" or "." or "..")
            || normalized.Any(character => !char.IsAsciiLetterOrDigit(character)
                && character is not '_' and not '-' and not '.' and not '/'))
        {
            throw new TemplateRuleViolationException(code);
        }

        return normalized;
    }
}

public sealed record LayerPath
{
    public const string PsdBottomToTop = "psd_bottom_to_top";

    private LayerPath(ImmutableArray<int> segments)
    {
        Segments = segments;
        Order = PsdBottomToTop;
    }

    public ImmutableArray<int> Segments { get; }

    public string Order { get; }

    public static LayerPath Create(IEnumerable<int> segments)
    {
        ArgumentNullException.ThrowIfNull(segments);
        var frozen = segments.ToImmutableArray();
        if (frozen.IsDefaultOrEmpty || frozen.Length > 64 || frozen.Any(segment => segment <= 0))
        {
            throw new TemplateRuleViolationException("TPL_FRAME_LAYER_PATH_INVALID");
        }

        return new LayerPath(frozen);
    }
}

public sealed record LayerStructureSignature
{
    private LayerStructureSignature(string value)
    {
        Value = value;
    }

    public string Value { get; }

    public static LayerStructureSignature Create(string value) =>
        new(TemplateValueRules.Digest(value, "TPL_FRAME_STRUCTURE_EVIDENCE_INCOMPLETE"));
}

public sealed record ExpectedLayerEvidence
{
    private ExpectedLayerEvidence(
        int layerId,
        TemplateLayerKind kind,
        string nameBefore,
        bool visible,
        bool clipping,
        bool userMask,
        bool vectorMask,
        byte opacity,
        string blendMode,
        LayerStructureSignature structureSignature)
    {
        LayerId = layerId;
        Kind = kind;
        NameBefore = nameBefore;
        Visible = visible;
        Clipping = clipping;
        UserMask = userMask;
        VectorMask = vectorMask;
        Opacity = opacity;
        BlendMode = blendMode;
        StructureSignature = structureSignature;
    }

    public int LayerId { get; }

    public TemplateLayerKind Kind { get; }

    public string NameBefore { get; }

    public bool Visible { get; }

    public bool Clipping { get; }

    public bool UserMask { get; }

    public bool VectorMask { get; }

    public byte Opacity { get; }

    public string BlendMode { get; }

    public LayerStructureSignature StructureSignature { get; }

    public static ExpectedLayerEvidence Create(
        int layerId,
        string nameBefore,
        bool visible,
        bool clipping,
        bool userMask,
        bool vectorMask,
        byte opacity,
        string blendMode,
        LayerStructureSignature structureSignature,
        TemplateLayerKind kind = TemplateLayerKind.SmartObject)
    {
        ArgumentNullException.ThrowIfNull(structureSignature);
        if (!Enum.IsDefined(kind) || layerId <= 0 || !visible || string.IsNullOrWhiteSpace(nameBefore) || nameBefore.Length > 256)
        {
            throw new TemplateRuleViolationException("TPL_FRAME_STRUCTURE_EVIDENCE_INCOMPLETE");
        }

        return new ExpectedLayerEvidence(
            layerId,
            kind,
            // This is exact PSD identity evidence, not a user-facing label.
            nameBefore,
            visible,
            clipping,
            userMask,
            vectorMask,
            opacity,
            TemplateValueRules.RequiredText(blendMode, 64, "TPL_FRAME_STRUCTURE_EVIDENCE_INCOMPLETE"),
            structureSignature);
    }
}

public sealed record SupersededLayerEvidence
{
    private SupersededLayerEvidence(
        LayerPath layerPath,
        bool expectedClipping,
        int? expectedLayerId)
    {
        LayerPath = layerPath;
        ExpectedKind = TemplateLayerKind.SmartObject;
        ExpectedVisible = true;
        ExpectedClipping = expectedClipping;
        ExpectedLayerId = expectedLayerId;
    }

    public LayerPath LayerPath { get; }

    public TemplateLayerKind ExpectedKind { get; }

    public bool ExpectedVisible { get; }

    public bool ExpectedClipping { get; }

    public int? ExpectedLayerId { get; }

    public static SupersededLayerEvidence Create(
        LayerPath layerPath,
        bool expectedClipping,
        int? expectedLayerId)
    {
        ArgumentNullException.ThrowIfNull(layerPath);
        if (expectedLayerId is <= 0)
        {
            throw new TemplateRuleViolationException("TPL_FRAME_STRUCTURE_EVIDENCE_INCOMPLETE");
        }

        return new SupersededLayerEvidence(layerPath, expectedClipping, expectedLayerId);
    }
}

public sealed record SelectionCriterion
{
    private SelectionCriterion(string key, string value)
    {
        Key = key;
        Value = value;
    }

    public string Key { get; }

    public string Value { get; }

    public static SelectionCriterion Create(string key, string value) => new(
        TemplateValueRules.RequiredText(key, 64, "TPL_SELECTION_REQUIREMENT_INVALID"),
        TemplateValueRules.RequiredText(value, 256, "TPL_SELECTION_REQUIREMENT_INVALID"));
}

public sealed record SelectionRequirement
{
    private SelectionRequirement(string description, ImmutableArray<SelectionCriterion> criteria)
    {
        Description = description;
        Criteria = criteria;
    }

    public string Description { get; }

    public ImmutableArray<SelectionCriterion> Criteria { get; }

    public static SelectionRequirement Create(
        string description,
        IEnumerable<SelectionCriterion>? criteria = null)
    {
        var frozen = criteria?.ToImmutableArray() ?? [];
        if (frozen.Any(item => item is null)
            || frozen.Select(item => item.Key).Distinct(StringComparer.Ordinal).Count() != frozen.Length)
        {
            throw new TemplateRuleViolationException("TPL_SELECTION_REQUIREMENT_INVALID");
        }

        return new SelectionRequirement(
            TemplateValueRules.RequiredText(description, 2000, "TPL_SELECTION_REQUIREMENT_INVALID"),
            frozen);
    }
}

public readonly record struct TemplateRect(double Left, double Top, double Right, double Bottom)
{
    public static TemplateRect Create(double left, double top, double right, double bottom)
    {
        if (!double.IsFinite(left) || !double.IsFinite(top) || !double.IsFinite(right) || !double.IsFinite(bottom)
            || left < 0 || top < 0 || right <= left || bottom <= top)
        {
            throw new TemplateRuleViolationException("TPL_LAYOUT_INTENT_INVALID");
        }

        return new TemplateRect(left, top, right, bottom);
    }
}

public sealed record LayoutIntent
{
    private LayoutIntent(
        string intent,
        double scale,
        double offsetX,
        double offsetY,
        TemplateRect safeBounds)
    {
        Intent = intent;
        Scale = scale;
        OffsetX = offsetX;
        OffsetY = offsetY;
        SafeBounds = safeBounds;
    }

    public string Intent { get; }

    public double Scale { get; }

    public double OffsetX { get; }

    public double OffsetY { get; }

    public TemplateRect SafeBounds { get; }

    public static LayoutIntent Create(
        string intent,
        double scale,
        double offsetX,
        double offsetY,
        TemplateRect safeBounds)
    {
        if (!double.IsFinite(scale) || scale <= 0 || scale > 20
            || !double.IsFinite(offsetX) || !double.IsFinite(offsetY))
        {
            throw new TemplateRuleViolationException("TPL_LAYOUT_INTENT_INVALID");
        }

        return new LayoutIntent(
            TemplateValueRules.RequiredText(intent, 128, "TPL_LAYOUT_INTENT_INVALID"),
            scale,
            offsetX,
            offsetY,
            safeBounds);
    }
}

public sealed record RevisionTransitionEvidence(
    bool SourceReadable = false,
    bool CopyMatchesSource = false,
    bool StructureReadable = false,
    bool RegisteredFramesHaveEvidence = false,
    bool CalibrationComplete = false,
    bool ConfigurationValid = false,
    bool CommitArtifactsConsistent = false);
