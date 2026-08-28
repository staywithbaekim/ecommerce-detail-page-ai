using System.IO.Compression;
using System.Security.Cryptography;
using Desktop.Application.Templates;
using Desktop.Application.Psd;
using Desktop.Infrastructure.Templates;

namespace Desktop.Infrastructure.Tests;

public sealed class TemplatePackagePortTests
{
    [Fact]
    public async Task ExportAndValidateRoundTripRejectsSensitiveAndHashTampering()
    {
        using var directory = new TestDirectory();
        var path = System.IO.Path.Combine(directory.Path, "template.tplpkg");
        var sourcePath = System.IO.Path.Combine(directory.Path, "source.psd");
        var bytes = new byte[] { 1, 2, 3, 4 };
        await File.WriteAllBytesAsync(sourcePath, bytes);
        var sourceHash = Convert.ToHexString(SHA256.HashData(bytes));
        var manifest = new TemplatePackageManifest(
            TemplatePackageProtocol.Version,
            "模板组",
            2,
            "revision_02",
            "sha256:" + new string('a', 64),
            [new TemplatePackageEntry(
                "resource_01",
                "templates/resource_01.psd",
                bytes.Length,
                sourceHash)],
            [new TemplatePackagePage(
                "page_01",
                0,
                "页面",
                TemplatePageContentTypeOption.NormalComposition,
                TemplateGenerationModeOption.OncePerProduct,
                null,
                true,
                "resource_01",
                Inspection(sourceHash, bytes.Length),
                [Binding()])]);
        var port = new ZipTemplatePackagePort();

        await port.ExportAsync(
            manifest,
            new Dictionary<string, string> { ["resource_01"] = sourcePath },
            path,
            CancellationToken.None);
        var result = await port.ValidateAsync(path, CancellationToken.None);

        Assert.Equal(2, result.RevisionNo);
        Assert.Equal("revision_02", result.RevisionId);
        Assert.Single(result.Entries);
        var copiedPath = System.IO.Path.Combine(directory.Path, "copied.psd");
        await port.CopyValidatedEntryAsync(
            path,
            Assert.Single(result.Entries),
            copiedPath,
            CancellationToken.None);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(copiedPath));

        var inconsistent = manifest with
        {
            Pages = [manifest.Pages![0] with
            {
                Inspection = Inspection(new string('a', 64), bytes.Length)
            }]
        };
        var inconsistentPath = System.IO.Path.Combine(directory.Path, "inconsistent.tplpkg");
        var inconsistentException = await Assert.ThrowsAsync<InvalidDataException>(() => port.ExportAsync(
            inconsistent,
            new Dictionary<string, string> { ["resource_01"] = sourcePath },
            inconsistentPath,
            CancellationToken.None));
        Assert.Equal("TPL_PACKAGE_INVALID", inconsistentException.Message);

        await using (var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Update))
        {
            var extra = archive.CreateEntry("customer-photo.jpg");
            await using var extraStream = extra.Open();
            await extraStream.WriteAsync(new byte[] { 7, 8, 9 });
        }
        var extraException = await Assert.ThrowsAsync<InvalidDataException>(() =>
            port.ValidateAsync(path, CancellationToken.None));
        Assert.Equal("TPL_PACKAGE_ENTRY_INVALID", extraException.Message);

        await using (var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Update))
        {
            archive.GetEntry("customer-photo.jpg")!.Delete();
            var entry = archive.GetEntry("templates/resource_01.psd")!;
            await using var entryStream = entry.Open();
            entryStream.Position = 0;
            await entryStream.WriteAsync(new byte[] { 9, 9, 9, 9 });
        }

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            port.ValidateAsync(path, CancellationToken.None));
        Assert.Equal("TPL_PACKAGE_HASH_MISMATCH", exception.Message);
    }

    [Fact]
    public async Task InvalidVersionAndSensitivePathsAreRejectedBeforeReading()
    {
        using var directory = new TestDirectory();
        var path = System.IO.Path.Combine(directory.Path, "invalid.tplpkg");
        await using (var stream = new FileStream(path, FileMode.CreateNew))
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            var entry = archive.CreateEntry("manifest.json");
            await using var writer = new StreamWriter(entry.Open());
            await writer.WriteAsync("{\"protocol\":\"template-package/v9\",\"groupName\":\"x\",\"revisionNo\":1,\"revisionId\":\"r\",\"ruleDigest\":\"x\",\"entries\":[]}");
        }

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            new ZipTemplatePackagePort().ValidateAsync(path, CancellationToken.None));
        Assert.Equal("TPL_PACKAGE_VERSION_UNSUPPORTED", exception.Message);
    }

    private static TemplatePackageFrameBinding Binding() => new(
        "frame_01",
        "page_01",
        [2],
        257,
        "registered_frame",
        true,
        false,
        false,
        false,
        255,
        "normal",
        "sha256:" + new string('b', 64),
        "Choose the main product image.",
        [new TemplatePackageSelectionCriterion("asset_id", "asset_front")],
        "subject_complete",
        1,
        0,
        0,
        0,
        0,
        790,
        1252);

    private static PsdInspectionDocument Inspection(string hash, int length) => new(
        hash,
        length,
        new PsdCanvasInspection(790, 1252, 8, "RGB"),
        1,
        1,
        0,
        0,
        0,
        "sha256:" + new string('b', 64),
        new PsdInspectionTiming(1, 1, 2),
        1024,
        null,
        [new PsdLayerInspection(
            [2],
            257,
            "registered_frame",
            "smartObject",
            false,
            true,
            true,
            false,
            255,
            255,
            "normal",
            new PsdLayerBounds(0, 0, 790, 1252),
            false,
            false,
            null,
            new PsdSmartObjectInspection("u1", "asset.psb", "embedded", "psb", "psb", length, false, []))]);
}
