using Desktop.Application.Psd;
using Desktop.Application.Rendering;
using Desktop.Domain.Templates;

namespace Desktop.Application.Templates;

/// <summary>Desktop-only layout math. The adapter receives the final whole-image rectangle.</summary>
public static class TemplatePlacementResolver
{
    public const string Version = "subject-placement/v1";
    private const decimal RoundingTolerance = .000000001m;

    public static RenderTargetPlacement Resolve(FrameBinding binding, TemplateRenderSource source,
        TemplateRenderSubjectGeometry? subject, PsdInspectionDocument inspection)
    {
        var layout = binding.LayoutIntent;
        var area = layout.SafeBounds;
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
        var width = (decimal)source.PixelWidth; var height = (decimal)source.PixelHeight;
        var cover = Math.Max(frameWidth / width, frameHeight / height);
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
        if (subject is not null && complete)
        {
            var maximum = Math.Min(frameWidth / (width * (subject.Right - subject.Left)),
                frameHeight / (height * (subject.Bottom - subject.Top)));
            // No solution can simultaneously cover the frame and preserve the complete subject.
            if ((cover - maximum) * Math.Max(width, height) > RoundingTolerance)
                throw Invalid("TPL_RENDER_LAYOUT_REVIEW_REQUIRED");
            scale = Math.Max(cover, Math.Min(scale, maximum));
        }
        var placedWidth = width * scale; var placedHeight = height * scale;
        var minLeft = right - placedWidth; var maxLeft = left;
        var minTop = bottom - placedHeight; var maxTop = top;
        var desiredLeft = left + frameWidth / 2 - placedWidth * (subject is null ? .5m : (subject.Left + subject.Right) / 2) + offsetX;
        var desiredTop = top + frameHeight / 2 - placedHeight * (subject is null ? .5m : (subject.Top + subject.Bottom) / 2) + offsetY;
        if (subject is not null && complete)
        {
            minLeft = Math.Max(minLeft, left - placedWidth * subject.Left);
            maxLeft = Math.Min(maxLeft, right - placedWidth * subject.Right);
            minTop = Math.Max(minTop, top - placedHeight * subject.Top);
            maxTop = Math.Min(maxTop, bottom - placedHeight * subject.Bottom);
        }
        if (minLeft - maxLeft > RoundingTolerance || minTop - maxTop > RoundingTolerance)
            throw Invalid("TPL_RENDER_LAYOUT_REVIEW_REQUIRED");
        // Decimal division can separate mathematically equal endpoints by a subpixel rounding residue.
        maxLeft = Math.Max(minLeft, maxLeft); maxTop = Math.Max(minTop, maxTop);
        return new(source.PixelWidth, source.PixelHeight, Math.Clamp(desiredLeft, minLeft, maxLeft),
            Math.Clamp(desiredTop, minTop, maxTop), placedWidth, placedHeight);
    }

    private static TemplateRuleViolationException Invalid(string code) => new(code);
}
