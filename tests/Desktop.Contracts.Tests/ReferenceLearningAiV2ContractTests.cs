using System.Text.Json;
using Desktop.Contracts.Ai;
using Desktop.Contracts.Validation;

namespace Desktop.Contracts.Tests;

public sealed class ReferenceLearningAiV2ContractTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public void AcceptsReusableVisualCategoryPhrase()
    {
        var result = Result("人物正面端坐图");
        var json = JsonSerializer.Serialize(result, JsonOptions);

        Assert.True(new JsonSchemaContractValidator()
            .Validate(ContractDocumentKind.ReferenceLearningAi, json).IsValid);
        Assert.DoesNotContain("front", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RejectsLegacyResultVersionInsteadOfMixingContracts()
    {
        var result = Result("人物正面站立图");
        var json = JsonSerializer.Serialize(result with
        {
            SchemaVersion = AiAnalysisProtocol.ResultSchemaVersion
        }, JsonOptions);

        Assert.False(new JsonSchemaContractValidator()
            .Validate(ContractDocumentKind.ReferenceLearningAi, json).IsValid);
    }

    [Fact]
    public void AcceptsVersionedProviderNeutralRequestWithoutSourcePaths()
    {
        var request = new ReferenceLearningAiRequestV2(
            ReferenceLearningAiProtocol.RequestSchemaVersion,
            "request",
            ReferenceLearningAiOperation.GenerateSelectionRequirements,
            "request_01",
            "learn_01",
            "doubao-seed-2.1-pro-260628",
            ReferenceLearningAiProtocol.SemanticPromptVersion,
            ReferenceLearningAiProtocol.ResultSchemaVersion,
            "wic-bounded/max1600/jpeg-q82/png/v1",
            "consent_01",
            "sha256:" + new string('a', 64),
            [new AiAssetInputV1(
                "asset_01", "sha256:" + new string('b', 64), "image/jpeg", 1000, 1500,
                "copy_01", "sha256:" + new string('c', 64), 1024, "image/jpeg",
                "wic-bounded/max1600/jpeg-q82/png/v1", "lease_01")],
            [new ReferenceLearningFramePromptV2(
                "frame_01", "page_01", "asset_01", "模特展示页",
                "基于像素闭合证据生成要求", "主体完整并保留参考构图")]);
        var json = JsonSerializer.Serialize(request, JsonOptions);

        Assert.True(new JsonSchemaContractValidator()
            .Validate(ContractDocumentKind.ReferenceLearningAi, json).IsValid);
        Assert.DoesNotContain("C:\\", json, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("wic-bounded/max1600/jpeg-q82/png-opaque-jpeg/v2")]
    [InlineData("wic-bounded/max1280/jpeg-q76/png-opaque-jpeg/v3")]
    public void AcceptsVersionedProductionCompressionProfilesAlongsideLegacyV1(string compressionProfile)
    {
        var request = new ReferenceLearningAiRequestV2(
            ReferenceLearningAiProtocol.RequestSchemaVersion,
            "request",
            ReferenceLearningAiOperation.GenerateSelectionRequirements,
            "request_v2",
            "learn_v2",
            "doubao-seed-2.1-pro-260628",
            ReferenceLearningAiProtocol.SemanticPromptVersion,
            ReferenceLearningAiProtocol.ResultSchemaVersion,
            compressionProfile,
            "consent_v2",
            "sha256:" + new string('d', 64),
            [new AiAssetInputV1(
                "asset_v2", "sha256:" + new string('e', 64), "image/png", 1000, 1500,
                "copy_v2", "sha256:" + new string('f', 64), 1024, "image/jpeg",
                compressionProfile, "lease_v2")],
            [new ReferenceLearningFramePromptV2(
                "frame_v2", "page_v2", "asset_v2", "模特展示页",
                "基于像素闭合证据生成要求", "主体完整并保留参考构图")]);

        Assert.True(new JsonSchemaContractValidator()
            .Validate(ContractDocumentKind.ReferenceLearningAi, JsonSerializer.Serialize(request, JsonOptions)).IsValid);
    }

    [Fact]
    public void TargetMatchingOperationIsNotPartOfReferenceSemanticV2()
    {
        var json = JsonSerializer.Serialize(Result("人物正面站立图"), JsonOptions)
            .Replace(
                ReferenceLearningAiOperation.GenerateSelectionRequirements,
                "match_target_group",
                StringComparison.Ordinal);

        Assert.False(new JsonSchemaContractValidator()
            .Validate(ContractDocumentKind.ReferenceLearningAi, json).IsValid);
    }

    [Theory]
    [InlineData("竖版构图，中近景景别，人物坐在木质长凳上，双手捧着书卷")]
    [InlineData("人物正面端坐图。")]
    public void RejectsReferencePhotoNarrative(string narrative)
    {
        var json = JsonSerializer.Serialize(Result(narrative), JsonOptions);

        Assert.False(new JsonSchemaContractValidator()
            .Validate(ContractDocumentKind.ReferenceLearningAi, json).IsValid);
    }

    private static ReferenceLearningAiResultV2 Result(string requirement) => new(
        ReferenceLearningAiProtocol.ResultSchemaVersion,
        "result",
        ReferenceLearningAiOperation.GenerateSelectionRequirements,
        "request_01",
        "doubao-seed-2.1-pro-260628",
        [new ReferenceLearningFrameResultV2(
            "frame_01",
            requirement,
            ["参考组中仅该图呈坐姿且为半身近景"],
            ["排除站姿、背身和远景"],
            0.97m)],
        [],
        new AiUsageSummaryV1("usage_01", 100, 20, 1, null, null));
}
