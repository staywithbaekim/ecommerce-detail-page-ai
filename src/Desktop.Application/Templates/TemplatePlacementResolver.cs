using Desktop.Application.Psd;
using Desktop.Application.Rendering;
using Desktop.Domain.Templates;

namespace Desktop.Application.Templates;

/// <summary>Desktop-only layout math. The adapter receives the final whole-image rectangle.</summary>
public static class TemplatePlacementResolver
{
    public const string Version = "subject-placement/v1";
    private const decimal CoverageBleedPixels = 2m;
    private const decimal RoundingTolerance = .000000001m;

    public static RenderTargetPlacement Resolve(FrameBinding binding, TemplateRenderSource source,
        TemplateRenderSubjectGeometry? subject, PsdInspectionDocument inspection)
    {
        var layout = binding.LayoutIntent;
        var area = ResolveFrameBounds(binding, inspection);
        if (source.PixelWidth <= 0 || source.PixelHeight <= 0
            || !double.IsFinite(area.Left) || !double.IsFinite(area.Top)
            || !double.IsFinite(area.Right) || !double.IsFinite(area.Bottom)
            || area.Left < 0 || area.Top < 0 || area.Right <= area.Left || area.Bottom <= area.Top
            || area.Right > inspection.Canvas.Width || area.Bottom > inspection.Canvas.Height
            || subject is not null && (!subject.IsValid || subject.AssetResourceId != source.ResourceId))
            throw Invalid("TPL_RENDER_GEOMETRY_INVALID");
        if (Math.Abs(layout.OffsetX) >= (double)decimal.MaxValue || Math.Abs(layout.OffsetY) >= (double)decimal.MaxValue)
            throw Invalid("TPL_RENDER_LAYOUT_REVIEW_REQUIRED");
        var complete = layout.Intent is "subject_complete" or "full_product" or "hero_centered"
            or "grid_top_left" or "grid_top_right" or "grid_bottom_left" or "grid_bottom_right";
        if (!complete && layout.Intent is not ("subject_cover" or "detail_crop"))
            throw Invalid("TPL_RENDER_LAYOUT_REVIEW_REQUIRED");

        decimal left = (decimal)area.Left, top = (decimal)area.Top;
        decimal right = (decimal)area.Right, bottom = (decimal)area.Bottom;
        var frameWidth = right - left; var frameHeight = bottom - top;
        // For an existing clipped layer, overscan slightly beyond its clipping
        // window to prevent rounding/interpolation from exposing the old image.
        // An unclipped layer must remain inside its calibrated safe bounds.
        var bleed = binding.ExpectedLayer.Clipping ? CoverageBleedPixels : 0m;
        var coverageLeft = left - bleed; var coverageTop = top - bleed;
        var coverageRight = right + bleed; var coverageBottom = bottom + bleed;
        var coverageWidth = coverageRight - coverageLeft; var coverageHeight = coverageBottom - coverageTop;
        var width = (decimal)source.PixelWidth; var height = (decimal)source.PixelHeight;
        var cover = Math.Max(coverageWidth / width, coverageHeight / height);
        var preferred = cover * (decimal)layout.Scale;
        var offsetX = (decimal)layout.OffsetX; var offsetY = (decimal)layout.OffsetY;
        var learned = binding.SelectionRequirement.Criteria.Any(item => item.Key == "reference_only" && item.Value == "true");
        if (learned)
        {
            // Learned offsets were measured from the reference SO's top-left, unlike the manual preview.
            // Use the exact registered transform to recover its center and zoom preference.
            var layer = inspection.Layers.SingleOrDefault(item => item.LayerId == binding.ExpectedLayer.LayerId
                && item.Path.Select(segment => segment + 1).SequenceEqual(binding.LayerPath.Segments));
            var box = layer?.SmartObject?.TransformBox;
            if (box is not { Count: 8 } || box.Any(value => !double.IsFinite(value))
                || box[0] != box[6] || box[1] != box[3] || box[2] != box[4] || box[5] != box[7]
                || box[2] <= box[0] || box[5] <= box[1]) throw Invalid("TPL_RENDER_LAYOUT_REVIEW_REQUIRED");
            var referenceWidth = (decimal)(box[2] - box[0]); var referenceHeight = (decimal)(box[5] - box[1]);
            var oldScale = Math.Clamp(Math.Max(referenceWidth / frameWidth, referenceHeight / frameHeight), .01m, 20m);
            var referenceZoom = Math.Min(referenceWidth / frameWidth, referenceHeight / frameHeight);
            preferred = cover * referenceZoom * (decimal)layout.Scale / oldScale;
            offsetX += (referenceWidth - frameWidth) / 2;
            offsetY += (referenceHeight - frameHeight) / 2;
            if (subject is null) { preferred = cover; offsetX = 0; offsetY = 0; }
        }

        var scale = Math.Max(cover, preferred);
        var keepSubjectInsideFrame = false;
        if (subject is not null && complete)
        {
            var maximum = Math.Min(coverageWidth / (width * (subject.Right - subject.Left)),
                coverageHeight / (height * (subject.Bottom - subject.Top)));
            keepSubjectInsideFrame = (cover - maximum) * Math.Max(width, height) <= RoundingTolerance;
            if (keepSubjectInsideFrame)
                scale = Math.Max(cover, Math.Min(scale, maximum));
        }
        var placedWidth = width * scale; var placedHeight = height * scale;
        var minLeft = coverageRight - placedWidth; var maxLeft = coverageLeft;
        var minTop = coverageBottom - placedHeight; var maxTop = coverageTop;
        var desiredLeft = left + frameWidth / 2 - placedWidth * (subject is null ? .5m : (subject.Left + subject.Right) / 2) + offsetX;
        var desiredTop = top + frameHeight / 2 - placedHeight * (subject is null ? .5m : (subject.Top + subject.Bottom) / 2) + offsetY;
        if (subject is not null && keepSubjectInsideFrame)
        {
            minLeft = Math.Max(minLeft, coverageLeft - placedWidth * subject.Left);
            maxLeft = Math.Min(maxLeft, coverageRight - placedWidth * subject.Right);
            minTop = Math.Max(minTop, coverageTop - placedHeight * subject.Top);
            maxTop = Math.Min(maxTop, coverageBottom - placedHeight * subject.Bottom);
        }
        if (minLeft - maxLeft > RoundingTolerance || minTop - maxTop > RoundingTolerance)
            throw Invalid("TPL_RENDER_LAYOUT_REVIEW_REQUIRED");
        // Decimal division can separate mathematically equal endpoints by a subpixel rounding residue.
        maxLeft = Math.Max(minLeft, maxLeft); maxTop = Math.Max(minTop, maxTop);
        return new(source.PixelWidth, source.PixelHeight, Math.Clamp(desiredLeft, minLeft, maxLeft),
            Math.Clamp(desiredTop, minTop, maxTop), placedWidth, placedHeight);
    }

