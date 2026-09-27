using System.Security.Cryptography;
using Desktop.Application.Psd;
using Desktop.Application.Templates;
using Desktop.Infrastructure.Bridge;

namespace Desktop.Infrastructure.Tests;

public sealed class RenderBatchResourcePreparerTests
{
    [Fact]
    public void PersistedTemplateEvidenceAuthorizesMatchingPlanWithoutReinspection()
    {
        using var fixture = new BridgeResourceLeaseTests.Fixture();
        var resource = TemplateResource(fixture);

        RenderBatchResourcePreparer.ValidateTemplateEvidence(fixture.Plan.ToJsonString(), resource);
    }

    [Theory]
    [InlineData("resource")]
    [InlineData("length")]
    [InlineData("hash")]
    [InlineData("structure")]
    [InlineData("inspection_hash")]
    [InlineData("target")]
    public void MissingOrMismatchedPersistedTemplateEvidenceFailsClosed(string mutation)
    {
        using var fixture = new BridgeResourceLeaseTests.Fixture();
        var resource = TemplateResource(fixture);
        if (mutation == "resource") resource = resource with { ResourceId = "other_resource" };
        if (mutation == "length") resource = resource with { Length = resource.Length + 1 };
        if (mutation == "hash") resource = resource with { Sha256 = new string('0', 64) };
        if (mutation == "structure") resource = resource with { StructureSignature = new string('0', 64) };
        if (mutation == "inspection_hash") resource = resource with { Inspection = resource.Inspection with { FileSha256 = new string('0', 64) } };
        if (mutation == "target") fixture.Plan["targets"]![0]!["expected_layer"]!["layer_id"] = 999;

        Assert.Throws<InvalidDataException>(() =>
            RenderBatchResourcePreparer.ValidateTemplateEvidence(fixture.Plan.ToJsonString(), resource));
    }

