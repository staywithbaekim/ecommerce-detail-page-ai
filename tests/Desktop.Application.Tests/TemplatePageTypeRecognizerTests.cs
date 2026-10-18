using Desktop.Application.Psd;
using Desktop.Application.Templates;
using Desktop.Domain.Templates;

namespace Desktop.Application.Tests;

public sealed class TemplatePageTypeRecognizerTests
{
    [Theory]
    [InlineData("尺码表", TemplatePageTypeRecognition.SizeTablePage)]
    [InlineData("F-尺码表-【L-5XL】", TemplatePageTypeRecognition.SizeTablePage)]
    [InlineData("尺寸表", TemplatePageTypeRecognition.SizeTablePage)]
    [InlineData("SIZE CHART", TemplatePageTypeRecognition.SizeTablePage)]
    [InlineData("size_table", TemplatePageTypeRecognition.SizeTablePage)]
    [InlineData("详情正反面", TemplatePageTypeRecognition.OrdinaryPage)]
    [InlineData("未命名", TemplatePageTypeRecognition.OrdinaryPage)]
    public void PageLabelUsesSizeEvidenceAndOtherwiseKeepsOrdinaryBaseline(string name, TemplatePageTypeRecognition expected)
    {
        Assert.Equal(expected, TemplatePageTypeRecognizer.Recognize(name, Inspection([])));
    }

    [Theory]
    [InlineData(true, true, TemplatePageTypeRecognition.SizeTablePage)]
    [InlineData(false, false, TemplatePageTypeRecognition.OrdinaryPage)]
    [InlineData(true, false, TemplatePageTypeRecognition.OrdinaryPage)]
    public void OnlyEffectiveVisibleSizeLayersAffectClassification(bool visible, bool effectiveVisible, TemplatePageTypeRecognition expected)
    {
        var layer = new PsdLayerInspection([0], 1, "尺码数据", "group", true, visible, effectiveVisible,
            false, 255, 255, "norm", new(10, 20, 700, 800), false, false, null, null);
        Assert.Equal(expected, TemplatePageTypeRecognizer.Recognize("未命名", Inspection([layer])));
    }

    [Fact]
    public void EmbeddedFilenameIsEvidenceButOrdinarySmartObjectsAreNotSizeSlots()
    {
        var layer = new PsdLayerInspection([0], 1, "图层", "smartobject", false, true, true,
            false, 255, 255, "norm", new(10, 20, 700, 800), false, false, null,
            new("embedded", "size chart.png", "data", "png", "png", 4, false, []));
        Assert.Equal(TemplatePageTypeRecognition.SizeTablePage, TemplatePageTypeRecognizer.Recognize("未命名", Inspection([layer])));
        layer = layer with { SmartObject = layer.SmartObject! with { Filename = "photo.png" } };
        Assert.Equal(TemplatePageTypeRecognition.OrdinaryPage, TemplatePageTypeRecognizer.Recognize("未命名", Inspection([layer])));
    }

