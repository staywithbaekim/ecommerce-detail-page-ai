using Desktop.Application.Psd;
using Desktop.Application.Templates;
using Desktop.Domain.Templates;

namespace Desktop.Application.Tests;

public sealed class TemplatePlacementResolverTests
{
    [Theory]
    [InlineData(TemplateLayerKind.SmartObject, false)]
    [InlineData(TemplateLayerKind.SmartObject, true)]
    [InlineData(TemplateLayerKind.Pixel, false)]
    [InlineData(TemplateLayerKind.Pixel, true)]
    public void OldLayerSizedCalibrationUsesMatchingVisibleWindowForAllTargetKinds(TemplateLayerKind kind, bool masked)
    {
        var legacy = Binding(clipping: true);
        var binding = FrameBinding.Create(legacy.FrameId, legacy.PageId, legacy.LayerPath,
            ExpectedLayerEvidence.Create(1, "slot", true, true, masked, masked, 255, "norm",
                legacy.ExpectedLayer.StructureSignature, kind), [], legacy.SelectionRequirement, legacy.LayoutIntent);
        var window = new PsdLayerBounds(240, 320, 480, 740);
        var layer = new PsdLayerInspection([0], 1, "slot", kind == TemplateLayerKind.Pixel ? "pixel" : "smartobject",
            false, true, true, true, 255, 255, "norm", new(-400, -500, 1600, 1800), masked, masked,
            new([1], 2, "window", "pixel", true, true, window, false, false, 255, "norm"), null)
            { PixelEvidence = new(true, 4600000, 100800, window) };
        var source = new TemplateRenderSource("asset", new string('a', 64), 64, PixelWidth: 1200, PixelHeight: 1800);
        var subject = new TemplateRenderSubjectGeometry("asset", .25m, .2m, .75m, .95m, 1);
        var placement = TemplatePlacementResolver.Resolve(binding, source, subject, Inspection() with { Layers = [layer] });
        Assert.InRange(Math.Abs(placement.Height - 424m), 0, .000000001m);
        Assert.True(placement.Left + placement.Width * subject.Left >= window.Left);
        Assert.True(placement.Left + placement.Width * subject.Right <= window.Right);
        Assert.True(placement.Top + placement.Height * subject.Top >= window.Top);
        Assert.True(placement.Top + placement.Height * subject.Bottom <= window.Bottom);
        Assert.Equal(TemplateRect.Create(100, 100, 900, 900), binding.LayoutIntent.SafeBounds);
    }

    [Fact]
    public void OlderInspectionUsesStructuralClippingWindowWithoutTakingPhotoLayerBounds()
    {
        var layer = new PsdLayerInspection([0], 1, "slot", "smartobject", false, true, true, true,
            255, 255, "norm", new(-900, -900, 1900, 1900), false, false,
            new([1], 2, "window", "pixel", true, true, new(200, 300, 400, 600), false, false, 255, "norm"), null);
        var inspection = Inspection() with { Layers = [layer] };
        var source = new TemplateRenderSource("asset", new string('a', 64), 64, PixelWidth: 1200, PixelHeight: 1800);
        var placement = TemplatePlacementResolver.Resolve(Binding(clipping: true), source, null, inspection);
        Assert.Equal(198m, placement.Left);
        Assert.Equal(297m, placement.Top);
        Assert.Equal(204m, placement.Width);
        Assert.Equal(306m, placement.Height);
        var underfilled = layer with { Bounds = new(240, 350, 340, 450),
            PixelEvidence = new(true, 10000, 10000, new(240, 350, 340, 450)) };
        Assert.Equal(placement, TemplatePlacementResolver.Resolve(Binding(clipping: true), source, null,
            inspection with { Layers = [underfilled] }));
        var wrongPath = layer with { Path = [3] };
        Assert.Throws<TemplateRuleViolationException>(() => TemplatePlacementResolver.Resolve(
            Binding(clipping: true), source, null, inspection with { Layers = [wrongPath] }));
        Assert.Throws<TemplateRuleViolationException>(() => TemplatePlacementResolver.Resolve(
            Binding(clipping: true), source, null, inspection with { Layers = [layer with { PixelEvidence = new(true, 9000000, 0, null) }] }));
    }

