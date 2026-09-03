using Desktop.Application.Inputs;
using Desktop.Application.SizeTables;
using Desktop.Contracts.Ai;
using Desktop.Domain.SizeTables;

namespace Desktop.Application.Tests;

public sealed class SizeTableServiceTests
{
    [Fact]
    public void SameContentReferencesAreDeduplicatedByHash()
    {
        var manifest = Manifest(
            [Style("style_a", ["asset_a", "asset_duplicate"])],
            [Asset("asset_a", "style_a", 'a'), Asset("asset_duplicate", "style_a", 'a')]);
        var result = SizeTableService.Prepare(Command(
            manifest,
            "style_a",
            [Candidate("asset_a"), Candidate("asset_duplicate")]));

        Assert.Equal(SizeTableSourceResolutionStatus.Selected, result.SourceResolution.Status);
        var option = Assert.Single(result.SourceResolution.Options);
        Assert.Equal(["asset_a", "asset_duplicate"], option.DuplicateAssetIds);
        Assert.Equal(SizeTableStatus.Valid, result.Table!.Status);
    }

    [Fact]
    public void DistinctContentForOneSyntheticStyleRequiresExplicitSelection()
    {
        var manifest = Manifest(
            [Style("style_a", ["asset_a", "asset_b"])],
            [Asset("asset_a", "style_a", 'a'), Asset("asset_b", "style_a", 'b')]);
        var candidates = new[] { Candidate("asset_a"), Candidate("asset_b") };

        var conflict = SizeTableService.Prepare(Command(manifest, "style_a", candidates));
        var selected = SizeTableService.Prepare(Command(
            manifest,
            "style_a",
            candidates,
            selectedSourceAssetId: "asset_b"));

        Assert.Equal(SizeTableSourceResolutionStatus.Conflict, conflict.SourceResolution.Status);
        Assert.Null(conflict.Table);
        Assert.Equal("SIZE_TABLE_SOURCE_CONFLICT", conflict.ReasonCode);
        Assert.Equal(SizeTableSourceResolutionStatus.Selected, selected.SourceResolution.Status);
        Assert.Equal("asset_b", selected.Table!.Source.AssetId);
        Assert.Equal(1, selected.SourceResolution.SelectionRevision);
    }

    [Fact]
    public void DifferentProductAssignmentsDoNotConflict()
    {
        var manifest = Manifest(
            [Style("style_a", ["asset_a"]), Style("style_b", ["asset_b"])],
            [Asset("asset_a", "style_a", 'a'), Asset("asset_b", "style_b", 'b')]);

        var first = SizeTableService.Prepare(Command(manifest, "style_a", [Candidate("asset_a")]));
        var second = SizeTableService.Prepare(Command(manifest, "style_b", [Candidate("asset_b")]));

        Assert.Equal(SizeTableSourceResolutionStatus.Selected, first.SourceResolution.Status);
        Assert.Equal(SizeTableSourceResolutionStatus.Selected, second.SourceResolution.Status);
        Assert.Equal("asset_a", first.Table!.Source.AssetId);
        Assert.Equal("asset_b", second.Table!.Source.AssetId);
    }

    [Fact]
    public void MissingCandidateFailsClosedWithoutInventingTable()
    {
        var manifest = Manifest(
            [Style("style_a", ["asset_a"])],
            [Asset("asset_a", "style_a", 'a')]);

        var result = SizeTableService.Prepare(Command(manifest, "style_a", []));

        Assert.Equal(SizeTableSourceResolutionStatus.Missing, result.SourceResolution.Status);
        Assert.Null(result.Candidate);
        Assert.Null(result.Table);
        Assert.Equal("SIZE_TABLE_SOURCE_MISSING", result.ReasonCode);
    }

    [Fact]
    public void CandidateCannotReferenceAssetOwnedByAnotherStyle()
    {
        var manifest = Manifest(
            [Style("style_a", ["asset_a"]), Style("style_b", ["asset_b"])],
            [Asset("asset_a", "style_a", 'a'), Asset("asset_b", "style_b", 'b')]);

        var exception = Assert.Throws<SizeTableRuleViolationException>(() =>
            SizeTableService.Prepare(Command(manifest, "style_a", [Candidate("asset_b")])));

        Assert.Equal("SIZE_TABLE_SOURCE_OWNERSHIP_MISMATCH", exception.Code);
    }

    [Fact]
    public void SelectionRevisionUsesCas()
    {
        var manifest = Manifest(
            [Style("style_a", ["asset_a", "asset_b"])],
            [Asset("asset_a", "style_a", 'a'), Asset("asset_b", "style_a", 'b')]);
        var resolution = SizeTableService.ResolveSources(
            manifest,
            "style_a",
            [Candidate("asset_a"), Candidate("asset_b")]);

        var exception = Assert.Throws<SizeTableRuleViolationException>(() =>
            resolution.Select("asset_a", expectedRevision: 1));

        Assert.Equal("SIZE_TABLE_SOURCE_SELECTION_REVISION_CONFLICT", exception.Code);
    }

    private static readonly DateTimeOffset Now = new(2026, 9, 1, 8, 0, 0, TimeSpan.Zero);

    private static PrepareSizeTableCommand Command(
        AssetManifestV1 manifest,
        string styleId,
        IReadOnlyList<AiSizeTableCandidateV1> candidates,
        string? selectedSourceAssetId = null) => new(
            manifest,
            styleId,
            "page_size",
            candidates,
            selectedSourceAssetId,
            0,
            Now);

    private static AiSizeTableCandidateV1 Candidate(string assetId) => new(
        assetId,
        0.99m,
        ["尺码", "袖长"],
        [["L", "/"], ["XL", "/"]]);

    private static AssetManifestV1 Manifest(
        IReadOnlyList<AssetManifestStyle> styles,
        IReadOnlyList<AssetManifestAsset> assets) => new(
            AssetManifestProtocol.Version,
            "manifest_01",
            "batch_01",
            "style_directory_batch",
            new AssetManifestTemplateSnapshot(
                "snapshot_01",
                "revision_01",
                "sha256:" + new string('c', 64),
                ["page_size"]),
            styles,
            assets,
            "sha256:" + new string('d', 64),
            Now);

    private static AssetManifestStyle Style(string styleId, IReadOnlyList<string> assetIds) =>
        new(styleId, styleId, styleId, 0, assetIds);

    private static AssetManifestAsset Asset(string assetId, string styleId, char hashCharacter) => new(
        assetId,
        styleId,
        $"{styleId}/{assetId}.jpg",
        $"{assetId}.jpg",
        100,
        new string(hashCharacter, 64),
        750,
        800,
        "jpeg",
        "image/jpeg",
        "readable");
}
