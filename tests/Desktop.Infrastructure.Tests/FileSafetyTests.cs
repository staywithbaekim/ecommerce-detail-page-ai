using System.Security.Cryptography;
using System.Text;
using Desktop.Application.Files;
using Desktop.Infrastructure.Diagnostics;
using Desktop.Infrastructure.Files;

namespace Desktop.Infrastructure.Tests;

public sealed class FileSafetyTests
{
    [Fact]
    public async Task AtomicStoreWritesOnlyInsideOwnedRootAndPreservesProtectedSource()
    {
        using var directory = new TestDirectory();
        var protectedDirectory = System.IO.Path.Combine(directory.Path, "protected");
        Directory.CreateDirectory(protectedDirectory);
        var sourcePath = System.IO.Path.Combine(protectedDirectory, "source.psd");
        await File.WriteAllBytesAsync(sourcePath, "protected-source"u8.ToArray());
        var hashBefore = await HashAsync(sourcePath);

        var policy = new LocalAppPathPolicy(System.IO.Path.Combine(directory.Path, "app-owned"));
        var fileStore = new AtomicFileStore(policy);
        await fileStore.WriteAtomicallyAsync(
            System.IO.Path.Combine("manifests", "task.json"),
            "{\"state\":\"ready\"}"u8.ToArray(),
            CancellationToken.None);

        var hashAfter = await HashAsync(sourcePath);
        Assert.Equal(hashBefore, hashAfter);
        Assert.Throws<InvalidOperationException>(() => policy.ResolveOwnedPath(
            System.IO.Path.Combine("..", "protected", "source.psd")));
    }

    [Fact]
    public async Task ManifestRoundTripsThroughAtomicStore()
    {
        using var directory = new TestDirectory();
        var fileStore = new AtomicFileStore(new LocalAppPathPolicy(directory.Path));
        var store = new JsonManifestStore(fileStore);
        var manifest = new AppManifest(
            "manifest-01",
            1,
            [new ManifestEntry("logical-01", "owned/item.json", 12, "ABCDEF", "verified")],
            DateTimeOffset.UtcNow);

        await store.SaveAsync("manifests/manifest.json", manifest, CancellationToken.None);
        var loaded = await store.LoadAsync("manifests/manifest.json", CancellationToken.None);

        Assert.NotNull(loaded);
        Assert.Equal(manifest.ManifestId, loaded.ManifestId);
        Assert.Equal(manifest.Version, loaded.Version);
        Assert.Equal(manifest.UpdatedAt, loaded.UpdatedAt);
        Assert.Equal(manifest.Entries, loaded.Entries);
    }

    [Fact]
    public void LoggerRedactsSecretsPathsAndMediaNames()
    {
        using var directory = new TestDirectory();
        var policy = new LocalAppPathPolicy(directory.Path);
        var logger = new RedactingFileLogger(policy, System.IO.Path.Combine("logs", "test.jsonl"));
        logger.Information("TEST_EVENT", new Dictionary<string, object?>
        {
            ["api_key"] = "secret-value",
            ["input"] = @"C:\Customers\photo.jpg",
            ["asset"] = "result.psd"
        });

        var log = File.ReadAllText(policy.ResolveOwnedPath(System.IO.Path.Combine("logs", "test.jsonl")));
        Assert.DoesNotContain("secret-value", log, StringComparison.Ordinal);
        Assert.DoesNotContain(@"C:\Customers", log, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("photo.jpg", log, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("result.psd", log, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("[REDACTED]", log, StringComparison.Ordinal);
    }

    private static async Task<string> HashAsync(string path)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream));
    }
}
