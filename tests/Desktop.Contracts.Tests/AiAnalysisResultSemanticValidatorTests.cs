using System.Globalization;
using Desktop.Contracts.Ai;

namespace Desktop.Contracts.Tests;

public sealed class AiAnalysisResultSemanticValidatorTests
{
    [Fact]
    public void AcceptsDistinctTopNCandidatesAndRectangularSizeTable()
    {
        var result = Result(
            matches:
            [
                new AiRequirementMatchV1("frame_01", "asset_01", 1, 0.98m, ["front"]),
                new AiRequirementMatchV1("frame_01", "asset_02", 2, 0.87m, ["alternate"])
            ],
            geometry:
            [
                new AiSubjectGeometryV1("asset_01", 0.1m, 0.2m, 0.8m, 0.9m, 0.95m),
                new AiSubjectGeometryV1("asset_02", 0m, 0m, 1m, 1m, 0.80m)
            ],
            sizeTable: new AiSizeTableCandidateV1(
                "asset_01",
                0.9m,
                ["尺码", "胸围"],
                [["M", "96"], ["L", "100"]]));

        AiAnalysisResultSemanticValidator.Validate(result);
    }

    [Fact]
    public void RejectsDuplicateRankWithinRequirement()
    {
        var result = Result(matches:
        [
            new AiRequirementMatchV1("frame_01", "asset_01", 1, 0.98m, ["front"]),
            new AiRequirementMatchV1("frame_01", "asset_02", 1, 0.87m, ["alternate"])
        ]);

        var exception = Assert.Throws<InvalidDataException>(
            () => AiAnalysisResultSemanticValidator.Validate(result));

        Assert.Equal(AiAnalysisResultSemanticValidator.RequirementRankDuplicateCode, exception.Message);
    }

    [Fact]
    public void RejectsDuplicateAssetWithinRequirement()
    {
        var result = Result(matches:
        [
            new AiRequirementMatchV1("frame_01", "asset_01", 1, 0.98m, ["front"]),
            new AiRequirementMatchV1("frame_01", "asset_01", 2, 0.87m, ["alternate"])
        ]);

        var exception = Assert.Throws<InvalidDataException>(
            () => AiAnalysisResultSemanticValidator.Validate(result));

        Assert.Equal(AiAnalysisResultSemanticValidator.RequirementAssetDuplicateCode, exception.Message);
    }

