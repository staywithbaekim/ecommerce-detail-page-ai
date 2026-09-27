using System.IO.Compression;
using System.Security.Cryptography;
using Desktop.Application.Psd;
using Desktop.Application.Templates;
using Desktop.Contracts.Validation;
using Desktop.Infrastructure.Templates;

namespace Desktop.Infrastructure.Tests;

public sealed class TemplatePackageSupersededLayerTests
{
    private const int TargetAId = 1001;
    private const int OldA1Id = 1002;
    private const int OldA2Id = 1003;
    private const int TargetBId = 1004;
    private const int OldBId = 1005;
    private const string StructureSignature =
        "sha256:abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789";

    private static readonly byte[] SourceBytes = [0x38, 0x42, 0x50, 0x53, 1, 2, 3, 4];
    private static readonly string SourceHash = Convert.ToHexStringLower(SHA256.HashData(SourceBytes));

    [Fact]
    public async Task V2PreservesMultipleSupersededLayersIncludingNullableIdentity()
    {
        var binding = BindingA(
        [
            Superseded([2], OldA1Id, clipping: false),
            Superseded([3], expectedLayerId: null, clipping: true)
        ]);
        using var directory = new TestDirectory();
        var packagePath = await ExportAsync(directory, Manifest(bindings: [binding]));

        var validated = await Port().ValidateAsync(packagePath, CancellationToken.None);

        var superseded = Assert.Single(Assert.Single(validated.Manifest!.Pages!).Bindings!).SupersededLayers;
        Assert.Collection(
            Assert.IsAssignableFrom<IReadOnlyList<TemplatePackageSupersededLayer>>(superseded),
            first =>
            {
                Assert.Equal([2], first.LayerPath);
                Assert.Equal("smartObject", first.ExpectedKind);
                Assert.True(first.ExpectedVisible);
                Assert.False(first.ExpectedClipping);
                Assert.Equal(OldA1Id, first.ExpectedLayerId);
            },
            second =>
            {
                Assert.Equal([3], second.LayerPath);
                Assert.True(second.ExpectedClipping);
                Assert.Null(second.ExpectedLayerId);
            });
    }

    [Fact]
    public async Task V2WithoutSupersededFieldRemainsReadableAndDoesNotEmitNullProperty()
    {
        using var directory = new TestDirectory();
        var packagePath = await ExportAsync(directory, Manifest(bindings: [BindingA()]));

        using (var archive = ZipFile.OpenRead(packagePath))
        using (var reader = new StreamReader(archive.GetEntry("manifest.json")!.Open()))
        {
            var json = await reader.ReadToEndAsync();
            Assert.DoesNotContain("\"supersededLayers\"", json, StringComparison.Ordinal);
        }

        var validated = await Port().ValidateAsync(packagePath, CancellationToken.None);
        Assert.Null(Assert.Single(Assert.Single(validated.Manifest!.Pages!).Bindings!).SupersededLayers);
    }

    [Fact]
    public async Task V1RejectsSupersededField()
    {
        var v1Inspection = Inspection(Inspection().Layers.Select(layer => layer with
        {
            Path = layer.Path.Select(segment => segment + 1).ToArray(),
            ClippingBase = layer.ClippingBase is null
                ? null
                : layer.ClippingBase with
                {
                    Path = layer.ClippingBase.Path.Select(segment => segment + 1).ToArray()
                }
        }).ToArray());
        var manifest = Manifest(
            TemplatePackageProtocol.Version,
            v1Inspection,
            [BindingA([Superseded([2], OldA1Id, clipping: false)])]);

        await AssertRejectedAsync(manifest);
    }

    [Theory]
    [InlineData("wrong_path")]
    [InlineData("wrong_id")]
    [InlineData("wrong_expected_kind")]
    [InlineData("wrong_layer_kind")]
    [InlineData("group")]
    [InlineData("not_embedded")]
    [InlineData("hidden")]
    [InlineData("ineffective")]
    [InlineData("clipping")]
    public async Task V2RejectsSupersededEvidenceThatDoesNotMatchInspection(string mutation)
    {
        var inspection = Inspection();
        var superseded = Superseded([2], OldA1Id, clipping: false);
        switch (mutation)
        {
            case "wrong_path":
                superseded = superseded with { LayerPath = [99] };
                break;
            case "wrong_id":
                superseded = superseded with { ExpectedLayerId = 9999 };
                break;
            case "wrong_expected_kind":
                superseded = superseded with { ExpectedKind = "pixel" };
                break;
            case "wrong_layer_kind":
                inspection = MutateLayer(inspection, OldA1Id, layer => layer with { Kind = "pixel" });
                break;
            case "group":
                inspection = MutateLayer(inspection, OldA1Id, layer => layer with { IsGroup = true });
                break;
            case "not_embedded":
                inspection = MutateLayer(inspection, OldA1Id, layer => layer with
                {
                    SmartObject = layer.SmartObject! with { Kind = "external" }
                });
                break;
            case "hidden":
                inspection = MutateLayer(inspection, OldA1Id, layer => layer with
                {
                    Visible = false,
                    EffectiveVisible = false
                });
                break;
            case "ineffective":
                inspection = MutateLayer(inspection, OldA1Id, layer => layer with
                {
                    EffectiveVisible = false
                });
                break;
            case "clipping":
                superseded = superseded with { ExpectedClipping = true };
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(mutation));
        }