    [Fact]
    public async Task LocalAppDataSourceSupportsPackagedFileSystemVirtualization()
    {
        using var root = new TestDirectory();
        var sourceRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "EcommerceDetailPage.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(sourceRoot);
        var source = Path.Combine(sourceRoot, "source.png");
        byte[] bytes = [137, 80, 78, 71, 13, 10, 26, 10];
        try
        {
            await File.WriteAllBytesAsync(source, bytes);
            var destination = Path.Combine(root.Path, "copy.png");
            var owned = await RenderBatchResourcePreparer.CopyVerifiedAsync(source, destination, bytes.Length, Hash(bytes), default);
            Assert.Equal(bytes, await File.ReadAllBytesAsync(destination));
            owned.Dispose();
            Assert.False(File.Exists(destination));
            Assert.Equal(bytes, await File.ReadAllBytesAsync(source));
        }
        finally { Directory.Delete(sourceRoot, recursive: true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OpenedHandleCannotBeSubstitutedForAnotherExpectedPath(bool differentDirectory)
    {
        using var root = new TestDirectory();
        var source = Path.Combine(root.Path, "source.png");
        File.WriteAllText(source, "same bytes");
        var otherDirectory = differentDirectory ? Path.Combine(root.Path, "other") : root.Path;
        Directory.CreateDirectory(otherDirectory);
        var expected = Path.Combine(otherDirectory, differentDirectory ? "source.png" : "different.png");
        File.WriteAllText(expected, "same bytes");
        using var opened = File.OpenRead(source);
        Assert.Throws<InvalidDataException>(() => BridgeResourceLease.VerifyOpenedPath(opened.SafeFileHandle, expected));
    }

    [Fact]
    public async Task ReleaseSupportsActualPlanResourcePathsLongerThanMaxPath()
    {
        using var root = new TestDirectory();
        var source = Path.Combine(root.Path, "source.jpg");
        var copy = Path.Combine(root.Path, ".app-staging", "resources", new string('a', 64),
            new string('b', 32), new string('c', 64) + ".jpg");
        Assert.True(copy.Length > 260);
        byte[] bytes = [255, 216, 255, 1, 2, 3, 4, 5];
        await File.WriteAllBytesAsync(source, bytes);
        var owned = await RenderBatchResourcePreparer.CopyVerifiedAsync(source, copy, bytes.Length, Hash(bytes), default);
        owned.Dispose();
        Assert.False(File.Exists(copy));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(source));
    }

    [Fact]
    public async Task ReleaseRemovesOnlyNewCopyAfterAllReadersClose()
    {
        using var root = new TestDirectory();
        var source = Path.Combine(root.Path, "source.jpg");
        var copy = Path.Combine(root.Path, ".app-staging", "new-copy.jpg");
        var unrelated = Path.Combine(root.Path, "old-output.jpg");
        byte[] bytes = [255, 216, 255, 1, 2, 3, 4, 5];
        await File.WriteAllBytesAsync(source, bytes);
        await File.WriteAllBytesAsync(unrelated, bytes);
        var owned = await RenderBatchResourcePreparer.CopyVerifiedAsync(source, copy, bytes.Length, Hash(bytes), default);
        using (var reader = File.Open(copy, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.Throws<IOException>(owned.Dispose);
            Assert.Equal(bytes.Length, reader.Length);
        }
        owned.Dispose(); owned.Dispose();
        Assert.False(File.Exists(copy));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(source));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(unrelated));
        Assert.True(Directory.Exists(Path.GetDirectoryName(copy)));
    }

    [Fact]
    public async Task ReplacedPathIsNeverDeletedEvenWhenItsBytesMatch()
    {
        using var root = new TestDirectory();
        var source = Path.Combine(root.Path, "source.jpg");
        var copy = Path.Combine(root.Path, "new-copy.jpg");
        var moved = Path.Combine(root.Path, "moved-copy.jpg");
        byte[] bytes = [255, 216, 255, 1, 2, 3, 4, 5];
        await File.WriteAllBytesAsync(source, bytes);
        var owned = await RenderBatchResourcePreparer.CopyVerifiedAsync(source, copy, bytes.Length, Hash(bytes), default);
        File.Move(copy, moved);
        await File.WriteAllBytesAsync(copy, bytes);
        Assert.Throws<IOException>(owned.Dispose);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(copy));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(moved));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(source));
    }

    [Fact]
    public async Task HashFailureReleasesNewCopyAndNameCollisionPreservesExistingFile()
    {
        using var root = new TestDirectory();
        var source = Path.Combine(root.Path, "source.jpg");
        var copy = Path.Combine(root.Path, "new-copy.jpg");
        byte[] bytes = [255, 216, 255, 1, 2, 3, 4, 5];
        await File.WriteAllBytesAsync(source, bytes);
        await Assert.ThrowsAsync<InvalidDataException>(() => RenderBatchResourcePreparer.CopyVerifiedAsync(
            source, copy, bytes.Length, new string('0', 64), default));
        Assert.False(File.Exists(copy));
        await File.WriteAllTextAsync(copy, "unrelated existing output");
        await Assert.ThrowsAsync<IOException>(() => RenderBatchResourcePreparer.CopyVerifiedAsync(source, copy, bytes.Length, Hash(bytes), default));
        Assert.Equal("unrelated existing output", await File.ReadAllTextAsync(copy));
        await Assert.ThrowsAsync<IOException>(() => RenderBatchResourcePreparer.CopyVerifiedAsync(source, source, bytes.Length, Hash(bytes), default));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(source));
    }

    private static ImportedTemplateResource TemplateResource(BridgeResourceLeaseTests.Fixture fixture)
    {
        var plan = fixture.Plan;
        var resourcePlan = plan["resources"]![0]!;
        var expected = plan["targets"]![0]!["expected_layer"]!;
        var hash = resourcePlan["sha256"]!.GetValue<string>();
        var signature = expected["structure_signature"]!.GetValue<string>();
        var layer = new PsdLayerInspection([1], expected["layer_id"]!.GetValue<int>(),
            expected["name_before"]!.GetValue<string>(), "smartobject", false, true, true,
            expected["clipping"]!.GetValue<bool>(), expected["opacity"]!.GetValue<int>(), 255,
            expected["blend_mode"]!.GetValue<string>(), new(0, 0, 790, 1252), false, false, null,
            new PsdSmartObjectInspection("embedded_1", "source.jpg", "data", "jpg", "jpg", 8, false, [])
            { EmbeddedContentAvailable = true, EmbeddedContentSha256 = plan["resources"]![1]!["sha256"]!.GetValue<string>() });
        var inspection = new PsdInspectionDocument(hash, resourcePlan["byte_length"]!.GetValue<int>(),
            new(790, 1252, 8, "RGB"), 1, 1, 0, 0, 0, signature, new(0, 0, 0), 0, null, [layer]);
        return new(resourcePlan["resource_id"]!.GetValue<string>(), "group_01", "revision_01", "template.psd",
            inspection.SizeBytes, hash, signature, inspection, DateTimeOffset.UnixEpoch);
    }

    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}
