using System.Security.Cryptography;
using Desktop.Application.Templates;
using Desktop.Infrastructure.Files;

namespace Desktop.Infrastructure.Tests;

public sealed class TemplateSourceFilePortTests
{
    [Fact]
    public async Task CopiesToOwnedStagingWithoutChangingSource()
    {
        using var appRoot = new TestDirectory();
        using var sourceRoot = new TestDirectory();
        var sourcePath = await WriteSourceAsync(sourceRoot.Path);
        var sourceHashBefore = await HashAsync(sourcePath);
        var port = new TemplateSourceFilePort(new LocalAppPathPolicy(appRoot.Path));
        var fingerprint = await port.FingerprintAsync(sourcePath, CancellationToken.None);

        var result = await port.CopyVerifiedAsync(
            sourcePath,
            fingerprint.Fingerprint!,
            "templates/groups/group_01/drafts/draft_01/staging/resource_01/template.psd",
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(sourceHashBefore, result.Copy!.Sha256);
        Assert.Equal(sourceHashBefore, await HashAsync(sourcePath));
        var internalPath = Path.Combine(
            appRoot.Path,
            result.Copy.InternalRelativePath.Replace('/', Path.DirectorySeparatorChar));
        Assert.Equal(sourceHashBefore, await HashAsync(internalPath));
    }

    [Fact]
    public async Task ChangedFingerprintLeavesNoInternalOrPartFile()
    {
        using var appRoot = new TestDirectory();
        using var sourceRoot = new TestDirectory();
        var sourcePath = await WriteSourceAsync(sourceRoot.Path);
        var port = new TemplateSourceFilePort(new LocalAppPathPolicy(appRoot.Path));
        const string relativePath =
            "templates/groups/group_01/drafts/draft_01/staging/resource_01/template.psd";

        var result = await port.CopyVerifiedAsync(
            sourcePath,
            new TemplateSourceFingerprint(4, new string('0', 64)),
            relativePath,
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("TPL_SOURCE_CHANGED_DURING_COPY", result.Error!.Code);
        Assert.False(File.Exists(Path.Combine(
            appRoot.Path,
            relativePath.Replace('/', Path.DirectorySeparatorChar))));
        Assert.Empty(Directory.GetFiles(appRoot.Path, "*.part", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task RejectsEscapingDestinationAndNeverTouchesSource()
    {
        using var appRoot = new TestDirectory();
        using var sourceRoot = new TestDirectory();
        var sourcePath = await WriteSourceAsync(sourceRoot.Path);
        var sourceHashBefore = await HashAsync(sourcePath);
        var port = new TemplateSourceFilePort(new LocalAppPathPolicy(appRoot.Path));
        var fingerprint = await port.FingerprintAsync(sourcePath, CancellationToken.None);

        var result = await port.CopyVerifiedAsync(
            sourcePath,
            fingerprint.Fingerprint!,
            "../outside.psd",
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("TPL_INTERNAL_PATH_INVALID", result.Error!.Code);
        Assert.Equal(sourceHashBefore, await HashAsync(sourcePath));
    }

    [Fact]
    public async Task CleanupDeletesOnlyOwnedCopyAndKeepsSource()
    {
        using var appRoot = new TestDirectory();
        using var sourceRoot = new TestDirectory();
        var sourcePath = await WriteSourceAsync(sourceRoot.Path);
        var sourceHashBefore = await HashAsync(sourcePath);
        var port = new TemplateSourceFilePort(new LocalAppPathPolicy(appRoot.Path));
        var fingerprint = await port.FingerprintAsync(sourcePath, CancellationToken.None);
        const string relativePath =
            "templates/groups/group_01/drafts/draft_01/staging/resource_01/template.psd";
        var copied = await port.CopyVerifiedAsync(
            sourcePath,
            fingerprint.Fingerprint!,
            relativePath,
            CancellationToken.None);

        var deleted = await port.DeleteOwnedCopyAsync(relativePath, CancellationToken.None);

        Assert.True(copied.IsSuccess);
        Assert.True(deleted);
        Assert.Equal(sourceHashBefore, await HashAsync(sourcePath));
        Assert.False(File.Exists(Path.Combine(
            appRoot.Path,
            relativePath.Replace('/', Path.DirectorySeparatorChar))));
    }

    private static async Task<string> WriteSourceAsync(string root)
    {
        var path = Path.Combine(root, "readonly-source.psd");
        await File.WriteAllBytesAsync(path, [1, 2, 3, 4]);
        return path;
    }

    private static async Task<string> HashAsync(string path)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream)).ToLowerInvariant();
    }
}