        await AssertRejectedAsync(Manifest(inspection: inspection, bindings: [BindingA([superseded])]));
    }

    [Theory]
    [InlineData("duplicate_superseded")]
    [InlineData("aliases_own_target")]
    [InlineData("duplicate_target")]
    [InlineData("another_frame_target")]
    [InlineData("another_frame_superseded")]
    public async Task V2RejectsLayerIdentityClaimedMoreThanOncePerPage(string conflict)
    {
        IReadOnlyList<TemplatePackageFrameBinding> bindings = conflict switch
        {
            "duplicate_superseded" =>
            [
                BindingA(
                [
                    Superseded([2], OldA1Id, clipping: false),
                    Superseded([2], expectedLayerId: null, clipping: false)
                ])
            ],
            "aliases_own_target" =>
            [
                BindingA([Superseded([1], TargetAId, clipping: false)])
            ],
            "duplicate_target" =>
            [
                BindingA(),
                Binding("frame_b", [1], TargetAId, "target_a")
            ],
            "another_frame_target" =>
            [
                BindingA([Superseded([2], OldA1Id, clipping: false)]),
                Binding("frame_b", [2], OldA1Id, "old_a_1")
            ],
            "another_frame_superseded" =>
            [
                BindingA([Superseded([2], OldA1Id, clipping: false)]),
                BindingB([Superseded([2], OldA1Id, clipping: false)])
            ],
            _ => throw new ArgumentOutOfRangeException(nameof(conflict))
        };

        await AssertRejectedAsync(Manifest(bindings: bindings));
    }

    [Theory]
    [InlineData("duplicate_id")]
    [InlineData("duplicate_path")]
    public async Task V2RejectsInspectionWithAmbiguousLayerIdentity(string duplicate)
    {
        var inspection = Inspection();
        inspection = duplicate switch
        {
            "duplicate_id" => MutateLayer(
                inspection,
                OldA1Id,
                layer => layer with { LayerId = TargetAId }),
            "duplicate_path" => MutateLayer(
                inspection,
                OldA1Id,
                layer => layer with { Path = [0] }),
            _ => throw new ArgumentOutOfRangeException(nameof(duplicate))
        };

        await AssertRejectedAsync(Manifest(inspection: inspection, bindings: [BindingA()]));
    }

    [Fact]
    public async Task V2AllowsDistinctTargetsWhoseCanvasBoundsOverlap()
    {
        var inspection = Inspection();
        var targetBounds = inspection.Layers.Single(layer => layer.LayerId == TargetAId).Bounds;
        inspection = MutateLayer(
            inspection,
            TargetBId,
            layer => layer with { Bounds = targetBounds });
        using var directory = new TestDirectory();
        var packagePath = await ExportAsync(
            directory,
            Manifest(inspection: inspection, bindings: [BindingA(), BindingB()]));

        var validated = await Port().ValidateAsync(packagePath, CancellationToken.None);

        Assert.Equal(2, Assert.Single(validated.Manifest!.Pages!).Bindings!.Count);
    }

    private static ZipTemplatePackagePort Port() => new(new JsonSchemaContractValidator());

    private static async Task<string> ExportAsync(
        TestDirectory directory,
        TemplatePackageManifest manifest)
    {
        var sourcePath = Path.Combine(directory.Path, "source.psd");
        var packagePath = Path.Combine(directory.Path, $"{Guid.NewGuid():N}.tplpkg");
        await File.WriteAllBytesAsync(sourcePath, SourceBytes);
        await Port().ExportAsync(
            manifest,
            new Dictionary<string, string> { ["resource"] = sourcePath },
            packagePath,
            CancellationToken.None);
        return packagePath;
    }

    private static async Task AssertRejectedAsync(TemplatePackageManifest manifest)
    {
        using var directory = new TestDirectory();
        var sourcePath = Path.Combine(directory.Path, "source.psd");
        var packagePath = Path.Combine(directory.Path, "rejected.tplpkg");
        await File.WriteAllBytesAsync(sourcePath, SourceBytes);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() => Port().ExportAsync(
            manifest,
            new Dictionary<string, string> { ["resource"] = sourcePath },
            packagePath,
            CancellationToken.None));

        Assert.Equal("TPL_PACKAGE_INVALID", exception.Message);
        Assert.False(File.Exists(packagePath));
    }

    private static TemplatePackageManifest Manifest(
        string protocol = TemplatePackageProtocol.CurrentVersion,
        PsdInspectionDocument? inspection = null,
        IReadOnlyList<TemplatePackageFrameBinding>? bindings = null) => new(
        protocol,
        "superseded-layer-tests",
        1,
        "revision_superseded",
        "sha256:" + new string('c', 64),
        [new TemplatePackageEntry("resource", "resources/source.psd", SourceBytes.Length, SourceHash)],
        [new TemplatePackagePage(
            "page",
            0,
            "page",
            TemplatePageContentTypeOption.NormalComposition,
            TemplateGenerationModeOption.OncePerProduct,
            null,
            true,
            "resource",
            inspection ?? Inspection(),
            bindings ?? [BindingA()])]);

    private static TemplatePackageFrameBinding BindingA(
        IReadOnlyList<TemplatePackageSupersededLayer>? superseded = null) =>
        Binding("frame_a", [1], TargetAId, "target_a", superseded);

    private static TemplatePackageFrameBinding BindingB(
        IReadOnlyList<TemplatePackageSupersededLayer>? superseded = null) =>
        Binding("frame_b", [4], TargetBId, "target_b", superseded);

    private static TemplatePackageFrameBinding Binding(
        string frameId,
        IReadOnlyList<int> layerPath,
        int layerId,
        string layerName,
        IReadOnlyList<TemplatePackageSupersededLayer>? superseded = null) => new(
        frameId,
        "page",
        layerPath,
        layerId,
        layerName,
        true,
        false,
        false,
        false,
        255,
        "normal",
        StructureSignature,
        "Choose the matching product image.",
        [new TemplatePackageSelectionCriterion("asset_id", frameId + "_asset")],
        "subject_complete",
        1,
        0,
        0,
        0,
        0,
        1000,
        1000,
        superseded);

    private static TemplatePackageSupersededLayer Superseded(
        IReadOnlyList<int> layerPath,
        int? expectedLayerId,
        bool clipping) => new(
        layerPath,
        "smartObject",
        true,
        clipping,
        expectedLayerId);

    private static PsdInspectionDocument MutateLayer(
        PsdInspectionDocument inspection,
        int layerId,
        Func<PsdLayerInspection, PsdLayerInspection> mutation) => Inspection(
        inspection.Layers.Select(layer => layer.LayerId == layerId ? mutation(layer) : layer).ToArray());

    private static PsdInspectionDocument Inspection(IReadOnlyList<PsdLayerInspection>? layers = null)
    {
        var actualLayers = layers?.ToArray() ??
        [
            Layer([0], TargetAId, "target_a", new(0, 0, 400, 400)),
            Layer([1], OldA1Id, "old_a_1", new(10, 10, 390, 390)),
            Layer([2], OldA2Id, "old_a_2", new(20, 20, 380, 380), clipping: true),
            Layer([3], TargetBId, "target_b", new(500, 0, 900, 400)),
            Layer([4], OldBId, "old_b", new(510, 10, 890, 390))
        ];
        return new PsdInspectionDocument(
            SourceHash,
            SourceBytes.Length,
            new PsdCanvasInspection(1000, 1000, 8, "RGB"),
            actualLayers.Length,
            actualLayers.Count(layer => layer.SmartObject is not null),
            actualLayers.Count(layer => layer.HasMask),
            actualLayers.Count(layer => layer.HasVectorMask),
            actualLayers.Count(layer => layer.Clipping),
            StructureSignature,
            new PsdInspectionTiming(1, 1, 2),
            1024,
            null,
            actualLayers);
    }

    private static PsdLayerInspection Layer(
        IReadOnlyList<int> path,
        int layerId,
        string name,
        PsdLayerBounds bounds,
        bool clipping = false) => new(
        path,
        layerId,
        name,
        "smartObject",
        false,
        true,
        true,
        clipping,
        255,
        255,
        "normal",
        bounds,
        false,
        false,
        null,
        new PsdSmartObjectInspection(
            $"embedded-{layerId}",
            name + ".jpg",
            "data",
            "jpg",
            "jpg",
            128,
            false,
            [bounds.Left, bounds.Top, bounds.Right, bounds.Top,
                bounds.Right, bounds.Bottom, bounds.Left, bounds.Bottom])
        {
            EmbeddedContentSha256 = new string('d', 64),
            EmbeddedContentAvailable = true
        });
}
