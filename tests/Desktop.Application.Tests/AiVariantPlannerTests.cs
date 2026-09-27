using Desktop.Application.Ai;
using Desktop.Contracts.Ai;

namespace Desktop.Application.Tests;

public sealed class AiVariantPlannerTests
{
    private static readonly AiRequirementInputV1[] Requirements =
    [
        new("front", "商品正面图", "正面", true),
        new("back", "商品背面图", "背面", true)
    ];

    [Fact]
    public void OneCompleteColorBecomesPrimaryAndIncompleteColorIsKeptBlocked()
    {
        var result = AiVariantPlanner.Plan(
            [
                new AiVariantGroupV1("color_a", "color", "示例色 A", ["a_front", "a_back"], false,
                    CoverageRequirementIds: ["front", "back"], IsComplete: true),
                new AiVariantGroupV1("color_b", "color", "示例色 B", ["b_front"], false,
                    CoverageRequirementIds: ["front"], MissingRequirementIds: ["back"], IsComplete: false)
            ],
            Requirements,
            [
                Match("front", "a_front"), Match("back", "a_back"), Match("front", "b_front")
            ]);

        Assert.Equal(AiVariantPlanningStatus.ReviewRequired, result.Status);
        Assert.Equal("color_a", result.PrimaryVariantId);
        Assert.True(result.Variants.Single(item => item.VariantId == "color_a").CanRender);
        Assert.Contains("back", result.Variants.Single(item => item.VariantId == "color_b").MissingRequirementIds);
    }

    [Fact]
    public void MultipleCompleteColorsUseOneDeterministicPrimarySuggestion()
    {
        var result = AiVariantPlanner.Plan(
            [
                new AiVariantGroupV1("color_z", "color", "示例色 Z", ["z_front", "z_back"], false,
                    IsComplete: true),
                new AiVariantGroupV1("color_a", "color", "示例色 A", ["a_front", "a_back"], true,
                    IsComplete: true)
            ],
            Requirements,
            [Match("front", "z_front"), Match("back", "z_back"), Match("front", "a_front"), Match("back", "a_back")]);

        Assert.Equal(AiVariantPlanningStatus.Ready, result.Status);
        Assert.Equal("color_a", result.PrimaryVariantId);
        Assert.All(result.Variants, variant => Assert.True(variant.CanRender));
    }

    [Fact]
    public void AllIncompleteColorsChooseTheLargestCoverageWithoutBorrowingAnotherColor()
    {
        var result = AiVariantPlanner.Plan(
            [
                new AiVariantGroupV1("color_a", "color", "示例色 A", ["a_front"], false),
                new AiVariantGroupV1("color_b", "color", "示例色 B", ["b_back"], false)
            ],
            Requirements,
            [Match("front", "a_front"), Match("back", "b_back")]);

        Assert.Equal(AiVariantPlanningStatus.ReviewRequired, result.Status);
        Assert.Equal("color_a", result.PrimaryVariantId);
        Assert.DoesNotContain("b_back", result.Variants.Single(item => item.VariantId == "color_a").RequirementAssets.Values);
        Assert.DoesNotContain("a_front", result.Variants.Single(item => item.VariantId == "color_b").RequirementAssets.Values);
    }

    [Fact]
    public void ManualSelectionFromAnotherColorProducesAnActionableConflict()
    {
        var result = AiVariantPlanner.Plan(
            [
                new AiVariantGroupV1("color_a", "color", "示例色 A", ["a_front"], false),
                new AiVariantGroupV1("color_b", "color", "示例色 B", ["b_front"], false)
            ],
            [new("front", "商品正面图", "正面", true)],
            [Match("front", "a_front"), Match("front", "b_front")],
            new Dictionary<string, string> { ["front"] = "a_front" });

        Assert.Equal(AiVariantPlanningStatus.Conflict, result.Status);
        Assert.Contains(result.Variants, variant => variant.ConflictCode == AiVariantPlanner.ManualSelectionConflictCode);
    }

    private static AiRequirementMatchV1 Match(string requirementId, string assetId) =>
        new(requirementId, assetId, 1, 0.95m, ["exact"]);
}
