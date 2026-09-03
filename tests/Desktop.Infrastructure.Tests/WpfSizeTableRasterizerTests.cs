using Desktop.Application.SizeTables;
using Desktop.Domain.SizeTables;
using Desktop.Infrastructure.Files;
using Desktop.Infrastructure.SizeTables;

namespace Desktop.Infrastructure.Tests;

public sealed class WpfSizeTableRasterizerTests
{
    [Theory]
    [InlineData("/")]
    [InlineData("\\")]
    [InlineData("-")]
    public async Task LegalSymbolsProduceDeterministicLosslessResource(string symbol)
    {
        using var directory = new TestDirectory();
        var rasterizer = new WpfSizeTableRasterizer(
            new AtomicFileStore(new LocalAppPathPolicy(directory.Path)));
        var request = new SizeTableRasterizationRequest(Table(symbol, rowCount: 6), Style());

        var first = await rasterizer.RasterizeAsync(request, CancellationToken.None);
        var second = await rasterizer.RasterizeAsync(request, CancellationToken.None);
        var content = await File.ReadAllBytesAsync(Path.Combine(
            directory.Path,
            first.OwnedRelativePath.Replace('/', Path.DirectorySeparatorChar)));

        Assert.Equal("deterministic_size_table", first.Role);
        Assert.Equal("image/png", first.MediaType);
        Assert.Equal(1000, first.PixelWidth);
        Assert.True(first.PixelHeight > 0);
        Assert.Equal(96, first.Dpi);
        Assert.Equal("wpf-size-table-rasterizer/v1", first.RasterizerVersion);
        Assert.StartsWith("sha256:", first.FontSignature, StringComparison.Ordinal);
        Assert.Equal(first.ResourceId, second.ResourceId);
        Assert.Equal(first.Sha256, second.Sha256);
        Assert.Equal(first.ByteLength, second.ByteLength);
        Assert.Equal(first.ByteLength, content.LongLength);
        Assert.StartsWith("size_raster_", first.ResourceId, StringComparison.Ordinal);
        Assert.DoesNotContain(directory.Path, first.OwnedRelativePath, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RowCountChangesHeightAndResourceIdentity()
    {
        using var directory = new TestDirectory();
        var rasterizer = new WpfSizeTableRasterizer(
            new AtomicFileStore(new LocalAppPathPolicy(directory.Path)));

        var shortTable = await rasterizer.RasterizeAsync(
            new SizeTableRasterizationRequest(Table("/", 4), Style()),
            CancellationToken.None);
        var tallTable = await rasterizer.RasterizeAsync(
            new SizeTableRasterizationRequest(Table("/", 8), Style()),
            CancellationToken.None);

        Assert.True(tallTable.PixelHeight > shortTable.PixelHeight);
        Assert.NotEqual(shortTable.ResourceId, tallTable.ResourceId);
        Assert.NotEqual(shortTable.Sha256, tallTable.Sha256);
    }

    [Fact]
    public async Task MissingCellCannotBeRasterized()
    {
        using var directory = new TestDirectory();
        var source = SizeTableSource.Create("asset_size", new string('a', 64));
        var candidate = SizeTableCandidate.Create(
            "candidate_01", source, 0.99m, ["尺码", "袖长"], [["L", ""]]);
        var table = SizeTable.FromCandidate(
            "size_01", "batch_01", "style_01", "page_01", candidate, Now);
        var rasterizer = new WpfSizeTableRasterizer(
            new AtomicFileStore(new LocalAppPathPolicy(directory.Path)));

        var exception = await Assert.ThrowsAsync<SizeTableRuleViolationException>(() =>
            rasterizer.RasterizeAsync(
                new SizeTableRasterizationRequest(table, Style()),
                CancellationToken.None));

        Assert.Equal("SIZE_TABLE_RASTER_REQUIRES_VALID_TABLE", exception.Code);
        Assert.Empty(Directory.EnumerateFiles(directory.Path, "*.png", SearchOption.AllDirectories));
    }

    private static readonly DateTimeOffset Now = new(2026, 9, 1, 8, 0, 0, TimeSpan.Zero);

    private static SizeTableRasterStyle Style() => SizeTableRasterStyle.Create(
        "gold_v1",
        "dynamic_grid_v1",
        "Microsoft YaHei UI",
        24,
        1000,
        56,
        24,
        12,
        "#FFFFFFFF",
        "#FFF2F4F7",
        "#FF101828",
        "#FF98A2B3");

    private static SizeTable Table(string symbol, int rowCount)
    {
        var source = SizeTableSource.Create("asset_size", new string('a', 64));
        var rows = Enumerable.Range(1, rowCount)
            .Select(index => (IReadOnlyList<string>)[index == 1 ? "L" : $"{index}XL", symbol])
            .ToArray();
        var candidate = SizeTableCandidate.Create(
            "candidate_01", source, 0.99m, ["尺码", "袖长"], rows);
        return SizeTable.FromCandidate(
            "size_01", "batch_01", "style_01", "page_01", candidate, Now);
    }
}
