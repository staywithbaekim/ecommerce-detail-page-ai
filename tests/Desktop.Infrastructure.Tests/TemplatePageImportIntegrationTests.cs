using System.Security.Cryptography;
using Desktop.Application.Psd;
using Desktop.Application.Templates;
using Desktop.Domain.Templates;
using Desktop.Infrastructure.Files;
using Desktop.Infrastructure.Persistence;

namespace Desktop.Infrastructure.Tests;

public sealed class TemplatePageImportIntegrationTests
{
    [Fact]
    public async Task ImportRequestsApplicationOwnedCompositePreviewWithoutChangingSource()
    {
        using var appRoot = new TestDirectory();
        using var sourceRoot = new TestDirectory();
        var sourcePath = await WriteSourceAsync(sourceRoot.Path, "source.psd");
        var sourceHashBefore = await HashAsync(sourcePath);
        await using var store = new SqliteDesktopStore(new DesktopDataOptions(appRoot.Path));
        await store.InitializeAsync();
        await SaveGroupAsync(store);
        var inspection = SuccessInspection(sourceHashBefore);
        var preview = new PreviewWritingInspectionPort(
            appRoot.Path,
            inspection with
            {
                Inspection = inspection.Inspection! with
                {
                    Preview = new PsdPreviewInspection(10, 10, "RGBA", new string('b', 64), 8)
                }
            });
        var service = CreateService(appRoot.Path, store, preview);

        var result = await service.ImportAsync(Request("command_preview", "resource_preview", sourcePath));

        Assert.True(result.IsSuccess);
        Assert.Equal(sourceHashBefore, await HashAsync(sourcePath));
        Assert.NotNull(preview.LastRequest?.PreviewRelativePath);
        Assert.True(File.Exists(ToOwnedPath(appRoot.Path, preview.LastRequest!.PreviewRelativePath!)));
        Assert.DoesNotContain(sourcePath, preview.LastRequest.PreviewRelativePath!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ImportPreservesSourceAndRecoversResourceAfterRestart()
    {
        using var appRoot = new TestDirectory();
        using var sourceRoot = new TestDirectory();
        var sourcePath = await WriteSourceAsync(sourceRoot.Path, "source.psd");
        var sourceHashBefore = await HashAsync(sourcePath);
        var options = new DesktopDataOptions(appRoot.Path);

        await using (var store = new SqliteDesktopStore(options))
        {
            await store.InitializeAsync();
            await SaveGroupAsync(store);
            var service = CreateService(appRoot.Path, store, SuccessInspection(sourceHashBefore));

            var result = await service.ImportAsync(Request("command_01", "resource_01", sourcePath));

            Assert.True(result.IsSuccess);
            Assert.Equal(sourceHashBefore, await HashAsync(sourcePath));
            Assert.DoesNotContain(sourcePath, result.Resource!.InternalRelativePath, StringComparison.OrdinalIgnoreCase);
            Assert.True(File.Exists(ToOwnedPath(appRoot.Path, result.Resource.InternalRelativePath)));
        }

        await using var reopened = new SqliteDesktopStore(options);
        await reopened.InitializeAsync();
        var recovered = await reopened.GetByCommandIdAsync("command_01", CancellationToken.None);

        Assert.Equal("resource_01", recovered!.ResourceId);
        Assert.Equal(sourceHashBefore, recovered.Sha256);
        Assert.Equal(sourceHashBefore, await HashAsync(sourcePath));
    }

    [Fact]
    public async Task InspectionFailureLeavesNoResourceOrOwnedPsdAndPreservesSource()
    {
        using var appRoot = new TestDirectory();
        using var sourceRoot = new TestDirectory();
        var sourcePath = await WriteSourceAsync(sourceRoot.Path, "source.psd");
        var sourceHashBefore = await HashAsync(sourcePath);
        await using var store = new SqliteDesktopStore(new DesktopDataOptions(appRoot.Path));
        await store.InitializeAsync();
        await SaveGroupAsync(store);
        var service = CreateService(
            appRoot.Path,
            store,
            PsdInspectionResult.Failure(new PsdInspectionError(
                "TPL_PSD_UNSUPPORTED_STRUCTURE",
                "Unsupported.",
                "Choose another PSD.",
                Retryable: false)));

        var result = await service.ImportAsync(Request("command_01", "resource_01", sourcePath));

        Assert.Equal(TemplatePageImportStatus.Failed, result.Status);
        Assert.Equal("TPL_PSD_UNSUPPORTED_STRUCTURE", result.Error!.Code);
        Assert.Null(await store.GetByCommandIdAsync("command_01", CancellationToken.None));
        Assert.Empty(await store.FindBySha256Async(sourceHashBefore, CancellationToken.None));
        Assert.Empty(Directory.GetFiles(appRoot.Path, "*.psd", SearchOption.AllDirectories));
        Assert.Equal(sourceHashBefore, await HashAsync(sourcePath));
    }

    [Fact]
    public async Task DuplicateReviewKeepsBothSourcesAndDoesNotCreateSecondResource()
    {
        using var appRoot = new TestDirectory();
        using var sourceRoot = new TestDirectory();
        var firstSource = await WriteSourceAsync(sourceRoot.Path, "first.psd");
        var secondSource = await WriteSourceAsync(sourceRoot.Path, "second.psd");
        var sourceHash = await HashAsync(firstSource);
        await using var store = new SqliteDesktopStore(new DesktopDataOptions(appRoot.Path));
        await store.InitializeAsync();
        await SaveGroupAsync(store);
        var service = CreateService(appRoot.Path, store, SuccessInspection(sourceHash));
        var first = await service.ImportAsync(Request("command_01", "resource_01", firstSource));

        var duplicate = await service.ImportAsync(Request("command_02", "resource_02", secondSource));

        Assert.True(first.IsSuccess);
        Assert.Equal(TemplatePageImportStatus.DuplicateReviewRequired, duplicate.Status);
        Assert.Equal("resource_01", Assert.Single(duplicate.DuplicateCandidates).ResourceId);
        Assert.True(File.Exists(firstSource));
        Assert.True(File.Exists(secondSource));
        Assert.Equal(sourceHash, await HashAsync(firstSource));
        Assert.Equal(sourceHash, await HashAsync(secondSource));
        Assert.Null(await store.GetByCommandIdAsync("command_02", CancellationToken.None));
        Assert.Single(await store.FindBySha256Async(sourceHash, CancellationToken.None));
        Assert.True(File.Exists(ToOwnedPath(
            appRoot.Path,
            "templates/groups/group_01/drafts/draft_01/staging/resource_02/template.psd")));
    }

    [Fact]
    public async Task DatabaseFailureRollsBackResourceAndDeletesOwnedPsd()
    {
        using var appRoot = new TestDirectory();
        using var sourceRoot = new TestDirectory();
        var sourcePath = await WriteSourceAsync(sourceRoot.Path, "source.psd");
        var sourceHashBefore = await HashAsync(sourcePath);
        await using var store = new SqliteDesktopStore(new DesktopDataOptions(appRoot.Path));
        await store.InitializeAsync();
        var service = CreateService(appRoot.Path, store, SuccessInspection(sourceHashBefore));

        var result = await service.ImportAsync(Request("command_01", "resource_01", sourcePath));

        Assert.Equal(TemplatePageImportStatus.Failed, result.Status);
        Assert.Equal("TPL_REVISION_COMMIT_FAILED", result.Error!.Code);
        Assert.Null(await store.GetByCommandIdAsync("command_01", CancellationToken.None));
        Assert.Empty(await store.FindBySha256Async(sourceHashBefore, CancellationToken.None));
        Assert.Empty(Directory.GetFiles(appRoot.Path, "*.psd", SearchOption.AllDirectories));
        Assert.Equal(sourceHashBefore, await HashAsync(sourcePath));
    }

    private static TemplatePageImportService CreateService(
        string appRoot,
        ITemplateResourceRepository repository,
        PsdInspectionResult inspection) => CreateService(
            appRoot,
            repository,
            new FakeInspectionPort(inspection));

    private static TemplatePageImportService CreateService(
        string appRoot,
        ITemplateResourceRepository repository,
        IPsdInspectionPort inspection) => new(
            new TemplateSourceFilePort(new LocalAppPathPolicy(appRoot)),
            inspection,
            repository,
            new FixedTimeProvider(new DateTimeOffset(2026, 8, 27, 12, 0, 0, TimeSpan.Zero)));

    private static TemplatePageImportRequest Request(string commandId, string resourceId, string sourcePath) => new(
        commandId,
        "group_01",
        "draft_01",
        resourceId,
        sourcePath);

    private static async Task SaveGroupAsync(SqliteDesktopStore repository) =>
        await repository.SaveGroupAsync(
            TemplateGroup.Create("group_01", "Template", DateTimeOffset.UtcNow),
            CancellationToken.None);

    private static PsdInspectionResult SuccessInspection(string sha256) => PsdInspectionResult.Success(
        new PsdInspectionDocument(
            sha256,
            4,
            new PsdCanvasInspection(10, 10, 8, "RGB"),
            0,
            0,
            0,
            0,
            0,
            new string('a', 64),
            new PsdInspectionTiming(1, 1, 2),
            1024,
            null,
            []));

    private static async Task<string> WriteSourceAsync(string root, string name)
    {
        var path = Path.Combine(root, name);
        await File.WriteAllBytesAsync(path, [1, 2, 3, 4]);
        return path;
    }

    private static async Task<string> HashAsync(string path)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream)).ToLowerInvariant();
    }

    private static string ToOwnedPath(string appRoot, string relativePath) => Path.Combine(
        appRoot,
        relativePath.Replace('/', Path.DirectorySeparatorChar));

    private sealed class FakeInspectionPort(PsdInspectionResult result) : IPsdInspectionPort
    {
        public Task<PsdInspectionResult> InspectAsync(
            PsdInspectionRequest request,
            CancellationToken cancellationToken) => Task.FromResult(result);
    }

    private sealed class PreviewWritingInspectionPort(
        string appRoot,
        PsdInspectionResult result) : IPsdInspectionPort
    {
        public PsdInspectionRequest? LastRequest { get; private set; }

        public async Task<PsdInspectionResult> InspectAsync(
            PsdInspectionRequest request,
            CancellationToken cancellationToken)
        {
            LastRequest = request;
            if (request.PreviewRelativePath is null)
            {
                return PsdInspectionResult.Failure(new PsdInspectionError(
                    "TPL_PSD_REQUEST_INVALID",
                    "Preview path required.",
                    "Retry.",
                    false));
            }

            var path = ToOwnedPath(appRoot, request.PreviewRelativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllBytesAsync(path, [137, 80, 78, 71, 1, 2, 3, 4], cancellationToken);
            return result;
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
