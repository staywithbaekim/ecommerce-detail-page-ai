using System.Text.Json.Nodes;
using Desktop.Application.Psd;
using Desktop.Application.Rendering;
using Desktop.Domain.Templates;

namespace Desktop.Application.Tests;

public sealed class RenderTemplateEvidenceValidatorTests
{
    private const string Signature = "abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789";
    private static JsonNode Plan() => JsonNode.Parse(File.ReadAllText(Path.Combine(
        AppContext.BaseDirectory, "Fixtures", "render-plan-v1.valid.example.json")))!;
    private static PsdInspectionDocument Inspection() => new(
        "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef", 1048576,
        new(790, 1252, 8, "RGB"), 1, 1, 0, 0, 0, Signature.ToUpperInvariant(), new(1, 1, 2), 1024, null,
        [new([1], 257, "product_frame", "smartobject", false, true, true, false, 255, 255, "norm",
            new(0, 0, 790, 1252), false, false, null, new("embedded-id", "source.jpg", "data", "jpg", "jpg", 128, false, []))]);

    [Fact]
    public void ExactPsdEvidenceClosesOneBasedPlanWithoutModifyingInspection()
    {
        var inspection = Inspection();
        Assert.True(RenderTemplateEvidenceValidator.Matches(Plan().ToJsonString(), inspection));
        Assert.Equal([1], inspection.Layers[0].Path);
        Assert.False(RenderTemplateEvidenceValidator.Matches(Plan().ToJsonString(), null));
    }

    [Theory]
    [InlineData("\tfabric_detail")]
    [InlineData("\tsleeve")]
    [InlineData(" front ")]
    public void LayerIdentityWhitespaceSurvivesDomainBindingAndStillRequiresExactMatch(string name)
    {
        var inspection = Inspection();
        inspection = inspection with { Layers = [inspection.Layers[0] with { Name = name }] };
        var binding = ExpectedLayerEvidence.Create(257, name, true, false, false, false, 255, "normal",
            LayerStructureSignature.Create("sha256:" + Signature));
        var plan = Plan();
        plan["targets"]![0]!["expected_layer"]!["name_before"] = binding.NameBefore;
        Assert.True(RenderTemplateEvidenceValidator.Matches(plan.ToJsonString(), inspection));
        plan["targets"]![0]!["expected_layer"]!["name_before"] = name.Trim();
        Assert.False(RenderTemplateEvidenceValidator.Matches(plan.ToJsonString(), inspection));
    }

    [Theory]
    [InlineData("hash")]
    [InlineData("length")]
    [InlineData("canvas")]
    [InlineData("signature")]
    [InlineData("path")]
    [InlineData("id")]
    [InlineData("name")]
    [InlineData("hidden_parent")]
    [InlineData("linked")]
    [InlineData("mask")]
    [InlineData("opacity")]
    [InlineData("blend")]
    [InlineData("duplicate_path")]
    [InlineData("duplicate_id")]
    public void StaleOrAmbiguousInspectionCannotAuthorizeReplacement(string mutation)
    {
        var inspection = Inspection();
        var layer = inspection.Layers[0];
        inspection = mutation switch
        {
            "hash" => inspection with { FileSha256 = new string('0', 64) },
            "length" => inspection with { SizeBytes = 1 },
            "canvas" => inspection with { Canvas = new(790, 1253, 8, "RGB") },
            "signature" => inspection with { StructureSignature = new string('0', 64) },
            "path" => inspection with { Layers = [layer with { Path = [2] }] },
            "id" => inspection with { Layers = [layer with { LayerId = 258 }] },
            "name" => inspection with { Layers = [layer with { Name = "another layer" }] },
            "hidden_parent" => inspection with { Layers = [layer with { EffectiveVisible = false }] },
            "linked" => inspection with { Layers = [layer with { SmartObject = layer.SmartObject! with { Kind = "external" } }] },
            "mask" => inspection with { Layers = [layer with { HasMask = true }] },
            "opacity" => inspection with { Layers = [layer with { Opacity = 254 }] },
            "blend" => inspection with { Layers = [layer with { BlendMode = "multiply" }] },
            "duplicate_path" => inspection with { LayerCount = 2, Layers = [layer, layer with { LayerId = 258 }] },
            "duplicate_id" => inspection with { LayerCount = 2, Layers = [layer, layer with { Path = [2] }] },
            _ => throw new ArgumentOutOfRangeException(nameof(mutation))
        };
        Assert.False(RenderTemplateEvidenceValidator.Matches(Plan().ToJsonString(), inspection));
    }

    [Fact]
    public void SupersededLayerMustResolveIndependentlyAndCannotAliasTarget()
    {
        var plan = Plan();
        plan["targets"]![0]!["superseded_layers"] = JsonNode.Parse("""
            [{"layer_path":[2],"layer_path_order":"psd_bottom_to_top","expected_kind":"smartObject",
              "expected_visible":true,"expected_clipping":false,"expected_layer_id":257}]
            """);
        Assert.False(RenderTemplateEvidenceValidator.Matches(plan.ToJsonString(), Inspection()));
        plan["targets"]![0]!["superseded_layers"]![0]!["layer_path"]![0] = 3;
        Assert.False(RenderTemplateEvidenceValidator.Matches(plan.ToJsonString(), Inspection()));
    }
}
