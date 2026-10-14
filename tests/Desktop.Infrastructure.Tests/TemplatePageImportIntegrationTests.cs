using System.Security.Cryptography;
using System.Text.Json;
using Desktop.Application.Psd;
using Desktop.Application.Templates;
using Desktop.Domain.Templates;
using Desktop.Infrastructure.Files;
using Desktop.Infrastructure.Persistence;

namespace Desktop.Infrastructure.Tests;

public sealed class TemplatePageImportIntegrationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EditorAndAtReferencesSeeUpgradedPixelTargetAndKeepSavedIdentity(bool referencesFirst)
    {
        using var appRoot = new TestDirectory();
        using var sourceRoot = new TestDirectory();
        var source = await WriteSourceAsync(sourceRoot.Path, "source.psd");
        var hash = await HashAsync(source);
        await using var store = new SqliteDesktopStore(new DesktopDataOptions(appRoot.Path));
        await store.InitializeAsync();
        await SaveGroupAsync(store);
        var legacy = LegacyPixelInspection(hash);
        await CreateService(appRoot.Path, store, legacy).ImportAsync(Request("command_old", "resource_old", source));
        var revision = TemplateRevision.CreateDraft("draft_01", "group_01", 1, DateTimeOffset.UtcNow,
            [TemplatePage.Create("page_01", "draft_01", 0, "照片页", TemplatePageContentType.NormalComposition,
                TemplateGenerationMode.OncePerProduct, null, true, "resource_old")]);
        await store.SaveDraftRevisionAsync(revision, default);
        var worker = new FakeInspectionPort(PhotoInspection(legacy));
        using var importer = CreateService(appRoot.Path, store, worker);
        var calibration = new TemplateCalibrationService(store, store, importer: importer);
        var editing = new TemplateWorkspaceService(store, store, importer, TimeProvider.System);
        var session = new TemplateDraftSession("group_01", "Template", "draft_01", 1, new HashSet<string>());
        if (referencesFirst) _ = await editing.GetRulesAsync(session, default);
        var view = await calibration.LoadAsync(session, "page_01", default);
        var frame = Assert.Single(view.Frames);
        Assert.Equal(TemplateLayerKind.Pixel, frame.LayerKind);
        var rules = await editing.GetRulesAsync(session, default);
        var candidate = Assert.Single(rules.Candidates);
        Assert.True(candidate.CanInterpret);
        Assert.Equal(TemplateLayerKind.Pixel, candidate.Target.LayerKind);
        var alias = Assert.Single(rules.Aliases.Where(item => item.FrameId == candidate.FrameId));
        Assert.Empty((await store.GetRevisionAsync("draft_01", default))!.FrameBindings);
        await calibration.SetSelectionRequirementAsync(session, "page_01", frame.FrameId, "商品正面图", default);
        var binding = Assert.Single((await store.GetRevisionAsync("draft_01", default))!.FrameBindings);
        Assert.Equal(candidate.FrameId, binding.FrameId);
        Assert.Equal(TemplateLayerKind.Pixel, binding.ExpectedLayer.Kind);
        var reopened = await new TemplateWorkspaceService(store, store, importer, TimeProvider.System).GetRulesAsync(session, default);
        Assert.Equal(alias, Assert.Single(reopened.Aliases.Where(item => item.FrameId == binding.FrameId)));
        Assert.Equal("商品正面图", Assert.Single(reopened.Frames).Description);
        var savedCandidate = Assert.Single(reopened.Candidates);
        Assert.Equal(binding.FrameId, savedCandidate.FrameId);
        Assert.False(savedCandidate.CanInterpret);
        Assert.Equal(binding.ExpectedLayer.StructureSignature.Value, savedCandidate.Target.StructureSignature);
        var excluded = await editing.ApplyRulePatchAsync(session, new("exclude", reopened.Version,
            new([], []) { CandidateVisibility = [new(binding.FrameId, true)] }), default);
        excluded = await editing.GetRulesAsync(session, default);
        Assert.Single(excluded.ExcludedCandidates);
        Assert.Empty((await store.GetRevisionAsync("draft_01", default))!.ActiveFrameBindings);
        var restored = await editing.ApplyRulePatchAsync(session, new("restore", excluded.Version,
            new([], []) { CandidateVisibility = [new(binding.FrameId, false)] }), default);
        restored = await editing.GetRulesAsync(session, default);
        Assert.Empty(restored.ExcludedCandidates);
        Assert.Equal(binding.FrameId, Assert.Single((await store.GetRevisionAsync("draft_01", default))!.ActiveFrameBindings).FrameId);
        Assert.Equal(1, worker.CallCount);
    }

    [Fact]
    public async Task CancelledCacheUpgradeDoesNotCallWorkerAndDoesNotBlockRetry()
    {
        using var appRoot = new TestDirectory();
        using var sourceRoot = new TestDirectory();
        var source = await WriteSourceAsync(sourceRoot.Path, "source.psd");
        var hash = await HashAsync(source);
        await using var store = new SqliteDesktopStore(new DesktopDataOptions(appRoot.Path));
        await store.InitializeAsync();
        await SaveGroupAsync(store);
        var legacy = LegacyPixelInspection(hash);
        var imported = await CreateService(appRoot.Path, store, legacy).ImportAsync(Request("command_old", "resource_old", source));
        var worker = new FakeInspectionPort(PhotoInspection(legacy));
        using var importer = CreateService(appRoot.Path, store, worker);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => importer.EnsureCurrentInspectionAsync(imported.Resource!, cancelled.Token));
        Assert.Equal(0, worker.CallCount);
        var updated = await importer.EnsureCurrentInspectionAsync(imported.Resource!, default);
        Assert.False(updated.Inspection.NeedsRasterImageEvidence);
        Assert.Equal(1, worker.CallCount);
    }

    [Fact]
    public async Task LegacyPixelCacheUpgradesOnceAcrossConcurrentEditorLoadsAndRestart()
    {
        using var appRoot = new TestDirectory();
        using var sourceRoot = new TestDirectory();
        var source = await WriteSourceAsync(sourceRoot.Path, "source.psd");
        var hash = await HashAsync(source);
        var options = new DesktopDataOptions(appRoot.Path);
        await using (var store = new SqliteDesktopStore(options))
        {
            await store.InitializeAsync();
            await SaveGroupAsync(store);
            var legacy = LegacyPixelInspection(hash);
            var imported = await CreateService(appRoot.Path, store, legacy)
                .ImportAsync(Request("command_old", "resource_old", source));
            var refreshed = PhotoInspection(legacy);
            var worker = new FakeInspectionPort(refreshed with { Inspection = refreshed.Inspection! with
            {
                FileSha256 = refreshed.Inspection.FileSha256.ToUpperInvariant(),
                StructureSignature = "sha256:" + refreshed.Inspection.StructureSignature.ToUpperInvariant()
            } });
            var service = CreateService(appRoot.Path, new SqliteTemplateRepository(store), worker);
            var upgraded = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ =>
                service.EnsureCurrentInspectionAsync(imported.Resource!, CancellationToken.None)));
            Assert.Equal(1, worker.CallCount);
            Assert.Null(worker.LastRequest!.PreviewRelativePath);
            Assert.False(worker.LastRequest.IncludeRenderContentEvidence);
            Assert.All(upgraded, resource =>
            {
                Assert.False(resource.Inspection.NeedsRasterImageEvidence);
                Assert.Equal(imported.Resource!.ResourceId, resource.ResourceId);
                Assert.Equal(imported.Resource.CreatedAt, resource.CreatedAt);
                Assert.Equal(imported.Resource.InternalRelativePath, resource.InternalRelativePath);
                Assert.Equal(legacy.Inspection!.Layers[0].ContentEvidence, resource.Inspection.Layers[0].ContentEvidence);
            });
            Assert.Equal(hash, await HashAsync(source));
            Assert.Equal(hash, await HashAsync(ToOwnedPath(appRoot.Path, imported.Resource!.InternalRelativePath)));
        }
        await using var reopened = new SqliteDesktopStore(options);
        await reopened.InitializeAsync();
        var cached = await reopened.GetByCommandIdAsync("command_old", CancellationToken.None);
        var unavailable = new FakeInspectionPort(PsdInspectionResult.Failure(new("unused", "unused", "unused", false)));
        var repeated = await CreateService(appRoot.Path, reopened, unavailable)
            .EnsureCurrentInspectionAsync(cached!, CancellationToken.None);
        Assert.False(repeated.Inspection.NeedsRasterImageEvidence);
        Assert.Equal(0, unavailable.CallCount);
    }

    [Theory]
    [InlineData("old-worker")]
    [InlineData("changed-file")]
    [InlineData("changed-layer")]
    [InlineData("failure")]
    public async Task CacheUpgradeRejectsIncompleteOrChangedEvidenceWithoutChangingStoredResource(string scenario)
    {
        using var appRoot = new TestDirectory();
        using var sourceRoot = new TestDirectory();
        var source = await WriteSourceAsync(sourceRoot.Path, "source.psd");
        var hash = await HashAsync(source);
        await using var store = new SqliteDesktopStore(new DesktopDataOptions(appRoot.Path));
        await store.InitializeAsync();
        await SaveGroupAsync(store);
        var legacy = LegacyPixelInspection(hash);
        var imported = await CreateService(appRoot.Path, store, legacy)
            .ImportAsync(Request("command_old", "resource_old", source));
        var fresh = PhotoInspection(legacy);
        var response = scenario switch
        {
            "old-worker" => legacy,
            "changed-file" => fresh with { Inspection = fresh.Inspection! with { FileSha256 = new string('f', 64) } },
            "changed-layer" => fresh with { Inspection = fresh.Inspection! with
                { Layers = [fresh.Inspection.Layers[0] with { LayerId = 999 }] } },
            _ => PsdInspectionResult.Failure(new("TPL_PSD_TIMEOUT", "Timeout", "Retry", true))
        };
        var worker = new FakeInspectionPort(response);
        var service = CreateService(appRoot.Path, store, worker);
        var error = await Assert.ThrowsAnyAsync<Exception>(() =>
            service.EnsureCurrentInspectionAsync(imported.Resource!, CancellationToken.None));
        Assert.Equal(scenario switch
        {
            "old-worker" => "TPL_PSD_WORKER_IMAGE_EVIDENCE_REQUIRED",
            "failure" => "TPL_PSD_TIMEOUT",
            _ => "TPL_PSD_INSPECTION_CACHE_MISMATCH"
        }, error.Message);
        var stored = await store.GetByResourceIdAsync("resource_old", CancellationToken.None);
        Assert.Equal(JsonSerializer.Serialize(imported.Resource), JsonSerializer.Serialize(stored));
        Assert.Equal(1, worker.CallCount);
        Assert.Equal(hash, await HashAsync(source));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DuplicateAndIdempotentImportsUpgradeLegacyCacheWithoutNewResource(bool completedCommand)
    {
        using var appRoot = new TestDirectory();
        using var sourceRoot = new TestDirectory();
        var source = await WriteSourceAsync(sourceRoot.Path, "source.psd");
        var hash = await HashAsync(source);
        await using var store = new SqliteDesktopStore(new DesktopDataOptions(appRoot.Path));
        await store.InitializeAsync();
        await SaveGroupAsync(store);
        var legacy = LegacyPixelInspection(hash);
        await CreateService(appRoot.Path, store, legacy).ImportAsync(Request("command_old", "resource_old", source));
        var worker = new FakeInspectionPort(PhotoInspection(legacy));
        var service = CreateService(appRoot.Path, store, worker);
        var request = completedCommand ? Request("command_old", "resource_old", source)
            : Request("command_new", "resource_new", source) with
            { DuplicateDecision = TemplateDuplicateDecision.AutoReuseDraftDuplicates };
        var result = await service.ImportAsync(request);
        Assert.True(result.IsSuccess);
        Assert.False(result.Resource!.Inspection.NeedsRasterImageEvidence);
        Assert.Equal("resource_old", result.Resource.ResourceId);
        Assert.Equal(1, worker.CallCount);
        Assert.Single(await store.FindBySha256Async(hash, CancellationToken.None));
        var repeated = await service.ImportAsync(request);
        Assert.True(repeated.Idempotent);
        Assert.Equal(1, worker.CallCount);
    }

    private static PsdInspectionResult LegacyPixelInspection(string hash)
    {
        var inspection = SuccessInspection(hash).Inspection!;
        return PsdInspectionResult.Success(inspection with
        {
            LayerCount = 1,
            Layers = [new PsdLayerInspection([0], 42, "photo", "pixel", false, true, true,
                false, 255, 255, "normal", new(0, 0, 10, 10), false, false, null, null)
                { ContentEvidence = new(true, new string('c', 64), null, null, null, null) }]
        });
    }

    private static PsdInspectionResult PhotoInspection(PsdInspectionResult legacy) =>
        PsdInspectionResult.Success(legacy.Inspection! with
        {
            Layers = legacy.Inspection.Layers.Select(layer => layer with
            {
                ContentEvidence = null, // Normal inspection cannot erase stronger cached render evidence.
                PixelEvidence = new(true, 100, 100, new(0, 0, 10, 10))
                { RasterImage = new(true, 256, .1) }
            }).ToArray()
        });

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
        public int CallCount { get; private set; }
        public PsdInspectionRequest? LastRequest { get; private set; }
        public Task<PsdInspectionResult> InspectAsync(
            PsdInspectionRequest request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            LastRequest = request;
            return Task.FromResult(result);
        }
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