    [Theory]
    [InlineData("未命名", "中文")]
    [InlineData("06", "英文")]
    [InlineData("图层组 1", "单位")]
    [InlineData("详情页", "中文")]
    public void AnonymousSizeTemplateUsesAlignedMeasurementRows(string pageName, string language)
    {
        Assert.Equal(TemplatePageTypeRecognition.SizeTablePage,
            TemplatePageTypeRecognizer.Recognize(pageName, Inspection(MeasurementGrid(language))));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void MeasurementEvidenceSurvivesLayerOrderScalingAndSmallTextOffsets(int scale)
    {
        var layers = MeasurementGrid("中文").Select((layer, index) => layer with
        {
            Bounds = new(layer.Bounds.Left * scale + index % 2, layer.Bounds.Top * scale + index % 2,
                layer.Bounds.Right * scale + index % 2, layer.Bounds.Bottom * scale + index % 2)
        }).Reverse().ToArray();
        var inspection = Inspection(layers) with { Canvas = new(790 * scale, 1000 * scale, 8, "RGB") };
        Assert.Equal(TemplatePageTypeRecognition.SizeTablePage,
            TemplatePageTypeRecognizer.Recognize("未命名", inspection));
    }

    [Theory]
    [InlineData("模特尺码 M")]
    [InlineData("详情-尺码建议")]
    [InlineData("尺码齐全 多色可选")]
    [InlineData("尺码表仅供参考")]
    [InlineData("尺寸表面细节")]
    [InlineData("model measurements")]
    [InlineData("measurements.jpg backup")]
    [InlineData("oversize chart detail")]
    [InlineData("size chartreuse")]
    public void OrdinaryCopyIsNotAnExplicitSizeTableLabel(string copy)
    {
        Assert.Equal(TemplatePageTypeRecognition.OrdinaryPage,
            TemplatePageTypeRecognizer.Recognize(copy, Inspection([])));
        Assert.Equal(TemplatePageTypeRecognition.OrdinaryPage,
            TemplatePageTypeRecognizer.Recognize("详情", Inspection([Text(0, copy, 0, 0), Photo(1), Photo(2)])));
        Assert.Equal(TemplatePageTypeRecognition.OrdinaryPage,
            TemplatePageTypeRecognizer.Recognize("未命名", Inspection([Photo(1) with
            {
                SmartObject = Photo(1).SmartObject! with { Filename = copy + ".png" }
            }])));
    }

    [Theory]
    [InlineData("hidden")]
    [InlineData("hidden-parent")]
    [InlineData("transparent")]
    [InlineData("transparent-fill")]
    [InlineData("hidden-cell")]
    [InlineData("empty")]
    [InlineData("outside-canvas")]
    [InlineData("one-row")]
    [InlineData("one-measurement")]
    [InlineData("duplicate-measurement")]
    [InlineData("duplicate-size")]
    [InlineData("non-numeric")]
    [InlineData("missing-cell")]
    [InlineData("scattered-rows")]
    [InlineData("scattered-columns")]
    [InlineData("shared-cell")]
    [InlineData("inventory-table")]
    [InlineData("generic-layer-names")]
    [InlineData("raster-layers")]
    public void WeakOrUnrelatedGridEvidenceKeepsOrdinaryBaseline(string scenario)
    {
        var layers = MeasurementGrid("中文");
        switch (scenario)
        {
            case "hidden": layers = layers.Select(layer => layer with { Visible = false }).ToArray(); break;
            case "hidden-parent": layers = layers.Select(layer => layer with { EffectiveVisible = false }).ToArray(); break;
            case "transparent": layers = layers.Select(layer => layer with { Opacity = 0 }).ToArray(); break;
            case "transparent-fill": layers = layers.Select(layer => layer with { FillOpacity = 0 }).ToArray(); break;
            case "hidden-cell": layers[4] = layers[4] with { EffectiveVisible = false }; break;
            case "empty": layers = layers.Select(layer => layer with { Bounds = new(0, 0, 0, 0) }).ToArray(); break;
            case "outside-canvas": layers = layers.Select(layer => layer with { Bounds = new(900, 0, 960, 20) }).ToArray(); break;
            case "one-row": layers = layers[..6]; break;
            case "one-measurement": layers[2] = layers[2] with { Name = "备注" }; break;
            case "duplicate-measurement": layers[2] = layers[2] with { Name = "胸围" }; break;
            case "duplicate-size": layers[6] = layers[6] with { Name = "S" }; break;
            case "non-numeric": layers[4] = layers[4] with { Name = "待填写" }; break;
            case "missing-cell": layers = layers.Where((_, index) => index != 5).ToArray(); break;
            case "scattered-rows": layers[5] = layers[5] with { Bounds = new(400, 450, 460, 470) }; break;
            case "scattered-columns": layers[8] = layers[8] with { Bounds = new(600, 220, 660, 240) }; break;
            case "shared-cell":
                layers[2] = layers[2] with { Bounds = new(310, 100, 370, 120) };
                layers[4] = layers[4] with { Bounds = new(250, 160, 370, 180) };
                layers[7] = layers[7] with { Bounds = new(250, 220, 370, 240) };
                layers = layers.Where((_, index) => index != 5 && index != 8).ToArray();
                break;
            case "inventory-table":
                layers[1] = layers[1] with { Name = "库存" };
                layers[2] = layers[2] with { Name = "价格" };
                break;
            case "generic-layer-names": layers = layers.Select(layer => layer with { Name = "图层 " + layer.LayerId }).ToArray(); break;
            case "raster-layers": layers = layers.Select(layer => layer with { Kind = "pixel" }).ToArray(); break;
        }

        Assert.Equal(TemplatePageTypeRecognition.OrdinaryPage,
            TemplatePageTypeRecognizer.Recognize("商品展示", Inspection(layers)));
    }

    [Theory]
    [InlineData("SIZE-CHART")]
    [InlineData("size_chart")]
    [InlineData("Size Table")]
    [InlineData("measurement chart")]
    [InlineData("measurements")]
    [InlineData("尺码数据")]
    [InlineData("06-尺码表-【L-5XL】")]
    public void ExplicitLabelsRemainRecognizable(string label)
    {
        Assert.Equal(TemplatePageTypeRecognition.SizeTablePage,
            TemplatePageTypeRecognizer.Recognize(label, Inspection([])));
    }

    [Theory]
    [InlineData(false, true, 255)]
    [InlineData(true, false, 255)]
    [InlineData(true, true, 0)]
    public void InactiveEmbeddedSizeFilenameCannotChangeOrdinaryPage(bool visible, bool effectiveVisible, int opacity)
    {
        var layer = Text(0, "图层 1", 100, 100) with
        {
            Kind = "smartobject", Visible = visible, EffectiveVisible = effectiveVisible, Opacity = opacity,
            SmartObject = new("embedded", "size chart.png", "data", "png", "png", 4, false, [])
        };
        Assert.Equal(TemplatePageTypeRecognition.OrdinaryPage,
            TemplatePageTypeRecognizer.Recognize("详情", Inspection([layer])));
    }

    // Synthetic inspected metadata only: names are PSD layer names, not OCR or target-product values.
    private static PsdLayerInspection[] MeasurementGrid(string language)
    {
        string[] labels = language switch
        {
            "英文" => ["Item", "Chest", "Length", "S", "88.5", "62", "XL", "96", "66"],
            "单位" => ["项目", "胸围（cm）", "衣长(cm)", "L", "96", "66", "2XL", "104", "70"],
            _ => ["项目", "胸围", "衣长", "S", "88", "62", "M", "92", "64"]
        };
        return labels.Select((label, index) => Text(index, label, 100 + index % 3 * 150, 100 + index / 3 * 60)).ToArray();
    }

    private static PsdLayerInspection Text(int index, string name, int left, int top) => new(
        [index], index + 1, name, "type", false, true, true, false, 255, 255, "norm",
        new(left, top, left + 60, top + 20), false, false, null, null);

    private static PsdLayerInspection Photo(int index) => Text(index, "图层 " + index, index * 100, 400) with
    {
        Kind = "smartobject",
        SmartObject = new("embedded_" + index, "photo.jpg", "data", "jpg", "jpg", 4096, false, [])
        {
            EmbeddedContentAvailable = true, EmbeddedContentSha256 = new string('c', 64)
        },
        PixelEvidence = new(true, 1200, 1200, new(index * 100, 400, index * 100 + 60, 420))
    };

    private static PsdInspectionDocument Inspection(PsdLayerInspection[] layers) => new(
        new string('a', 64), 4, new(790, 1000, 8, "RGB"), layers.Length, layers.Count(layer => layer.SmartObject is not null),
        0, 0, 0, new string('b', 64), new(1, 1, 2), 1024, null, layers);
}