    [Theory]
    [InlineData(1000, 1000, .1, .1, .9, .9)]
    [InlineData(2000, 1000, .4, .1, .7, .9)]
    [InlineData(1000, 2000, .1, .2, .9, .6)]
    public void CompleteSubjectUsesWholeSafeSolutionWithoutWhiteEdges(int width, int height,
        double left, double top, double right, double bottom)
    {
        var subject = new TemplateRenderSubjectGeometry("asset", (decimal)left, (decimal)top, (decimal)right, (decimal)bottom, 1);
        var placement = TemplatePlacementResolver.Resolve(Binding(scale: 9, offsetX: 10000, offsetY: -10000, clipping: true),
            new("asset", new string('a', 64), 64, PixelWidth: width, PixelHeight: height), subject, Inspection());
        Assert.True(placement.Left <= 98 && placement.Top <= 98);
        Assert.True(placement.Left + placement.Width >= 902 && placement.Top + placement.Height >= 902);
        Assert.True(placement.Left + placement.Width * subject.Left >= 98);
        Assert.True(placement.Top + placement.Height * subject.Top >= 98);
        Assert.True(placement.Left + placement.Width * subject.Right <= 902);
        Assert.True(placement.Top + placement.Height * subject.Bottom <= 902);
        Assert.InRange(Math.Abs(placement.Width / width - placement.Height / height), 0, .000000001m);
    }

    [Fact]
    public void RepeatingScaleAtExactBoundaryDoesNotRejectAFeasiblePlacement()
    {
        var placement = TemplatePlacementResolver.Resolve(Binding(clipping: true),
            new("asset", new string('a', 64), 64, PixelWidth: 1233, PixelHeight: 1233),
            new("asset", 0, 0, 1, 1, 1), Inspection());
        Assert.InRange(Math.Abs(placement.Left - 98), 0, .000000001m);
        Assert.InRange(Math.Abs(placement.Top - 98), 0, .000000001m);
        Assert.InRange(Math.Abs(placement.Width - 804), 0, .000000001m);
        Assert.InRange(Math.Abs(placement.Height - 804), 0, .000000001m);
    }

    [Fact]
    public void InfeasibleCompleteSubjectStillProducesCenteredCoverPlacement()
    {
        var source = new TemplateRenderSource("asset", new string('a', 64), 64, PixelWidth: 2000, PixelHeight: 1000);
        var subject = new TemplateRenderSubjectGeometry("asset", 0, 0, 1, 1, 1);
        var placement = TemplatePlacementResolver.Resolve(Binding(), source, subject, Inspection());
        Assert.Equal(-300m, placement.Left); Assert.Equal(1600m, placement.Width);
        Assert.Equal(100m, placement.Top); Assert.Equal(800m, placement.Height);
    }

    [Fact]
    public void MissingGeometryUsesDimensionsAndDoesNotInventSubject()
    {
        var source = new TemplateRenderSource("asset", new string('a', 64), 64, PixelWidth: 2000, PixelHeight: 1000);
        var placement = TemplatePlacementResolver.Resolve(Binding(), source, null, Inspection());
        Assert.Equal(-300m, placement.Left); Assert.Equal(100m, placement.Top);
        Assert.Equal(1600m, placement.Width); Assert.Equal(800m, placement.Height);
        Assert.Throws<TemplateRuleViolationException>(() => TemplatePlacementResolver.Resolve(Binding(), source with { PixelWidth = 0 }, null, Inspection()));
    }

    [Fact]
    public void LearnedTopLeftOffsetIsNotTreatedAsManualCenterOffset()
    {
        var source = new TemplateRenderSource("asset", new string('a', 64), 64, PixelWidth: 2000, PixelHeight: 1000);
        var inspection = Inspection() with { Layers = [new([0], 1, "slot", "smartobject", false, true, true, false, 255, 255,
            "norm", new(100, 100, 900, 900), false, false, null,
            new("u", "source.jpg", "data", "jpg", "jpg", 64, false, [-300, 100, 1300, 100, 1300, 900, -300, 900]))] };
        var subject = new TemplateRenderSubjectGeometry("asset", .3m, .1m, .7m, .9m, 1);
        var learned = TemplatePlacementResolver.Resolve(Binding(scale: 2, offsetX: -400, learned: true), source, subject, inspection);
        Assert.Equal(-300m, learned.Left); Assert.Equal(1600m, learned.Width);
        Assert.Throws<TemplateRuleViolationException>(() => TemplatePlacementResolver.Resolve(
            Binding(learned: true), source, subject, Inspection()));
    }

    private static FrameBinding Binding(string intent = "subject_complete", double scale = 1,
        double offsetX = 0, double offsetY = 0, bool learned = false, bool clipping = false) => FrameBinding.Create("frame", "page", LayerPath.Create([1]),
            ExpectedLayerEvidence.Create(1, "slot", true, clipping, false, false, 255, "normal", LayerStructureSignature.Create("sha256:" + new string('b', 64))),
            [], SelectionRequirement.Create("商品主体", learned ? [SelectionCriterion.Create("reference_only", "true")] : []),
            LayoutIntent.Create(intent, scale, offsetX, offsetY, TemplateRect.Create(100, 100, 900, 900)));

    private static PsdInspectionDocument Inspection() => new(new string('b', 64), 64, new(1000, 1000, 8, "RGB"),
        0, 0, 0, 0, 0, new string('b', 64), new(0, 0, 0), 0, null, []);
}
