using Desktop.Application.Files;
using Desktop.Application.Psd;
using Desktop.Application.Templates;
using Desktop.Contracts.Ai;
using Desktop.Contracts.Validation;
using Desktop.Domain.Templates;
using Desktop.Infrastructure.Files;
using Desktop.Infrastructure.Persistence;
using Desktop.Infrastructure.Templates;

namespace Desktop.Infrastructure.Tests;

public sealed class TemplatePackageIntegrationTests
{
    [Fact]
    public async Task FrozenV1ImportsOneBasedPathsAndReexportsExplicitV2WithoutChangingBindings()
    {
        using var directory = new TestDirectory();
        var bytes = new byte[] { 0x38, 0x42, 0x50, 0x53, 1, 2, 3, 4 };
        var hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));
        var sourcePath = Path.Combine(directory.Path, "source.psd");
        await File.WriteAllBytesAsync(sourcePath, bytes);
        var manifest = new TemplatePackageManifest(TemplatePackageProtocol.Version, "v1 migration", 1,
            "revision", "sha256:" + new string('c', 64), [new("resource", "resources/source.psd", bytes.Length, hash)],
            [new("page_01", 0, "normal", TemplatePageContentTypeOption.NormalComposition,
                TemplateGenerationModeOption.OncePerProduct, null, true, "resource", Inspection(hash, bytes.Length),
                [PackageBinding() with { PageId = "page_01" }])]);
        var port = new ZipTemplatePackagePort(new JsonSchemaContractValidator());
        var oldPackage = Path.Combine(directory.Path, "v1.tplpkg");
        await port.ExportAsync(manifest, new Dictionary<string, string> { ["resource"] = sourcePath }, oldPackage, default);
        await using var store = new SqliteDesktopStore(new(Path.Combine(directory.Path, "data")));
        await store.InitializeAsync();
        var paths = new LocalAppPathPolicy(Path.Combine(directory.Path, "data"));
        var files = new AtomicFileStore(paths); var time = new FixedTimeProvider(DateTimeOffset.UtcNow);
        var workspace = Workspace(store, files, new JsonManifestStore(files), time);
        var service = new TemplatePackageService(store, store, port, files, paths, workspace, time);
        var group = await service.ImportAsNewGroupAsync(oldPackage, null, default);
        var revision = (await store.GetRevisionAsync(group.RevisionId, default))!;
        var resource = (await store.GetByResourceIdAsync(revision.Pages[0].TemplateResourceId, default))!;
        Assert.Equal(1, Assert.Single(resource.Inspection.Layers[0].Path));
        Assert.Equal(2, Assert.Single(revision.FrameBindings[0].LayerPath.Segments));
        var newPackage = Path.Combine(directory.Path, "v2.tplpkg");
        await service.ExportCurrentAsync(group.GroupId, newPackage, default);
        var migrated = (await port.ValidateAsync(newPackage, default)).Manifest!;
        Assert.Equal(TemplatePackageProtocol.CurrentVersion, migrated.Protocol);
        Assert.Equal(1, Assert.Single(migrated.Pages![0].Inspection!.Layers[0].Path));
        Assert.Equal(2, Assert.Single(migrated.Pages[0].Bindings![0].LayerPath));
        await Assert.ThrowsAsync<InvalidDataException>(() => port.ExportAsync(migrated with
        {
            Pages = [migrated.Pages[0] with { Bindings = [migrated.Pages[0].Bindings![0] with { LayerPath = [1] }] }]
        }, new Dictionary<string, string>(), Path.Combine(directory.Path, "wrong-index.tplpkg"), default));
    }

    [Fact]
    public async Task DynamicCalibrationRoundtripsPackageAndIsCoveredByCommittedManifest()
    {
        using var directory = new TestDirectory();
        using var packageDirectory = new TestDirectory();
        var bytes = new byte[] { 0x38, 0x42, 0x50, 0x53, 1, 2, 3, 4 };
        var hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));
        var sourcePath = Path.Combine(packageDirectory.Path, "source.psd");
        await File.WriteAllBytesAsync(sourcePath, bytes);
        var basis = Inspection(hash, bytes.Length);
        var layer = basis.Layers[0] with { Path = [0], Kind = "group", IsGroup = true, SmartObject = null,
            Visible = true, EffectiveVisible = true, Clipping = false };
        var inspection = basis with { Layers = [layer], LayerCount = 1, SmartObjectCount = 0 };
        var reference = new SizeTableTemplateLayer(1, layer.LayerId, layer.Kind, layer.Name, inspection.StructureSignature);
        var calibration = new SizeTableTemplateCalibration("size-table-template/v1", hash, "new table", reference, [reference],
            TemplateRect.Create(10, 20, 700, 1000),
            new("Microsoft YaHei UI", 24, 1000, 56, 24, 12, "#FFFFFFFF", "#FFF2F4F7", "#FF101828", "#FF98A2B3"));
        var manifest = new TemplatePackageManifest(TemplatePackageProtocol.CurrentVersion, "size template", 1, "revision", "sha256:" + new string('c', 64),
            [new("resource", "resources/source.psd", bytes.Length, hash)],
            [new("page", 0, "size", TemplatePageContentTypeOption.DynamicSizeTable, TemplateGenerationModeOption.OncePerProduct,
                null, true, "resource", inspection, [], calibration)]);
        var package = Path.Combine(packageDirectory.Path, "size.tplpkg");
        var port = new ZipTemplatePackagePort(new JsonSchemaContractValidator());
        await port.ExportAsync(manifest, new Dictionary<string, string> { ["resource"] = sourcePath }, package, default);
        await using var store = new SqliteDesktopStore(new(directory.Path)); await store.InitializeAsync();
        var paths = new LocalAppPathPolicy(directory.Path); var files = new AtomicFileStore(paths);
        var manifests = new JsonManifestStore(files); var time = new FixedTimeProvider(DateTimeOffset.UtcNow);
        var workspace = Workspace(store, files, manifests, time);
        var service = new TemplatePackageService(store, store, port, files, paths, workspace, time);
        var imported = await service.ImportAsNewGroupAsync(package, null, default);
        var revision = (await store.GetRevisionAsync(imported.RevisionId, default))!;
        var page = Assert.Single(revision.Pages);
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(calibration), System.Text.Json.JsonSerializer.Serialize(page.SizeTableCalibration));
        using var committedManifest = System.Text.Json.JsonDocument.Parse((await files.ReadAsync(revision.ManifestRef!, default))!);
        Assert.Contains("size_" + page.PageId, committedManifest.RootElement.ToString(), StringComparison.Ordinal);
        var clone = await service.SaveAsNewGroupAsync(imported.GroupId, "size copy", default);
        var copied = (await store.GetRevisionAsync(clone.RevisionId, default))!.Pages[0];
        Assert.NotEqual(page.PageId, copied.PageId);
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(calibration), System.Text.Json.JsonSerializer.Serialize(copied.SizeTableCalibration));
        var edit = await workspace.BeginPageEditAsync(imported.GroupId, page.PageId, default);
        var editPage = Assert.Single((await workspace.GetDraftAsync(edit, default)).Pages);
        var editedCalibration = editPage.Page.SizeTableCalibration! with { LayerName = "revised table" };
        await Assert.ThrowsAsync<TemplateRuleViolationException>(() => workspace.CalibrateSizeTableAsync(edit, page.PageId,
            editedCalibration with { TemplateSha256 = new string('0', 64) }, default));
        await workspace.CalibrateSizeTableAsync(edit, page.PageId, editedCalibration, default);
        var edited = await workspace.CommitDraftAsync(edit, default);
        Assert.NotEqual(revision.RuleDigest, (await store.GetRevisionAsync(edited.RevisionId, default))!.RuleDigest);
        Assert.Equal("new table", (await store.GetRevisionAsync(imported.RevisionId, default))!.Pages[0].SizeTableCalibration!.LayerName);
        await Assert.ThrowsAsync<InvalidDataException>(() => port.ExportAsync(manifest with {
            Pages = [manifest.Pages![0] with { SizeTableCalibration = calibration with { TemplateSha256 = new string('0', 64) } }] },
            new Dictionary<string, string> { ["resource"] = sourcePath }, Path.Combine(packageDirectory.Path, "bad.tplpkg"), default));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(sourcePath));
    }

    [Fact]
    public async Task CopyFailureRemovesImportedDraftDatabaseRowsAndOwnedFiles()
    {
        using var packageDirectory = new TestDirectory();
        using var targetDirectory = new TestDirectory();
        var packagePath = Path.Combine(packageDirectory.Path, "template.tplpkg");
        var sourcePath = Path.Combine(packageDirectory.Path, "source.psd");
        var bytes = new byte[] { 0x38, 0x42, 0x50, 0x53, 1, 2, 3, 4 };
        await File.WriteAllBytesAsync(sourcePath, bytes);
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();
        var manifest = new TemplatePackageManifest(
            TemplatePackageProtocol.Version,
            "故障注入模板",
            1,
            "revision_package_failure",
            "sha256:" + new string('c', 64),
            [new TemplatePackageEntry("resource_source", "resources/source.psd", bytes.Length, hash)],
            [new TemplatePackagePage(
                "page_source",
                0,
                "页面",
                TemplatePageContentTypeOption.NormalComposition,
                TemplateGenerationModeOption.OncePerProduct,
                null,
                true,
                "resource_source",
                Inspection(hash, bytes.Length),
                [PackageBinding()])]);
        var realPort = new ZipTemplatePackagePort(new JsonSchemaContractValidator());
        await realPort.ExportAsync(
            manifest,
            new Dictionary<string, string> { ["resource_source"] = sourcePath },
            packagePath,
            CancellationToken.None);

        var options = new DesktopDataOptions(targetDirectory.Path);
        var time = new FixedTimeProvider(DateTimeOffset.UtcNow);
        await using var target = new SqliteDesktopStore(options);
        await target.InitializeAsync();
        var paths = new LocalAppPathPolicy(targetDirectory.Path);
        var files = new AtomicFileStore(paths);
        var service = new TemplatePackageService(
            target,
            target,
            new FailOnCopyPackagePort(realPort),
            files,
            paths,
            Workspace(target, files, new JsonManifestStore(files), time),
            time,
            new TemplateOwnedStorage(paths));

        var exception = await Assert.ThrowsAsync<IOException>(() => service.ImportAsNewGroupAsync(
            packagePath,
            null,
            CancellationToken.None));

        Assert.Equal("TPL_PACKAGE_COPY_INJECTED", exception.Message);
        Assert.Empty(await target.ListGroupsAsync(CancellationToken.None));
        var groupsDirectory = Path.Combine(targetDirectory.Path, "templates", "groups");
        Assert.False(Directory.Exists(groupsDirectory)
            && Directory.EnumerateFileSystemEntries(groupsDirectory).Any());
    }

    [Fact]
    public async Task ExportThenImportIntoBlankDataRootPreservesRulesAndPsdHash()
    {
        using var sourceDirectory = new TestDirectory();
        using var targetDirectory = new TestDirectory();
        using var packageDirectory = new TestDirectory();
        var packagePath = Path.Combine(packageDirectory.Path, "template.tplpkg");
        var sourceOptions = new DesktopDataOptions(sourceDirectory.Path);
        var targetOptions = new DesktopDataOptions(targetDirectory.Path);
        var now = new DateTimeOffset(2026, 8, 28, 4, 0, 0, TimeSpan.Zero);
        var time = new FixedTimeProvider(now);
        var psdBytes = new byte[] { 0x38, 0x42, 0x50, 0x53, 1, 2, 3, 4 };
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(psdBytes)).ToLowerInvariant();

        await using var source = new SqliteDesktopStore(sourceOptions);
        await source.InitializeAsync();
        var sourcePaths = new LocalAppPathPolicy(sourceDirectory.Path);
        var sourceFiles = new AtomicFileStore(sourcePaths);
        var sourceManifest = new JsonManifestStore(sourceFiles);
        var sourceWorkspace = Workspace(source, sourceFiles, sourceManifest, time);
        var group = TemplateGroup.Create("group_package_source", "可迁移模板", now);
        var resourcePath =
            "templates/groups/group_package_source/revisions/revision_package_source/pages/page_package_source/template.psd";
        await sourceFiles.WriteAtomicallyAsync(resourcePath, psdBytes, CancellationToken.None);
        var workerInspection = Inspection(hash, psdBytes.Length) with
        {
            StructureSignature = new string('b', 64),
            LayerCount = 2,
            SmartObjectCount = 2,
            Layers = [Inspection(hash, psdBytes.Length).Layers[0] with
            {
                Path = [1],
                ContentEvidence = new(true, new string('d', 64), null, null, null, null)
            }, Inspection(hash, psdBytes.Length).Layers[0] with
            {
                Path = [0], LayerId = 256, Name = "old_product",
                SmartObject = Inspection(hash, psdBytes.Length).Layers[0].SmartObject! with { Kind = "data" }
            }]
        };
        var resource = new ImportedTemplateResource(
            "resource_package_source",
            group.GroupId,
            "revision_package_source",
            resourcePath,
            psdBytes.Length,
            hash,
            "sha256:" + new string('b', 64),
            workerInspection,
            now);
        var draft = TemplateRevision.CreateDraft(
            "revision_package_source",
            group.GroupId,
            1,
            now,
            [TemplatePage.Create(
                "page_package_source",
                "revision_package_source",
                0,
                "主图页",
                TemplatePageContentType.NormalComposition,
                TemplateGenerationMode.OncePerProduct,
                null,
                true,
                resource.ResourceId)],
            [Binding("frame_package_source", "page_package_source",
                [SupersededLayerEvidence.Create(LayerPath.Create([1]), false, 256)])]);
        await source.SaveGroupAsync(group, CancellationToken.None);
        await source.SaveImportedAsync("import_package_source", resource, CancellationToken.None);
        await source.CommitRevisionAsync(Commit(draft), null, CancellationToken.None);

        var sourcePackages = new TemplatePackageService(
            source,
            source,
            new ZipTemplatePackagePort(new JsonSchemaContractValidator()),
            sourceFiles,
            sourcePaths,
            sourceWorkspace,
            time);
        await sourcePackages.ExportCurrentAsync(group.GroupId, packagePath, CancellationToken.None);
        var storedInspection = (await source.GetByResourceIdAsync(resource.ResourceId, default))!.Inspection;
        Assert.Equal(new string('b', 64), storedInspection.StructureSignature);
        Assert.NotNull(storedInspection.Layers[0].ContentEvidence);
        using (var archive = System.IO.Compression.ZipFile.OpenRead(packagePath))
        using (var reader = new StreamReader(archive.GetEntry("manifest.json")!.Open()))
        {
            var packageJson = await reader.ReadToEndAsync();
            Assert.DoesNotContain("contentEvidence", packageJson, StringComparison.Ordinal);
            Assert.True(new JsonSchemaContractValidator().Validate(ContractDocumentKind.TemplatePackageManifestV2, packageJson).IsValid);
        }

        await using var target = new SqliteDesktopStore(targetOptions);
        await target.InitializeAsync();
        var targetPaths = new LocalAppPathPolicy(targetDirectory.Path);
        var targetFiles = new AtomicFileStore(targetPaths);
        var targetManifest = new JsonManifestStore(targetFiles);
        var targetWorkspace = Workspace(target, targetFiles, targetManifest, time);
        var targetPackages = new TemplatePackageService(
            target,
            target,
            new ZipTemplatePackagePort(new JsonSchemaContractValidator()),
            targetFiles,
            targetPaths,
            targetWorkspace,
            time);

        var imported = await targetPackages.ImportAsNewGroupAsync(
            packagePath,
            null,
            CancellationToken.None);
        var reopenedRevision = await target.GetRevisionAsync(imported.RevisionId, CancellationToken.None);
        var importedPage = Assert.Single(reopenedRevision!.Pages);
        var importedResource = await target.GetByResourceIdAsync(
            importedPage.TemplateResourceId,
            CancellationToken.None);
        var importedBytes = await targetFiles.ReadAsync(
            importedResource!.InternalRelativePath,
            CancellationToken.None);

        Assert.NotEqual(group.GroupId, imported.GroupId);
        Assert.Equal("可迁移模板", imported.Name);
        Assert.Equal(hash, importedResource.Sha256);
        Assert.Equal("sha256:" + new string('b', 64), importedResource.Inspection.StructureSignature);
        Assert.Null(importedResource.Inspection.Layers[0].ContentEvidence);
        Assert.Equal(psdBytes, importedBytes);
        var importedHidden = Assert.Single(Assert.Single(reopenedRevision.FrameBindings).SupersededLayers);
        Assert.Equal(1, Assert.Single(importedHidden.LayerPath.Segments));
        Assert.Equal(256, importedHidden.ExpectedLayerId);
        Assert.True(importedHidden.ExpectedVisible);
        Assert.False(importedHidden.ExpectedClipping);
        var importedCriteria = Assert.Single(reopenedRevision.FrameBindings)
            .SelectionRequirement.Criteria.ToDictionary(
                criterion => criterion.Key,
                criterion => criterion.Value,
                StringComparer.Ordinal);
        Assert.Equal("asset_front", importedCriteria["asset_id"]);
        Assert.Equal("reference-audit/v1", importedCriteria["reference_audit_version"]);
        Assert.Equal("sha256:" + new string('8', 64), importedCriteria["reference_set_digest"]);
        Assert.Equal(new string('9', 64) + "." + new string('a', 64), importedCriteria["reference_hashes_00"]);
        Assert.Equal(ReferenceLearningAiService.ModelConfigVersion, importedCriteria["semantic_model_version"]);
        Assert.Equal(ReferenceLearningAiProtocol.SemanticPromptVersion, importedCriteria["semantic_prompt_version"]);
        Assert.NotNull(await targetManifest.ReadMarkerAsync(
            $"templates/groups/{imported.GroupId}/revisions/{imported.RevisionId}",
            "committed.marker",
            CancellationToken.None));

        var savedAs = await targetPackages.SaveAsNewGroupAsync(
            imported.GroupId,
            "可迁移模板 副本",
            CancellationToken.None);
        Assert.NotEqual(imported.GroupId, savedAs.GroupId);
        Assert.Equal("可迁移模板 副本", savedAs.Name);
        var savedAsRevision = (await target.GetRevisionAsync(savedAs.RevisionId, default))!;
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(importedHidden),
            System.Text.Json.JsonSerializer.Serialize(Assert.Single(Assert.Single(savedAsRevision.FrameBindings).SupersededLayers)));
        Assert.False(Directory.EnumerateFiles(
            Path.Combine(targetDirectory.Path, "template-package-staging"),
            "*.tplpkg",
            SearchOption.AllDirectories).Any());
    }

    private static TemplateWorkspaceService Workspace(
        SqliteDesktopStore store,
        IFileStore files,
        IManifestStore manifests,
        TimeProvider time) => new(
            store,
            store,
            new TemplatePageImportService(
                new UnusedSourceFilePort(),
                new UnusedInspectionPort(),
                store,
                time),
            time,
            manifests, files: files);

    private static FrameBinding Binding(string frameId, string pageId,
        IReadOnlyList<SupersededLayerEvidence>? superseded = null) => FrameBinding.Create(
        frameId,
        pageId,
        LayerPath.Create([2]),
        ExpectedLayerEvidence.Create(
            257,
            "registered_frame",
            true,
            false,
            false,
            false,
            100,
            "normal",
            LayerStructureSignature.Create("sha256:" + new string('b', 64))),
        superseded ?? [],
        SelectionRequirement.Create(
            "Choose the main product image.",
            [
                SelectionCriterion.Create("asset_id", "asset_front"),
                SelectionCriterion.Create("reference_audit_version", "reference-audit/v1"),
                SelectionCriterion.Create("reference_set_digest", "sha256:" + new string('8', 64)),
                SelectionCriterion.Create("reference_hashes_00", new string('9', 64) + "." + new string('a', 64)),
                SelectionCriterion.Create("semantic_model_version", ReferenceLearningAiService.ModelConfigVersion),
                SelectionCriterion.Create("semantic_prompt_version", ReferenceLearningAiProtocol.SemanticPromptVersion)
            ]),
        LayoutIntent.Create("subject_complete", 1.2, 4, -3, TemplateRect.Create(0, 0, 790, 1252)));

    private static TemplatePackageFrameBinding PackageBinding() => new(
        "frame_package_source",
        "page_source",
        [2],
        257,
        "registered_frame",
        true,
        false,
        false,
        false,
        100,
        "normal",
        "sha256:" + new string('b', 64),
        "Choose the main product image.",
        [new TemplatePackageSelectionCriterion("asset_id", "asset_front")],
        "subject_complete",
        1.2,
        4,
        -3,
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
            100,
            100,
            "normal",
            new PsdLayerBounds(0, 0, 790, 1252),
            false,
            false,
            null,
            new PsdSmartObjectInspection("u1", "asset.psb", "embedded", "psb", "psb", length, false, []))]);

    private static TemplateRevision Commit(TemplateRevision revision)
    {
        var importing = revision.AdvanceTo(
            TemplateRevisionState.ImportingCopy,
            new RevisionTransitionEvidence(SourceReadable: true));
        var inspecting = importing.AdvanceTo(
            TemplateRevisionState.StructureInspecting,
            new RevisionTransitionEvidence(CopyMatchesSource: true));
        var binding = inspecting.AdvanceTo(
            TemplateRevisionState.FrameBinding,
            new RevisionTransitionEvidence(StructureReadable: true));
        var calibrating = binding.AdvanceTo(
            TemplateRevisionState.Calibrating,
            new RevisionTransitionEvidence(RegisteredFramesHaveEvidence: true));
        var review = calibrating.AdvanceTo(
            TemplateRevisionState.AwaitingReview,
            new RevisionTransitionEvidence(CalibrationComplete: true));
        var committing = review.AdvanceTo(
            TemplateRevisionState.Committing,
            new RevisionTransitionEvidence(ConfigurationValid: true));
        return committing.Commit(
            $"templates/groups/{revision.GroupId}/revisions/{revision.RevisionId}/revision-manifest.json",
            "sha256:" + new string('c', 64),
            new RevisionTransitionEvidence(CommitArtifactsConsistent: true));
    }

    private sealed class UnusedSourceFilePort : ITemplateSourceFilePort
    {
        public Task<SourceFingerprintResult> FingerprintAsync(string sourcePsdPath, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public Task<OwnedTemplateCopyResult> CopyVerifiedAsync(
            string sourcePsdPath,
            TemplateSourceFingerprint expectedSource,
            string destinationRelativePath,
            CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> DeleteOwnedCopyAsync(string internalRelativePath, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class UnusedInspectionPort : IPsdInspectionPort
    {
        public Task<PsdInspectionResult> InspectAsync(PsdInspectionRequest request, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class FailOnCopyPackagePort(ITemplatePackagePort inner) : ITemplatePackagePort
    {
        public Task ExportAsync(
            TemplatePackageManifest manifest,
            IReadOnlyDictionary<string, string> sourceFiles,
            string destinationPath,
            CancellationToken cancellationToken) => inner.ExportAsync(
                manifest,
                sourceFiles,
                destinationPath,
                cancellationToken);

        public Task<TemplatePackageImportResult> ValidateAsync(
            string packagePath,
            CancellationToken cancellationToken) => inner.ValidateAsync(packagePath, cancellationToken);

        public Task CopyValidatedEntryAsync(
            string packagePath,
            TemplatePackageEntry entry,
            string destinationPath,
            CancellationToken cancellationToken) => throw new IOException("TPL_PACKAGE_COPY_INJECTED");
    }
}