    [Theory]
    [InlineData(0.5, 0.5, 0.2, 0.8)]
    [InlineData(0.7, 0.2, 0.2, 0.8)]
    [InlineData(0.2, 0.5, 0.8, 0.5)]
    [InlineData(0.2, 0.8, 0.8, 0.2)]
    [InlineData(-0.1, 0.2, 0.8, 0.9)]
    [InlineData(0.1, 0.2, 1.1, 0.9)]
    public void RejectsZeroReversedOrOutOfRangeGeometry(
        decimal left,
        decimal top,
        decimal right,
        decimal bottom)
    {
        var result = Result(geometry:
        [new AiSubjectGeometryV1("asset_01", left, top, right, bottom, 0.9m)]);

        var exception = Assert.Throws<InvalidDataException>(
            () => AiAnalysisResultSemanticValidator.Validate(result));

        Assert.Equal(AiAnalysisResultSemanticValidator.GeometryInvalidCode, exception.Message);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public void RejectsRaggedSizeTableRows(int rowWidth)
    {
        var result = Result(sizeTable: new AiSizeTableCandidateV1(
            "asset_01",
            0.9m,
            ["尺码", "胸围"],
            [Enumerable.Range(0, rowWidth)
                .Select(index => index.ToString(CultureInfo.InvariantCulture))
                .ToArray()]));

        var exception = Assert.Throws<InvalidDataException>(
            () => AiAnalysisResultSemanticValidator.Validate(result));

        Assert.Equal(AiAnalysisResultSemanticValidator.SizeTableShapeInvalidCode, exception.Message);
    }

    [Fact]
    public void RejectsNullNestedMembersWithStableStructureCode()
    {
        var invalidResults = new (AiAnalysisResultV1 Result, string Code)[]
        {
            (Result() with { RequirementMatches = [null!] }, AiAnalysisResultSemanticValidator.StructureInvalidCode),
            (Result(matches: [new AiRequirementMatchV1("frame_01", "asset_01", 1, 0.9m, null!)]), AiAnalysisResultSemanticValidator.StructureInvalidCode),
            (Result(matches: [new AiRequirementMatchV1("frame_01", "asset_01", 1, 0.9m, [null!])]), AiAnalysisResultSemanticValidator.StructureInvalidCode),
            (Result() with { VariantGroups = [null!] }, AiAnalysisResultSemanticValidator.StructureInvalidCode),
            (Result() with
            {
                VariantGroups = [new AiVariantGroupV1("variant_01", "颜色", "黑色", null!, true)]
            }, AiAnalysisResultSemanticValidator.StructureInvalidCode),
            (Result() with
            {
                VariantGroups = [new AiVariantGroupV1("variant_01", "颜色", "黑色", [null!], true)]
            }, AiAnalysisResultSemanticValidator.StructureInvalidCode),
            (Result(geometry: [null!]), AiAnalysisResultSemanticValidator.GeometryInvalidCode),
            (Result() with { Warnings = [null!] }, AiAnalysisResultSemanticValidator.StructureInvalidCode),
            (Result() with { Usage = null! }, AiAnalysisResultSemanticValidator.StructureInvalidCode)
        };

        foreach (var (invalid, expectedCode) in invalidResults)
        {
            var exception = Assert.Throws<InvalidDataException>(
                () => AiAnalysisResultSemanticValidator.Validate(invalid));
            Assert.Equal(expectedCode, exception.Message);
        }
    }

    [Fact]
    public void RejectsInvalidTypedNestedValuesWithStableStructureCode()
    {
        var invalidResults = new (AiAnalysisResultV1 Result, string Code)[]
        {
            (Result(matches: [new AiRequirementMatchV1("frame_01", "asset_01", 20_001, 0.9m, ["front"])]), AiAnalysisResultSemanticValidator.StructureInvalidCode),
            (Result(matches: [new AiRequirementMatchV1("frame_01", "asset_01", 1, 0.9m, ["front", "front"])]), AiAnalysisResultSemanticValidator.StructureInvalidCode),
            (Result() with
            {
                VariantGroups = [new AiVariantGroupV1("variant_01", "颜色", "黑色", ["asset_01", "asset_01"], true)]
            }, AiAnalysisResultSemanticValidator.StructureInvalidCode),
            (Result() with
            {
                Warnings = [new AiWarningV1("warning_01", "unsupported", "invalid scope")]
            }, AiAnalysisResultSemanticValidator.StructureInvalidCode),
            (Result() with { Usage = new AiUsageSummaryV1("usage_request_01", -1, 1, 1, null, null) }, AiAnalysisResultSemanticValidator.StructureInvalidCode),
            (Result() with { Usage = new AiUsageSummaryV1("usage_request_01", 1, 1, 1, 0.01m, "usd") }, AiAnalysisResultSemanticValidator.StructureInvalidCode)
        };

        foreach (var (invalid, expectedCode) in invalidResults)
        {
            var exception = Assert.Throws<InvalidDataException>(
                () => AiAnalysisResultSemanticValidator.Validate(invalid));
            Assert.Equal(expectedCode, exception.Message);
        }
    }

    [Fact]
    public void RejectsNullSizeTableMembersWithStableSizeTableCode()
    {
        var invalidResults = new (AiAnalysisResultV1 Result, string Code)[]
        {
            (Result(sizeTable: new AiSizeTableCandidateV1("asset_01", 0.9m, null!, [["M"]])), AiAnalysisResultSemanticValidator.SizeTableShapeInvalidCode),
            (Result(sizeTable: new AiSizeTableCandidateV1("asset_01", 0.9m, ["尺码"], null!)), AiAnalysisResultSemanticValidator.SizeTableShapeInvalidCode),
            (Result(sizeTable: new AiSizeTableCandidateV1("asset_01", 0.9m, [null!], [["M"]])), AiAnalysisResultSemanticValidator.SizeTableShapeInvalidCode),
            (Result(sizeTable: new AiSizeTableCandidateV1("asset_01", 0.9m, ["尺码"], [null!])), AiAnalysisResultSemanticValidator.SizeTableShapeInvalidCode),
            (Result(sizeTable: new AiSizeTableCandidateV1("asset_01", 0.9m, ["尺码"], [[null!]])), AiAnalysisResultSemanticValidator.SizeTableShapeInvalidCode)
        };

        foreach (var (invalid, expectedCode) in invalidResults)
        {
            var exception = Assert.Throws<InvalidDataException>(
                () => AiAnalysisResultSemanticValidator.Validate(invalid));
            Assert.Equal(expectedCode, exception.Message);
        }
    }

    [Fact]
    public void ReferenceNormalizerRejectsNestedNullBeforeProjection()
    {
        var request = new AiAnalysisRequestV1(
            AiAnalysisProtocol.RequestSchemaVersion,
            "request",
            "request_01",
            "correlation_01",
            "product_01",
            "batch_01",
            "style_01",
            "snapshot_01",
            "revision_01",
            AiAnalysisKind.ProductJointUnderstanding,
            "model/proposed",
            "prompt/v1",
            AiAnalysisProtocol.ResultSchemaVersion,
            "compression/v1",
            "consent_01",
            "sha256:" + new string('a', 64),
            [new AiAssetInputV1("asset_01", "sha256:" + new string('b', 64), "image/jpeg", 10, 10, "copy_01")],
            [new AiRequirementInputV1("frame_01", "正面", "主图", true)]);
        var invalid = Result() with { VariantGroups = [null!] };

        var exception = Assert.Throws<InvalidDataException>(
            () => AiAnalysisResultReferenceNormalizer.Normalize(request, invalid));

        Assert.Equal(AiAnalysisResultSemanticValidator.StructureInvalidCode, exception.Message);
    }

    [Fact]
    public void ReferenceNormalizerClosesAllTransportAliasesOverProducingRequest()
    {
        var request = new AiAnalysisRequestV1(
            AiAnalysisProtocol.RequestSchemaVersion,
            "request",
            "ai-real-request-01",
            "correlation_01",
            "product_01",
            "batch_01",
            "style_01",
            "snapshot_01",
            "revision_01",
            AiAnalysisKind.ProductJointUnderstanding,
            "model/real-v3",
            "ai-prompt/v3",
            AiAnalysisProtocol.ResultSchemaVersion,
            "compression/v1",
            "consent_01",
            "sha256:" + new string('a', 64),
            [
                new AiAssetInputV1("asset_real_01", "sha256:" + new string('b', 64), "image/jpeg", 10, 10, "copy_01"),
                new AiAssetInputV1("asset_real_02", "sha256:" + new string('c', 64), "image/jpeg", 10, 10, "copy_02")
            ],
            [
                new AiRequirementInputV1("learn_frame_real_01", "正面站立图", "主图", true),
                new AiRequirementInputV1("learn_frame_real_02", "面料特写图", "特写", true)
            ]);
        var result = new AiAnalysisResultV1(
            AiAnalysisProtocol.ResultSchemaVersion,
            "result",
            "request_0",
            null,
            "model_0",
            [
                new AiRequirementMatchV1("req_0", "img_0", 1, 0.95m, ["exact"]),
                new AiRequirementMatchV1("req_1", "img_1", 1, 0.94m, ["exact"])
            ],
            [new AiVariantGroupV1("variant_01", "款式", "默认", ["img_0", "img_1"], true)],
            [new AiSubjectGeometryV1("img_0", 0.1m, 0.1m, 0.9m, 0.9m, 0.95m)],
            new AiSizeTableCandidateV1("img_1", 0.9m, ["尺码"], [["L"]]),
            [],
            new AiUsageSummaryV1("usage_01", 1, 1, 1, null, null));

        var normalized = AiAnalysisResultReferenceNormalizer.Normalize(request, result);

        Assert.Equal(request.RequestId, normalized.RequestId);
        Assert.Equal(request.ModelConfigVersion, normalized.ModelConfigVersion);
        Assert.Equal(
            request.Requirements.Select(requirement => requirement.RequirementId),
            normalized.RequirementMatches.Select(match => match.RequirementId));
        Assert.Equal(
            request.Assets.Select(asset => asset.AssetId),
            normalized.RequirementMatches.Select(match => match.AssetId));
        Assert.Equal("asset_real_01", Assert.Single(normalized.SubjectGeometry).AssetId);
        Assert.Equal("asset_real_02", normalized.SizeTableCandidate!.SourceAssetId);
    }

    [Fact]
    public void ReferenceNormalizerRejectsAliasAndRealIdThatCollapseToDuplicateAsset()
    {
        var request = new AiAnalysisRequestV1(
            AiAnalysisProtocol.RequestSchemaVersion,
            "request",
            "request_01",
            "correlation_01",
            "product_01",
            "batch_01",
            "style_01",
            "snapshot_01",
            "revision_01",
            AiAnalysisKind.ProductJointUnderstanding,
            "model/proposed",
            "ai-prompt/v3",
            AiAnalysisProtocol.ResultSchemaVersion,
            "compression/v1",
            "consent_01",
            "sha256:" + new string('a', 64),
            [new AiAssetInputV1("asset_01", "sha256:" + new string('b', 64), "image/jpeg", 10, 10, "copy_01")],
            [new AiRequirementInputV1("frame_01", "正面站立图", "主图", true)]);
        var result = Result() with
        {
            VariantGroups = [new AiVariantGroupV1(
                "variant_01",
                "款式",
                "默认",
                ["img_0", "asset_01"],
                true)]
        };

        var exception = Assert.Throws<InvalidDataException>(
            () => AiAnalysisResultReferenceNormalizer.Normalize(request, result));

        Assert.Equal(AiAnalysisResultSemanticValidator.StructureInvalidCode, exception.Message);
    }

    [Fact]
    public void RecognizesOnlyStableSemanticCodes()
    {
        Assert.True(AiAnalysisResultSemanticValidator.IsSemanticErrorCode(
            AiAnalysisResultSemanticValidator.GeometryInvalidCode));
        Assert.False(AiAnalysisResultSemanticValidator.IsSemanticErrorCode("AI_RESULT_REFERENCE_MISMATCH"));
    }

    private static AiAnalysisResultV1 Result(
        IReadOnlyList<AiRequirementMatchV1>? matches = null,
        IReadOnlyList<AiSubjectGeometryV1>? geometry = null,
        AiSizeTableCandidateV1? sizeTable = null) => new(
        AiAnalysisProtocol.ResultSchemaVersion,
        "result",
        "request_01",
        null,
        "model/proposed",
        matches ?? [],
        [],
        geometry ?? [],
        sizeTable,
        [],
        new AiUsageSummaryV1("usage_request_01", 1, 1, 1, null, null));
}
