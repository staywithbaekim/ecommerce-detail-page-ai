using System.Text.Json;
using Desktop.Application.Rendering;
using Desktop.Application.SizeTables;
using Desktop.Contracts.Validation;
using Desktop.Domain.Templates;

namespace Desktop.Application.Tests;

public sealed class SizeTableCalibrationResolverTests
{
    internal static SizeTableTemplateCalibration Calibration()
    {
        var f = SizeTableInsertionTests.Fixture();
        var reference = new SizeTableTemplateLayer(2, 2, "group", "old table", f.Baseline.StructureSignature);
        return new("size-table-template/v1", f.Baseline.FileSha256, "confirmed table", reference, [reference],
            TemplateRect.Create(10, 20, 780, 1200),
            new("Microsoft YaHei UI", 24, 1000, 56, 24, 12, "#FFFFFFFF", "#FFF2F4F7", "#FF101828", "#FF98A2B3"));
    }

    [Theory]
    [InlineData(1000, 400)]
    [InlineData(1000, 4000)]
    public void SavedAreaProducesCompleteCenteredPlacementForEachActualTable(int width, int height)
    {
        var f = SizeTableInsertionTests.Fixture(); var calibration = Calibration();
        Assert.True(SizeTableCalibrationResolver.Matches(calibration, f.Baseline));
        Assert.True(SizeTableCalibrationResolver.Matches(calibration, f.Baseline with { StructureSignature = f.Baseline.StructureSignature[7..] }));
        var page = TemplatePage.Create(f.Target.PageId, f.Target.TemplateRevisionId, 0, "size",
            TemplatePageContentType.DynamicSizeTable, TemplateGenerationMode.OncePerProduct, null, true, "template", sizeTableCalibration: calibration);
        var raster = new SizeTableRasterizedResource("raster", "deterministic_size_table", "image/png", new string('a', 64),
            128, width, height, "sha256:" + new string('b', 64), "style", "layout", "raster.png");
        var target = SizeTableCalibrationResolver.Resolve(page, raster);
        var p = target.Insertion.Placement;
        Assert.Equal(width, p.SourceWidth); Assert.Equal(height, p.SourceHeight);
        Assert.Equal(395m, p.Left + p.Width / 2); Assert.Equal(610m, p.Top + p.Height / 2);
        Assert.InRange(p.Left, 10, 780); Assert.InRange(p.Top, 20, 1200);
        Assert.True(p.Left + p.Width <= 780 && p.Top + p.Height <= 1200);
        var migrated = new RenderPlanPlacementMigration(new RenderPlanValidator(new JsonSchemaContractValidator()))
            .MigrateSizeTableInsertion(f.Plan, target, f.Baseline, "new_plan", "new_attempt", "new_output", DateTimeOffset.UnixEpoch);
        Assert.True(RenderTemplateEvidenceValidator.Matches(migrated, f.Baseline));
        Assert.NotEqual(SizeTableCalibrationResolver.RasterStyle(calibration).StyleVersion,
            SizeTableCalibrationResolver.RasterStyle(calibration with { Style = calibration.Style with { FontSize = 25 } }).StyleVersion);
    }

    [Theory]
    [InlineData("hash")]
    [InlineData("layer")]
    [InlineData("outside")]
    [InlineData("duplicate")]
    [InlineData("collision")]
    [InlineData("version")]
    public void StaleOrUnsafeCalibrationCannotMatchTemplate(string fault)
    {
        var f = SizeTableInsertionTests.Fixture(); var c = Calibration();
        c = fault switch
        {
            "hash" => c with { TemplateSha256 = new string('0', 64) },
            "layer" => c with { AboveLayer = c.AboveLayer with { LayerId = 999 } },
            "outside" => c with { SafeBounds = TemplateRect.Create(0, 0, 9999, 9999) },
            "duplicate" => c with { HiddenLayers = [c.AboveLayer, c.AboveLayer] },
            "collision" => c with { LayerName = "header" },
            _ => c with { Version = "unknown" }
        };
        Assert.False(SizeTableCalibrationResolver.Matches(c, f.Baseline));
    }

    [Fact]
    public void CalibrationSurvivesReorderAndInheritanceWhileLegacyPageStaysUncalibrated()
    {
        var c = Calibration();
        var old = TemplatePage.Create("page", "revision", 0, "size", TemplatePageContentType.DynamicSizeTable,
            TemplateGenerationMode.OncePerProduct, null, true, "resource");
        var draft = TemplateRevision.CreateDraft("revision", "group", 1, DateTimeOffset.UnixEpoch, [old]);
        var updated = draft.CalibrateSizeTable("page", c).ReorderPages(["page"]);
        Assert.Null(old.SizeTableCalibration);
        Assert.Equal(JsonSerializer.Serialize(c), JsonSerializer.Serialize(updated.Pages[0].InheritTo("next").SizeTableCalibration));
        Assert.Equal(c, updated.ConfigurePage("page", "normal", TemplatePageContentType.NormalComposition,
            TemplateGenerationMode.OncePerProduct, null, true).Pages[0].SizeTableCalibration);
    }

    [Fact]
    public void InactiveCalibrationSurvivesSwitchesButCannotAuthorizeOrdinaryPageInsertion()
    {
        var calibration = Calibration();
        var size = TemplatePage.Create("page", "revision", 0, "size", TemplatePageContentType.DynamicSizeTable,
            TemplateGenerationMode.OncePerProduct, null, true, "resource", sizeTableCalibration: calibration);
        var normal = size.Reconfigure(0, "size", TemplatePageContentType.NormalComposition,
            TemplateGenerationMode.OncePerProduct, null, true);
        Assert.Equal(calibration, normal.SizeTableCalibration);
        Assert.Equal(TemplatePageTypeRecognition.SizeTablePage, normal.TypeRecognition);
        var raster = new SizeTableRasterizedResource("raster", "deterministic_size_table", "image/png", new string('a', 64),
            128, 1000, 400, "sha256:" + new string('b', 64), "style", "layout", "raster.png");
        var error = Assert.Throws<InvalidOperationException>(() => SizeTableCalibrationResolver.Resolve(normal, raster));
        Assert.Equal("SIZE_TABLE_RENDER_TARGET_REQUIRED", error.Message);
        var restored = normal.InheritTo("next").Reconfigure(0, "size", TemplatePageContentType.DynamicSizeTable,
            TemplateGenerationMode.OncePerProduct, null, true);
        Assert.Equal(calibration, restored.SizeTableCalibration);
        Assert.Equal("next", SizeTableCalibrationResolver.Resolve(restored, raster).TemplateRevisionId);
    }
}
