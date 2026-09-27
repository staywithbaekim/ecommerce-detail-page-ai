using Desktop.Application.SizeTables;
using Desktop.Application.Files;
using Desktop.Domain.SizeTables;
using Desktop.Infrastructure.Files;
using Desktop.Infrastructure.SizeTables;

namespace Desktop.Infrastructure.Tests;

public sealed class WpfSizeTableRasterizerTests
{
    [Fact]
    public async Task MatchingHistoricalBytesAreReusedAcrossRasterizerInstancesWithoutRewriting()
    {
        using var directory = new TestDirectory();
        var files = new RecordingFiles(new AtomicFileStore(new LocalAppPathPolicy(directory.Path)));
        var request = new SizeTableRasterizationRequest(Table("/", 4), Style());
        var first = await new WpfSizeTableRasterizer(files).RasterizeAsync(request, default);
        var original = await files.ReadAsync(first.OwnedRelativePath, default);

        var retries = await Task.WhenAll(new WpfSizeTableRasterizer(files).RasterizeAsync(request, default),
            new WpfSizeTableRasterizer(files).RasterizeAsync(request, default));

        Assert.Equal(1, files.Writes);
        Assert.All(retries, retry => Assert.Equal(first, retry));
        Assert.Equal(original, await files.ReadAsync(first.OwnedRelativePath, default));
    }

    [Fact]
    public async Task SameIdentityWithDifferentExistingBytesIsRejectedAndOldBytesStayIntact()
    {
        using var directory = new TestDirectory();
        var files = new RecordingFiles(new AtomicFileStore(new LocalAppPathPolicy(directory.Path)));
        var request = new SizeTableRasterizationRequest(Table("/", 4), Style());
        var rasterizer = new WpfSizeTableRasterizer(files);
        var first = await rasterizer.RasterizeAsync(request, default);
        var original = (await files.ReadAsync(first.OwnedRelativePath, default))!;
        var different = original.ToArray();
        different[^1] ^= 1;
        await files.WriteAtomicallyAsync(first.OwnedRelativePath, different, default);
        var writes = files.Writes;

        var error = await Assert.ThrowsAsync<SizeTableRuleViolationException>(() => rasterizer.RasterizeAsync(request, default));

        Assert.Equal("SIZE_TABLE_RASTER_IDENTITY_CONFLICT", error.Code);
        Assert.Equal(writes, files.Writes);
        Assert.Equal(different, await files.ReadAsync(first.OwnedRelativePath, default));
    }

    [Fact]
    public async Task CorruptReadbackRejectsCandidateAndRetryDoesNotOverwriteFailedWrite()
    {
        using var directory = new TestDirectory();
        var files = new RecordingFiles(new AtomicFileStore(new LocalAppPathPolicy(directory.Path))) { CorruptWrite = true };
        var request = new SizeTableRasterizationRequest(Table("/", 4), Style());
        var rasterizer = new WpfSizeTableRasterizer(files);

        var first = await Assert.ThrowsAsync<SizeTableRuleViolationException>(() => rasterizer.RasterizeAsync(request, default));
        var preserved = await files.ReadAsync(files.LastPath!, default);
        files.CorruptWrite = false;
        var retry = await Assert.ThrowsAsync<SizeTableRuleViolationException>(() => rasterizer.RasterizeAsync(request, default));

        Assert.Equal("SIZE_TABLE_RASTER_READBACK_MISMATCH", first.Code);
        Assert.Equal("SIZE_TABLE_RASTER_IDENTITY_CONFLICT", retry.Code);
        Assert.Equal(1, files.Writes);
        Assert.Equal(preserved, await files.ReadAsync(files.LastPath!, default));
    }

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

    private sealed class RecordingFiles(IFileStore inner) : IFileStore
    {
        public int Writes { get; private set; }
        public bool CorruptWrite { get; set; }
        public string? LastPath { get; private set; }
        public Task WriteAtomicallyAsync(string relativePath, ReadOnlyMemory<byte> content, CancellationToken cancellationToken)
        {
            Writes++;
            LastPath = relativePath;
            var bytes = content.ToArray();
            if (CorruptWrite) bytes[^1] ^= 1;
            return inner.WriteAtomicallyAsync(relativePath, bytes, cancellationToken);
        }
        public Task<byte[]?> ReadAsync(string relativePath, CancellationToken cancellationToken) => inner.ReadAsync(relativePath, cancellationToken);
        public Task<string?> ComputeSha256Async(string relativePath, CancellationToken cancellationToken) => inner.ComputeSha256Async(relativePath, cancellationToken);
        public Task<bool> DeleteAsync(string relativePath, CancellationToken cancellationToken) => throw new InvalidOperationException("Rasterizer must not delete historical resources");
    }

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
