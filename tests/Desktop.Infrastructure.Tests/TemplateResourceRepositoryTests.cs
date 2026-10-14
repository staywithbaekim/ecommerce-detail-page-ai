using Desktop.Application.Psd;
using Desktop.Application.Templates;
using Desktop.Domain.Templates;
using Desktop.Infrastructure.Persistence;

namespace Desktop.Infrastructure.Tests;

public sealed class TemplateResourceRepositoryTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InspectionEnrichmentPreservesIdentityAndRejectsStaleResourceMutation(bool mutateIdentity)
    {
        using var directory = new TestDirectory();
        await using var store = new SqliteDesktopStore(new DesktopDataOptions(directory.Path));
        await store.InitializeAsync();
        await store.SaveGroupAsync(TemplateGroup.Create("group_01", "Template", DateTimeOffset.UtcNow), default);
        var resource = Resource("resource_01");
        resource = resource with { Inspection = resource.Inspection with
        {
            LayerCount = 1,
            Layers = [new PsdLayerInspection([0], 41, "pixel", "pixel", false, true, true,
                false, 255, 255, "normal", new(0, 0, 10, 10), false, false, null, null)]
        } };
        await store.SaveImportedAsync("command_01", resource, default);
        var inspected = resource.Inspection with { Layers = [resource.Inspection.Layers[0] with
        {
            PixelEvidence = new(true, 100, 90, new(0, 0, 10, 10)) { RasterImage = new(true, 200, .1) }
        }] };
        if (mutateIdentity)
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => store.RefreshInspectionCacheAsync(
                resource with { InternalRelativePath = "templates/changed.psd" }, inspected, default));
            Assert.Equal("TPL_PSD_INSPECTION_CACHE_CONFLICT", error.Message);
            Assert.True((await store.GetByResourceIdAsync(resource.ResourceId, default))!.Inspection.NeedsRasterImageEvidence);
            return;
        }
        var enriched = await store.RefreshInspectionCacheAsync(resource, inspected, default);
        var racing = await store.RefreshInspectionCacheAsync(resource, inspected, default);
        Assert.Equal(enriched.Inspection.Layers[0].PixelEvidence, racing.Inspection.Layers[0].PixelEvidence);
        Assert.Equal(resource.Sha256, enriched.Sha256);
        Assert.Equal(resource.StructureSignature, enriched.StructureSignature);
        Assert.Equal(resource.CreatedAt, enriched.CreatedAt);
        Assert.Equal(resource.InternalRelativePath, enriched.InternalRelativePath);
        Assert.False((await store.GetByCommandIdAsync("command_01", default))!.Inspection.NeedsRasterImageEvidence);
        var errorConflict = await Assert.ThrowsAsync<InvalidOperationException>(() => store.RefreshInspectionCacheAsync(
            resource, inspected with { Layers = [inspected.Layers[0] with
            { PixelEvidence = inspected.Layers[0].PixelEvidence with { CompositeContributionPixelCount = 80 } }] }, default));
        Assert.Equal("TPL_PSD_INSPECTION_CACHE_CONFLICT", errorConflict.Message);
    }

    private const string FileHash =
        "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private const string StructureHash =
        "abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789";

    [Fact]
    public async Task ImportedResourceAndCommandRecoverAfterRestart()
    {
        using var directory = new TestDirectory();
        var options = new DesktopDataOptions(directory.Path);
        var group = TemplateGroup.Create("group_01", "Template", DateTimeOffset.UtcNow);
        var resource = Resource("resource_01");
        await using (var store = new SqliteDesktopStore(options))
        {
            await store.InitializeAsync();
            await store.SaveGroupAsync(group, CancellationToken.None);
            await store.SaveImportedAsync("command_01", resource, CancellationToken.None);
        }

        await using var reopened = new SqliteDesktopStore(options);
        await reopened.InitializeAsync();
        var byCommand = await reopened.GetByCommandIdAsync("command_01", CancellationToken.None);
        var duplicates = await reopened.FindBySha256Async(FileHash, CancellationToken.None);

        Assert.Equal(resource.ResourceId, byCommand!.ResourceId);
        Assert.Equal(resource.InternalRelativePath, byCommand.InternalRelativePath);
        Assert.Equal(StructureHash, byCommand.Inspection.StructureSignature);
        Assert.Equal(resource.ResourceId, Assert.Single(duplicates).ResourceId);
    }

    [Fact]
    public async Task ImportCommandIsIdempotentAndRejectsDifferentResource()
    {
        using var directory = new TestDirectory();
        await using var store = new SqliteDesktopStore(new DesktopDataOptions(directory.Path));
        await store.InitializeAsync();
        await store.SaveGroupAsync(
            TemplateGroup.Create("group_01", "Template", DateTimeOffset.UtcNow),
            CancellationToken.None);
        var resource = Resource("resource_01");

        var first = await store.SaveImportedAsync("command_01", resource, CancellationToken.None);
        var repeated = await store.SaveImportedAsync("command_01", resource, CancellationToken.None);
        var conflict = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.SaveImportedAsync("command_01", Resource("resource_02"), CancellationToken.None));

        Assert.Equal(first.ResourceId, repeated.ResourceId);
        Assert.Equal("TPL_IMPORT_COMMAND_CONFLICT", conflict.Message);
    }

    private static ImportedTemplateResource Resource(string resourceId) => new(
        resourceId,
        "group_01",
        "draft_01",
        $"templates/groups/group_01/drafts/draft_01/staging/{resourceId}/template.psd",
        4,
        FileHash,
        StructureHash,
        new PsdInspectionDocument(
            FileHash,
            4,
            new PsdCanvasInspection(10, 10, 8, "RGB"),
            0,
            0,
            0,
            0,
            0,
            StructureHash,
            new PsdInspectionTiming(1, 1, 2),
            1024,
            null,
            []),
        new DateTimeOffset(2026, 8, 27, 12, 0, 0, TimeSpan.Zero));
}
