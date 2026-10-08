using System.Security.Cryptography;
using Desktop.Application.Inputs;
using Desktop.Application.SizeTables;
using Desktop.Contracts.Ai;
using Desktop.Domain.SizeTables;

namespace Desktop.Application.Tests;

public sealed class SizeTableGoldSampleTests
{
    private static readonly DateTimeOffset CreatedAt =
        new(2026, 9, 2, 9, 0, 0, TimeSpan.FromHours(8));

    [RealSizeGoldSampleFact("size-real-a-577fa4d19f919a7a.jpg")]
    public void RealGoldSampleAMatchesEveryCellAndPreservesSlashColumn()
    {
        AssertGoldSample(
            "size-real-a-577fa4d19f919a7a.jpg",
            "577fa4d19f919a7a7bbd64f07daa1102a34642c470e0967364f44543271a9e2b",
            "style_real_a",
            ["尺码", "衣长", "胸围", "肩宽", "袖长"],
            [
                ["L", "61", "102", "40", "/"],
                ["XL", "62", "107", "41", "/"],
                ["2XL", "63", "112", "42", "/"],
                ["3XL", "64", "117", "43", "/"],
                ["4XL", "65", "122", "44", "/"],
                ["5XL", "66", "127", "45", "/"]
            ]);
    }

    [RealSizeGoldSampleFact("size-real-b-9231ad299120bba6.jpg")]
    public void RealGoldSampleBMatchesEveryCellAndPreservesDecimalText()
    {
        AssertGoldSample(
            "size-real-b-9231ad299120bba6.jpg",
            "9231ad299120bba65af9391ba8a59097b7ff169197810a38971621df886d9fb0",
            "style_real_b",
            ["尺码", "胸围", "肩宽", "袖长", "衣长", "袖口"],
            [
                ["L", "99", "39.5", "51.5", "64", "28"],
                ["XL", "103", "40.5", "52.5", "65", "29"],
                ["2XL", "107", "41.5", "53.5", "66", "30"],
                ["3XL", "111", "42.5", "54.5", "67", "31"],
                ["4XL", "115", "43.5", "55.5", "68", "32"],
                ["5XL", "119", "44.5", "56", "68.5", "33"]
            ]);
    }

    private static void AssertGoldSample(
        string fileName,
        string expectedSha256,
        string styleId,
        IReadOnlyList<string> columns,
        IReadOnlyList<IReadOnlyList<string>> rows)
    {
        var path = Path.Combine(
            FindRepositoryRoot(),
            "需求样本",
            "SIZE-01金标",
            "真实来源",
            fileName);
        Assert.True(File.Exists(path), $"已发现的真实金标在执行前消失：{fileName}");

        var bytes = File.ReadAllBytes(path);
        var actualSha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        Assert.Equal(expectedSha256, actualSha256);

        var assetId = $"asset_{styleId}";
        var manifest = new AssetManifestV1(
            AssetManifestProtocol.Version,
            $"manifest_{styleId}",
            $"batch_{styleId}",
            "style_directory_batch",
            new AssetManifestTemplateSnapshot(
                "snapshot_size_gold",
                "revision_size_gold",
                "sha256:" + new string('c', 64),
                ["page_size"]),
            [new AssetManifestStyle(styleId, styleId, styleId, 0, [assetId])],
            [new AssetManifestAsset(
                assetId,
                styleId,
                $"{styleId}/{fileName}",
                fileName,
                bytes.Length,
                expectedSha256,
                750,
                800,
                "jpeg",
                "image/jpeg",
                "readable")],
            "sha256:" + new string('d', 64),
            CreatedAt);
        var candidate = new AiSizeTableCandidateV1(assetId, 0.99m, columns, rows);

        var result = SizeTableService.Prepare(new PrepareSizeTableCommand(
            manifest,
            styleId,
            "page_size",
            [candidate],
            null,
            0,
            CreatedAt));

        Assert.Equal(SizeTableSourceResolutionStatus.Selected, result.SourceResolution.Status);
        Assert.NotNull(result.Table);
        Assert.Equal(SizeTableStatus.Valid, result.Table.Status);
        Assert.Equal(columns, result.Table.Columns.Select(column => column.SourceText).ToArray());
        Assert.Equal(
            rows.Select(row => row.ToArray()),
            result.Table.Rows.Select(row => row.Cells.Select(cell => cell.SourceText).ToArray()));
        Assert.Equal(expectedSha256, result.Table.Source.ContentDigest[7..]);
    }

    private sealed class RealSizeGoldSampleFactAttribute : FactAttribute
    {
        public RealSizeGoldSampleFactAttribute(string fileName)
        {
            if (!File.Exists(Path.Combine(FindRepositoryRoot(), "需求样本", "SIZE-01金标", "真实来源", fileName)))
                Skip = $"外部验收素材缺失（不代表通过）：{fileName}";
        }
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "项目总览.md")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("SIZE01_REPOSITORY_ROOT_NOT_FOUND");
    }
}