    internal static TemplateRect ResolveFrameBounds(FrameBinding binding, PsdInspectionDocument inspection)
    {
        var layer = inspection.Layers.SingleOrDefault(item => item.LayerId == binding.ExpectedLayer.LayerId);
        if (layer is null) return binding.LayoutIntent.SafeBounds;
        if (!layer.Path.Select(segment => segment + 1).SequenceEqual(binding.LayerPath.Segments))
            throw Invalid("TPL_RENDER_GEOMETRY_INVALID");

        // Use persisted final-page evidence for both pixel layers and smart objects.
        // This also corrects old bindings when making a new plan without rewriting old tasks.
        PsdLayerBounds? bounds;
        if (layer.PixelEvidence.Available)
        {
            if (!layer.PixelEvidence.HasMeaningfulCompositeContribution)
                throw Invalid("TPL_RENDER_LAYOUT_REVIEW_REQUIRED");
            bounds = VisibleFrameBounds(layer);
        }
        else
        {
            if (layer.HasMask || layer.HasVectorMask)
                throw Invalid("TPL_RENDER_LAYOUT_REVIEW_REQUIRED");
            bounds = layer.Clipping && layer.ClippingBase is { EffectiveVisible: true } window
                ? window.Bounds : null;
        }
        // Legacy explicit calibration remains usable when inspection lacks display evidence;
        // never substitute the raw photo-layer rectangle for an explicit frame here.
        if (bounds is null) return binding.LayoutIntent.SafeBounds;
        var left = Math.Max(0, bounds.Left); var top = Math.Max(0, bounds.Top);
        var right = Math.Min(inspection.Canvas.Width, bounds.Right);
        var bottom = Math.Min(inspection.Canvas.Height, bounds.Bottom);
        if (right <= left || bottom <= top) throw Invalid("TPL_RENDER_GEOMETRY_INVALID");
        return TemplateRect.Create(left, top, right, bottom);
    }

    internal static PsdLayerBounds? VisibleFrameBounds(PsdLayerInspection layer)
    {
        if (layer.PixelEvidence.Available && !layer.PixelEvidence.HasMeaningfulCompositeContribution)
            return null;
        // An explicit clipping window belongs to the template, not to the old photo.
        // A photo that currently underfills that window must not shrink the replacement frame.
        if (layer.Clipping && !layer.HasMask && !layer.HasVectorMask
            && layer.ClippingBase is { EffectiveVisible: true, HasMask: false, HasVectorMask: false } window)
            return window.Bounds;
        return layer.PixelEvidence.HasMeaningfulCompositeContribution
            ? layer.PixelEvidence.CompositeContributionBounds : null;
    }

    private static TemplateRuleViolationException Invalid(string code) => new(code);
}
