using Desktop.Application.Psd;
using Desktop.Application.Templates;
using Desktop.Domain.Templates;

namespace Desktop.Application.Tests;

public sealed class TemplatePlacementResolverTests
{
    [Theory]
    [InlineData(1000, 1000, .1, .1, .9, .9)]
    [InlineData(2000, 1000, .4, .1, .7, .9)]
    [InlineData(1000, 2000, .1, .2, .9, .6)]
    public void CompleteSubjectUsesWholeSafeSolutionWithoutWhiteEdges(int width, int height,
        double left, double top, double right, double bottom)
    {
        var subject = new TemplateRenderSubjectGeometry("asset", (decimal)left, (decimal)top, (decimal)right, (decimal)bottom, 1);
        var placement = TemplatePlacementResolver.Resolve(Binding(scale: 9, offsetX: 10000, offsetY: -10000),
            new("asset", new string('a', 64), 64, PixelWidth: width, PixelHeight: height), subject, Inspection());
        Assert.True(placement.Left <= 100 && placement.Top <= 100);
        Assert.True(placement.Left + placement.Width >= 900 && placement.Top + placement.Height >= 900);
        Assert.True(placement.Left + placement.Width * subject.Left >= 100);
        Assert.True(placement.Top + placement.Height * subject.Top >= 100);
        Assert.True(placement.Left + placement.Width * subject.Right <= 900);
        Assert.True(placement.Top + placement.Height * subject.Bottom <= 900);
        Assert.InRange(Math.Abs(placement.Width / width - placement.Height / height), 0, .000000001m);
    }

    [Fact]
    public void RepeatingScaleAtExactBoundaryDoesNotRejectAFeasiblePlacement()
    {
        var placement = TemplatePlacementResolver.Resolve(Binding(),
            new("asset", new string('a', 64), 64, PixelWidth: 1233, PixelHeight: 1233),
            new("asset", 0, 0, 1, 1, 1), Inspection());
        Assert.InRange(Math.Abs(placement.Left - 100), 0, .000000001m);
        Assert.InRange(Math.Abs(placement.Top - 100), 0, .000000001m);
        Assert.InRange(Math.Abs(placement.Width - 800), 0, .000000001m);
        Assert.InRange(Math.Abs(placement.Height - 800), 0, .000000001m);
    }

    [Fact]
    public void ImpossibleCompleteSubjectIsReviewRequiredButExplicitDetailCropCanFill()
    {
        var source = new TemplateRenderSource("asset", new string('a', 64), 64, PixelWidth: 2000, PixelHeight: 1000);
        var subject = new TemplateRenderSubjectGeometry("asset", 0, 0, 1, 1, 1);
        var error = Assert.Throws<TemplateRuleViolationException>(() =>
            TemplatePlacementResolver.Resolve(Binding(), source, subject, Inspection()));
        Assert.Equal("TPL_RENDER_LAYOUT_REVIEW_REQUIRED", error.Code);
        var placement = TemplatePlacementResolver.Resolve(Binding("detail_crop"), source, subject, Inspection());
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
        double offsetX = 0, double offsetY = 0, bool learned = false) => FrameBinding.Create("frame", "page", LayerPath.Create([1]),
            ExpectedLayerEvidence.Create(1, "slot", true, false, false, false, 255, "normal", LayerStructureSignature.Create("sha256:" + new string('b', 64))),
            [], SelectionRequirement.Create("商品主体", learned ? [SelectionCriterion.Create("reference_only", "true")] : []),
            LayoutIntent.Create(intent, scale, offsetX, offsetY, TemplateRect.Create(100, 100, 900, 900)));

    private static PsdInspectionDocument Inspection() => new(new string('b', 64), 64, new(1000, 1000, 8, "RGB"),
        0, 0, 0, 0, 0, new string('b', 64), new(0, 0, 0), 0, null, []);
}
