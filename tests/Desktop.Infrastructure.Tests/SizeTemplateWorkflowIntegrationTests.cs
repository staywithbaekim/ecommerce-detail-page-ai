using System.Security.Cryptography;
using System.Text.Json;
using Desktop.Application.Ai;
using Desktop.Application.Psd;
using Desktop.Application.Rendering;
using Desktop.Application.SizeTables;
using Desktop.Application.Templates;
using Desktop.Contracts.Validation;
using Desktop.Domain.Inputs;
using Desktop.Domain.SizeTables;
using Desktop.Domain.Templates;
using Desktop.Infrastructure.Bridge;
using Desktop.Infrastructure.Files;
using Desktop.Infrastructure.Persistence;
using Desktop.Infrastructure.Templates;
using Microsoft.Data.Sqlite;

namespace Desktop.Infrastructure.Tests;

public sealed class SizeTemplateWorkflowIntegrationTests
{
    [Theory]
    [InlineData(TemplatePageContentType.NormalComposition)]
    [InlineData(TemplatePageContentType.DynamicSizeTable)]
    public async Task PageTypeRoundTripPreservesBothCalibrationsAndUsesOnlyCurrentRulesAfterReopen(
        TemplatePageContentType initialType)
    {
        using var directory = new TestDirectory();
        var paths = new LocalAppPathPolicy(directory.Path);
        var files = new AtomicFileStore(paths);
        var options = new DesktopDataOptions(directory.Path);
        TemplateDraftSession session;
        string expectedBinding;
        string expectedCalibration;
        await using (var store = new SqliteDesktopStore(options))
        {
            await store.InitializeAsync();
            var workspace = Workspace(store, paths, files);
            session = await workspace.BeginCreateAsync("page type round trip", default);
            var bytes = new byte[] { 0x38, 0x42, 0x50, 0x53 };
            var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
            const string resourcePath = "templates/fixture/template.psd";
            await files.WriteAtomicallyAsync(resourcePath, bytes, default);
            var inspection = Inspection(hash);
            await store.SaveImportedAsync("import", new("resource", session.GroupId, session.DraftRevisionId,
                resourcePath, bytes.Length, hash, inspection.StructureSignature, inspection, DateTimeOffset.UnixEpoch), default);
            var page = TemplatePage.Create("page", session.DraftRevisionId, 0, "switchable page",
                TemplatePageContentType.NormalComposition, TemplateGenerationMode.OncePerProduct, null, true,
                "resource", typeRecognition: TemplatePageTypeRecognition.SizeTablePage);
            var binding = FrameBinding.Create("frame", page.PageId, LayerPath.Create([1]),
                ExpectedLayerEvidence.Create(1, "photo", true, false, false, false, 255, "norm",
                    LayerStructureSignature.Create(inspection.StructureSignature)), [],
                SelectionRequirement.Create("本款正面", [SelectionCriterion.Create("view", "front")]),
                LayoutIntent.Create("subject_complete", 1.1, 4, -3, TemplateRect.Create(0, 0, 790, 1000)));
            var draft = (await store.GetRevisionAsync(session.DraftRevisionId, default))!.AddPage(page).AddFrameBinding(binding);
            await store.SaveDraftRevisionAsync(draft, default);
            await workspace.ConfigurePageAsync(session, page.PageId, page.Name, TemplatePageContentTypeOption.DynamicSizeTable,
                TemplateGenerationModeOption.OncePerProduct, null, true, default);
            var layer = new TemplateSizeLayer(2, 2, "group", "old table", inspection.StructureSignature);
            await workspace.CalibrateSizeTableAsync(session, page.PageId,
                new("size-table-template/v1", hash, "new table", layer, [layer], new(20, 200, 770, 900),
                    new("Microsoft YaHei UI", 24, 1000, 56, 24, 12,
                        "#FFFFFFFF", "#FFF2F4F7", "#FF101828", "#FF98A2B3")), default);
            var calibrated = (await store.GetRevisionAsync(session.DraftRevisionId, default))!;
            expectedBinding = JsonSerializer.Serialize(binding);
            expectedCalibration = JsonSerializer.Serialize(calibrated.Pages[0].SizeTableCalibration);
            await ConfigureTypeAsync(workspace, session, initialType);
        }

        var currentType = initialType;
        var committedDigests = new Dictionary<string, string>();
        for (var step = 0; step < 3; step++)
        {
            string revisionId;
            // A fresh store must recover the persisted draft before committing it.
            await using (var store = new SqliteDesktopStore(options))
            {
                await store.InitializeAsync();
                var draft = (await store.GetRevisionAsync(session.DraftRevisionId, default))!;
                AssertCalibrationContents(draft);
                var committed = await Workspace(store, paths, files).CommitDraftAsync(session, default);
                revisionId = committed.RevisionId;
            }

            await using (var reopened = new SqliteDesktopStore(options))
            {
                await reopened.InitializeAsync();
                Assert.Equal(revisionId, (await reopened.GetGroupAsync(session.GroupId, default))!.CurrentRevisionId);
                var revision = (await reopened.GetRevisionAsync(revisionId, default))!;
                Assert.Equal(TemplateRevisionState.Committed, revision.State);
                AssertCalibrationContents(revision);
                await AssertCurrentTypePlanAsync(reopened, revision, directory.Path);
                committedDigests.Add(revisionId, revision.RuleDigest!);
                foreach (var previous in committedDigests)
                    Assert.Equal(previous.Value, (await reopened.GetRevisionAsync(previous.Key, default))!.RuleDigest);

                if (step < 2)
                {
                    var editor = Workspace(reopened, paths, files);
                    session = await editor.BeginFullEditAsync(session.GroupId, default);
                    AssertCalibrationContents((await reopened.GetRevisionAsync(session.DraftRevisionId, default))!);
                    currentType = currentType == TemplatePageContentType.NormalComposition
                        ? TemplatePageContentType.DynamicSizeTable : TemplatePageContentType.NormalComposition;
                    await ConfigureTypeAsync(editor, session, currentType);
                    AssertCalibrationContents((await reopened.GetRevisionAsync(session.DraftRevisionId, default))!);
                }
            }
        }

        void AssertCalibrationContents(TemplateRevision revision)
        {
            AssertTracks(revision, currentType);
            Assert.Equal(expectedBinding, JsonSerializer.Serialize(Assert.Single(revision.FrameBindings)));
            Assert.Equal(expectedCalibration, JsonSerializer.Serialize(revision.Pages[0].SizeTableCalibration));
        }
    }

    private static Task ConfigureTypeAsync(TemplateWorkspaceService workspace, TemplateDraftSession session,
        TemplatePageContentType type) => workspace.ConfigurePageAsync(session, "page", "switchable page",
            type == TemplatePageContentType.NormalComposition
                ? TemplatePageContentTypeOption.NormalComposition : TemplatePageContentTypeOption.DynamicSizeTable,
            TemplateGenerationModeOption.OncePerProduct, null, true, default);

    private static async Task AssertCurrentTypePlanAsync(SqliteDesktopStore store, TemplateRevision revision, string root)
    {
        var page = Assert.Single(revision.Pages);
        var isSize = page.ContentType == TemplatePageContentType.DynamicSizeTable;
        var now = DateTimeOffset.UnixEpoch;
        var snapshot = BatchTemplateSnapshot.Create("snapshot", "batch", revision, [page.PageId], now);
        Assert.Equal(page.ContentType, Assert.Single(snapshot.Pages).ContentType);
        Assert.Equal(isSize ? Array.Empty<string>() : ["frame"], snapshot.Pages[0].FrameIds.ToArray());
        var asset = SourceAssetMetadata.CreateReadable("asset_front", "style", "front.jpg", "front.jpg",
            128, new string('c', 64), 790, 1000, SourceImageFormat.Jpeg, "image/jpeg");
        var batch = BatchDraft.Create("batch", Path.Combine(root, "sources"), Path.Combine(root, "outputs"),
                snapshot, BatchSelectionMode.Automatic, false, now)
            .BeginScanning(now.AddTicks(1))
            .MarkScanReady(BatchInputType.SingleStyle, [StyleDraft.Create("style", "fixture", "", 0, [asset.AssetId])],
                [asset], "batches/batch/asset-manifest/manifest.json", "sha256:" + new string('d', 64), now.AddTicks(2));
        var resource = (await store.GetByResourceIdAsync(page.TemplateResourceId, default))!;
        var table = SizeTable.FromCandidate("table", batch.BatchId, "style", page.PageId,
            SizeTableCandidate.Create("candidate", SizeTableSource.Create(asset.AssetId, asset.Sha256!),
                0.99m, ["尺码", "袖长"], [["L", "/"]]), now);
        var rasterStyle = SizeTableCalibrationResolver.RasterStyle(page.SizeTableCalibration!);
        var raster = new SizeTableRasterizedResource("size_raster", "deterministic_size_table", "image/png",
            new string('e', 64), 4096, 1000, 420, table.DataDigest, rasterStyle.StyleVersion, rasterStyle.LayoutVersion,
            "batches/batch/size-tables/style/page/resources/table.png");
        var prepared = new AiPreparedSizeTablePage(page.PageId, table, raster,
            CalibratedInsertion: isSize ? SizeTableCalibrationResolver.Resolve(page, raster) : null);
        AiConfirmedRenderSelection[] selections = isSize ? []
            : [new("frame", asset.AssetId, new(asset.AssetId, 0.1m, 0.1m, 0.9m, 0.9m, 0.99m), 0)];
        var validator = new RenderPlanValidator(new JsonSchemaContractValidator());
        var factory = new AiRenderPlanFactory(new TemplateRenderPlanBuilder(validator),
            new UnavailablePhotoshopAvailabilityPort(), new SizeTableRenderPlanBuilder(validator), store);
        var style = new AiRenderStyleInput("style", selections, PreparedSizeTablePages: [prepared]);
        var request = new AiRenderPlanPreparationRequest(batch, revision, [resource], [style], now);
        var result = await factory.PrepareAsync(request, default);
        Assert.Equal(AiRenderPreparationStatus.WaitingForPhotoshop, result.Status);
        var planned = Assert.Single(result.Pages);
        Assert.Equal(AiRenderPagePreparationStatus.ReadyToRender, planned.Status);
        Assert.True(planned.HasPlan);
        Assert.True(validator.Validate(planned.RenderPlanJson!).IsValid);
        Assert.True(RenderTemplateEvidenceValidator.Matches(planned.RenderPlanJson!, resource.Inspection));
        using var json = JsonDocument.Parse(planned.RenderPlanJson!);
        var plan = json.RootElement;
        Assert.Equal(isSize ? "dynamic_size_table" : "ordinary_set",
            plan.GetProperty("document").GetProperty("content_type").GetString());
        Assert.Equal(new[] { resource.ResourceId, isSize ? raster.ResourceId : asset.AssetId },
            plan.GetProperty("resources").EnumerateArray().Select(item => item.GetProperty("resource_id").GetString()).ToArray());
        if (isSize)
        {
            Assert.Empty(plan.GetProperty("targets").EnumerateArray());
            var size = plan.GetProperty("size_table");
            Assert.Equal(table.DataDigest, size.GetProperty("structured_data_digest").GetString());
            Assert.Equal("/", size.GetProperty("rows")[0].GetProperty("cells").GetProperty("col_002").GetString());
            var insertion = size.GetProperty("insertion");
            Assert.Equal(page.SizeTableCalibration!.LayerName, insertion.GetProperty("layer_name").GetString());
            Assert.Equal(2, insertion.GetProperty("above_layer").GetProperty("layer_id").GetInt32());
            Assert.Equal(2, Assert.Single(insertion.GetProperty("hidden_layers").EnumerateArray()).GetProperty("layer_id").GetInt32());
            Assert.Equal(20, insertion.GetProperty("safe_bounds").GetProperty("left").GetDouble());
        }
        else
        {
            var target = Assert.Single(plan.GetProperty("targets").EnumerateArray());
            Assert.Equal("frame", target.GetProperty("frame_id").GetString());
            Assert.Equal(1, target.GetProperty("expected_layer").GetProperty("layer_id").GetInt32());
            Assert.Equal(asset.AssetId, target.GetProperty("asset_resource_id").GetString());
            Assert.Equal(JsonValueKind.Null, plan.GetProperty("size_table").ValueKind);
            Assert.DoesNotContain(plan.GetProperty("required_capabilities").EnumerateArray(),
                capability => capability.GetString() == RenderPlanPlacementMigration.SizeInsertionCapability);
            var withoutSize = await factory.PrepareAsync(request with
            { Styles = [style with { PreparedSizeTablePages = [] }] }, default);
            Assert.Equal(planned.RenderPlanJson, Assert.Single(withoutSize.Pages).RenderPlanJson);
        }

        // Retained calibration cannot substitute for missing inputs of the active page type.
        var missingInput = isSize ? style with { PreparedSizeTablePages = [] } : style with { Selections = [] };
        var blocked = Assert.Single((await factory.PrepareAsync(request with { Styles = [missingInput] }, default)).Pages);
        Assert.Equal(AiRenderPagePreparationStatus.BusinessBlocked, blocked.Status);
        Assert.Equal(isSize ? "AI_SIZE_TABLE_CANDIDATE_MISSING" : "AI_RENDER_REQUIRED_SELECTION_MISSING", blocked.ReasonCode);
        Assert.False(blocked.HasPlan);

        // Filtering inactive bindings must not allow a frozen snapshot to carry the wrong frame set.
        var invalidSnapshot = BatchTemplateSnapshot.Restore(snapshot.SnapshotId, batch.BatchId, revision.RevisionId,
            [snapshot.Pages[0] with { FrameIds = isSize ? ["frame"] : [] }], revision.RuleDigest!, now);
        var invalidBatch = BatchDraft.Restore(batch.BatchId, batch.ScanStatus, batch.InputType,
            batch.InputRoot, batch.OutputRoot, invalidSnapshot, batch.SelectionMode, batch.IncludeEditablePsd,
            batch.Styles, batch.Assets, batch.ManifestReference, batch.ManifestDigest, batch.BlockReason,
            batch.CreatedAt, batch.UpdatedAt);
        var mismatch = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            factory.PrepareAsync(request with { Batch = invalidBatch }, default));
        Assert.Equal("AI_RENDER_SNAPSHOT_PAGE_MISMATCH", mismatch.Message);
    }

    [Fact]
    public async Task BothCalibrationTracksSurviveCommitReopenSaveAsAndV6Package()
    {
        using var directory = new TestDirectory();
        var paths = new LocalAppPathPolicy(directory.Path);
        var files = new AtomicFileStore(paths);
        var options = new DesktopDataOptions(directory.Path);
        string groupId;
        string revisionId;
        string originalDigest;
        string expectedBinding;
        string expectedCalibration;
        await using (var store = new SqliteDesktopStore(options))
        {
            await store.InitializeAsync();
            var workspace = Workspace(store, paths, files);
            var session = await workspace.BeginCreateAsync("two calibration tracks", default);
            groupId = session.GroupId;
            var bytes = new byte[] { 0x38, 0x42, 0x50, 0x53 };
            var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
            const string resourcePath = "templates/fixture/template.psd";
            await files.WriteAtomicallyAsync(resourcePath, bytes, default);
            var inspection = Inspection(hash);
            await store.SaveImportedAsync("import", new("resource", groupId, session.DraftRevisionId,
                resourcePath, bytes.Length, hash, inspection.StructureSignature, inspection, DateTimeOffset.UnixEpoch), default);
            var page = TemplatePage.Create("page", session.DraftRevisionId, 0, "size table",
                TemplatePageContentType.NormalComposition, TemplateGenerationMode.OncePerProduct, null, true,
                "resource", typeRecognition: TemplatePageTypeRecognition.SizeTablePage);
            var binding = FrameBinding.Create("frame", page.PageId, LayerPath.Create([1]),
                ExpectedLayerEvidence.Create(1, "photo", true, false, false, false, 255, "norm",
                    LayerStructureSignature.Create(inspection.StructureSignature)), [],
                SelectionRequirement.Create("本款正面"), LayoutIntent.Create("subject_complete", 1, 0, 0,
                    TemplateRect.Create(0, 0, 790, 1000)));
            var draft = (await store.GetRevisionAsync(session.DraftRevisionId, default))!.AddPage(page).AddFrameBinding(binding);
            await store.SaveDraftRevisionAsync(draft, default);
            await workspace.ConfigurePageAsync(session, page.PageId, page.Name, TemplatePageContentTypeOption.DynamicSizeTable,
                TemplateGenerationModeOption.OncePerProduct, null, true, default);
            var layer = new TemplateSizeLayer(2, 2, "group", "old table", inspection.StructureSignature);
            var calibration = new TemplateSizeCalibration("size-table-template/v1", hash, "new table", layer, [layer],
                new(20, 200, 770, 900), new("Microsoft YaHei UI", 24, 1000, 56, 24, 12,
                    "#FFFFFFFF", "#FFF2F4F7", "#FF101828", "#FF98A2B3"));
            await workspace.CalibrateSizeTableAsync(session, page.PageId, calibration, default);
            await workspace.ConfigurePageAsync(session, page.PageId, page.Name, TemplatePageContentTypeOption.NormalComposition,
                TemplateGenerationModeOption.OncePerProduct, null, true, default);
            expectedBinding = JsonSerializer.Serialize(binding);
            expectedCalibration = JsonSerializer.Serialize(calibration);
            var committed = await workspace.CommitDraftAsync(session, default);
            revisionId = committed.RevisionId;
            var revision = (await store.GetRevisionAsync(revisionId, default))!;
            originalDigest = revision.RuleDigest!;
            AssertTracks(revision, TemplatePageContentType.NormalComposition);
            using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(paths.ResolveOwnedPath(revision.ManifestRef!)));
            Assert.Contains("size_" + page.PageId, manifest.RootElement.ToString(), StringComparison.Ordinal);
        }

        await using var reopened = new SqliteDesktopStore(options);
        await reopened.InitializeAsync();
        AssertTracks((await reopened.GetRevisionAsync(revisionId, default))!, TemplatePageContentType.NormalComposition);
        var editor = Workspace(reopened, paths, files);
        var port = new ZipTemplatePackagePort();
        var packages = new TemplatePackageService(reopened, reopened, port, files, paths, editor, TimeProvider.System);
        var packagePath = Path.Combine(directory.Path, "retained.tplpkg");
        await packages.ExportCurrentAsync(groupId, packagePath, default);
        var portable = (await port.ValidateAsync(packagePath, default)).Manifest!;
        Assert.Equal(TemplatePackageProtocol.PageTypeVersion, portable.Protocol);
        Assert.Equal(TemplatePageTypeRecognition.SizeTablePage, portable.Pages![0].TypeRecognition);
        var imported = await packages.ImportAsNewGroupAsync(packagePath, "imported", default);
        AssertTracks((await reopened.GetRevisionAsync(imported.RevisionId, default))!, TemplatePageContentType.NormalComposition);
        var savedAs = await packages.SaveAsNewGroupAsync(groupId, "copy", default);
        AssertTracks((await reopened.GetRevisionAsync(savedAs.RevisionId, default))!, TemplatePageContentType.NormalComposition);
        var edit = await editor.BeginFullEditAsync(groupId, default);
        await editor.ConfigurePageAsync(edit, "page", "size table", TemplatePageContentTypeOption.DynamicSizeTable,
            TemplateGenerationModeOption.OncePerProduct, null, true, default);
        var changed = await editor.CommitDraftAsync(edit, default);
        var sizeRevision = (await reopened.GetRevisionAsync(changed.RevisionId, default))!;
        AssertTracks(sizeRevision, TemplatePageContentType.DynamicSizeTable);
        Assert.Empty(BatchTemplateSnapshot.Create("snapshot", "batch", sizeRevision, ["page"], DateTimeOffset.UnixEpoch).Pages[0].FrameIds);
        var original = (await reopened.GetRevisionAsync(revisionId, default))!;
        Assert.Equal(originalDigest, original.RuleDigest);
        Assert.Equal(TemplatePageContentType.NormalComposition, original.Pages[0].ContentType);
        var originalBinding = Assert.Single(original.FrameBindings);
        var originalCalibration = original.Pages[0].SizeTableCalibration!;
        Assert.Equal(expectedBinding, JsonSerializer.Serialize(originalBinding));
        Assert.Equal(expectedCalibration, JsonSerializer.Serialize(originalCalibration));
        await AssertRoundTripCalibrationAsync(imported.GroupId, imported.RevisionId);
        await AssertRoundTripCalibrationAsync(savedAs.GroupId, savedAs.RevisionId);

        async Task AssertRoundTripCalibrationAsync(string importedGroupId, string importedRevisionId)
        {
            var importedRevision = (await reopened.GetRevisionAsync(importedRevisionId, default))!;
            Assert.Equal(JsonSerializer.Serialize(originalCalibration),
                JsonSerializer.Serialize(Assert.Single(importedRevision.Pages).SizeTableCalibration));
            AssertEquivalentBinding(originalBinding, Assert.Single(importedRevision.FrameBindings));

            var reexportedPath = Path.Combine(directory.Path, importedRevisionId + "-roundtrip.tplpkg");
            await packages.ExportCurrentAsync(importedGroupId, reexportedPath, default);
            var reexported = (await port.ValidateAsync(reexportedPath, default)).Manifest!;
            Assert.Equal(TemplatePackageProtocol.PageTypeVersion, reexported.Protocol);
            Assert.Equal(JsonSerializer.Serialize(originalCalibration),
                JsonSerializer.Serialize(Assert.Single(reexported.Pages!).SizeTableCalibration));
            Assert.Equal(TemplatePageTypeRecognition.SizeTablePage, reexported.Pages![0].TypeRecognition);
        }
    }

    [Fact]
    public async Task V29DatabaseMigrationAndLegacyTemplateReopenPreserveBothCalibrationTracks()
    {
        using var directory = new TestDirectory();
        var paths = new LocalAppPathPolicy(directory.Path);
        var files = new AtomicFileStore(paths);
        var options = new DesktopDataOptions(directory.Path);
        string groupId;
        string revisionId;
        string ruleDigest;
        string bindingJson;
        string calibrationJson;

        await using (var store = new SqliteDesktopStore(options))
        {
            await store.InitializeAsync();
            var workspace = Workspace(store, paths, files);
            var session = await workspace.BeginCreateAsync("legacy size template", default);
            groupId = session.GroupId;
            var bytes = new byte[] { 0x38, 0x42, 0x50, 0x53, 5, 6, 7, 8 };
            var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
            const string resourcePath = "templates/fixture/legacy-template.psd";
            await files.WriteAtomicallyAsync(resourcePath, bytes, default);
            var inspection = Inspection(hash);
            await store.SaveImportedAsync("legacy_import", new("legacy_resource", groupId, session.DraftRevisionId,
                resourcePath, bytes.Length, hash, inspection.StructureSignature, inspection, DateTimeOffset.UnixEpoch), default);

            var page = TemplatePage.Create("legacy_page", session.DraftRevisionId, 0, "legacy size page",
                TemplatePageContentType.NormalComposition, TemplateGenerationMode.OncePerProduct, null, true,
                "legacy_resource", typeRecognition: TemplatePageTypeRecognition.SizeTablePage);
            var binding = FrameBinding.Create("legacy_frame", page.PageId, LayerPath.Create([1]),
                ExpectedLayerEvidence.Create(1, "photo", true, false, false, false, 255, "norm",
                    LayerStructureSignature.Create(inspection.StructureSignature)), [],
                SelectionRequirement.Create("legacy ordinary image", [SelectionCriterion.Create("view", "front")]),
                LayoutIntent.Create("subject_complete", 1.15, 7, -5, TemplateRect.Create(12, 24, 780, 990)));
            var draft = (await store.GetRevisionAsync(session.DraftRevisionId, default))!
                .AddPage(page)
                .AddFrameBinding(binding);
            await store.SaveDraftRevisionAsync(draft, default);
            await workspace.ConfigurePageAsync(session, page.PageId, page.Name,
                TemplatePageContentTypeOption.DynamicSizeTable, TemplateGenerationModeOption.OncePerProduct,
                null, true, default);
            var layer = new TemplateSizeLayer(2, 2, "group", "old table", inspection.StructureSignature);
            var calibration = new TemplateSizeCalibration("size-table-template/v1", hash, "legacy table", layer,
                [layer], new(18, 190, 772, 910), new("Microsoft YaHei UI", 26, 980, 60, 22, 13,
                    "#FFFFFFFF", "#FFF2F4F7", "#FF101828", "#FF98A2B3"));
            await workspace.CalibrateSizeTableAsync(session, page.PageId, calibration, default);
            var committed = await workspace.CommitDraftAsync(session, default);
            revisionId = committed.RevisionId;
            var saved = (await store.GetRevisionAsync(revisionId, default))!;
            ruleDigest = saved.RuleDigest!;
            bindingJson = JsonSerializer.Serialize(Assert.Single(saved.FrameBindings));
            calibrationJson = JsonSerializer.Serialize(saved.Pages[0].SizeTableCalibration);
        }

        await using var legacyConnection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = options.DatabasePath,
            Pooling = false
        }.ToString());
        await legacyConnection.OpenAsync();
        await using (var command = legacyConnection.CreateCommand())
        {
            command.CommandText = "ALTER TABLE template_pages DROP COLUMN type_recognition; DELETE FROM schema_migrations WHERE version=29;";
            await command.ExecuteNonQueryAsync();
        }

        await using var migrated = new SqliteDesktopStore(options);
        await migrated.InitializeAsync();
        var originalRevision = (await migrated.GetRevisionAsync(revisionId, default))!;
        Assert.Equal(TemplateRevisionState.Committed, originalRevision.State);
        Assert.Equal(ruleDigest, originalRevision.RuleDigest);
        Assert.Equal(bindingJson, JsonSerializer.Serialize(Assert.Single(originalRevision.FrameBindings)));
        Assert.Equal(calibrationJson, JsonSerializer.Serialize(originalRevision.Pages[0].SizeTableCalibration));
        Assert.Equal(TemplatePageTypeRecognition.SizeTablePage, originalRevision.Pages[0].TypeRecognition);
        Assert.Equal(TemplatePageContentType.DynamicSizeTable, originalRevision.Pages[0].ContentType);

        // Exercise the same read/edit/save path used when reopening an old committed template.
        var reopenedWorkspace = Workspace(migrated, paths, files);
        var details = await reopenedWorkspace.GetCommittedDetailsAsync(groupId, default);
        Assert.NotNull(details);
        Assert.Equal(revisionId, details!.RevisionId);
        var edit = await reopenedWorkspace.BeginFullEditAsync(groupId, default);
        var reopenedDraft = (await migrated.GetRevisionAsync(edit.DraftRevisionId, default))!;
        Assert.Equal(bindingJson, JsonSerializer.Serialize(Assert.Single(reopenedDraft.FrameBindings)));
        Assert.Equal(calibrationJson, JsonSerializer.Serialize(reopenedDraft.Pages[0].SizeTableCalibration));
        var savedAgain = await reopenedWorkspace.CommitDraftAsync(edit, default);
        var reopenedAgain = (await migrated.GetRevisionAsync(savedAgain.RevisionId, default))!;
        Assert.Equal(bindingJson, JsonSerializer.Serialize(Assert.Single(reopenedAgain.FrameBindings)));
        Assert.Equal(calibrationJson, JsonSerializer.Serialize(reopenedAgain.Pages[0].SizeTableCalibration));
        Assert.Equal(TemplatePageTypeRecognition.SizeTablePage, reopenedAgain.Pages[0].TypeRecognition);
    }

    [Theory]
    [InlineData("template-package/v1")]
    [InlineData("template-package/v2")]
    [InlineData("template-package/v3")]
    [InlineData("template-package/v4")]
    [InlineData("template-package/v5")]
    public async Task FrozenPackagesRejectNewRecognitionMetadata(string version)
    {
        using var directory = new TestDirectory();
        var inspection = Inspection(new string('a', 64));
        var page = new TemplatePackagePage("page", 0, "size", TemplatePageContentTypeOption.DynamicSizeTable,
            TemplateGenerationModeOption.OncePerProduct, null, true, "resource", inspection,
            TypeRecognition: TemplatePageTypeRecognition.SizeTablePage);
        var manifest = new TemplatePackageManifest(version, "group", 1, "revision", "sha256:" + new string('b', 64),
            [new("resource", "resource.psd", 4, inspection.FileSha256)], [page]);
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => new ZipTemplatePackagePort().ExportAsync(manifest,
            new Dictionary<string, string>(), Path.Combine(directory.Path, "reject.tplpkg"), default));
        Assert.Equal("TPL_PACKAGE_INVALID", error.Message);
    }

    [Fact]
    public async Task V28MigrationRecordsOriginalTypeWithoutChangingHistoricalDigestAndRollsBackOnFailure()
    {
        using var directory = new TestDirectory();
        var options = new DesktopDataOptions(directory.Path);
        await using (var store = new SqliteDesktopStore(options))
        {
            await store.InitializeAsync();
            await store.SaveGroupAsync(TemplateGroup.Create("group", "legacy", DateTimeOffset.UnixEpoch), default);
            var pages = new[] { TemplatePageContentType.NormalComposition, TemplatePageContentType.DynamicSizeTable }
                .Select((type, index) => TemplatePage.Create("page_" + index, "revision", index, "legacy", type,
                    TemplateGenerationMode.OncePerProduct, null, true, "resource"));
            await store.SaveDraftRevisionAsync(TemplateRevision.CreateDraft("revision", "group", 1, DateTimeOffset.UnixEpoch, pages), default);
        }
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = options.DatabasePath, Pooling = false }.ToString());
        await connection.OpenAsync();
        async Task Execute(string sql)
        {
            await using var command = connection.CreateCommand(); command.CommandText = sql; await command.ExecuteNonQueryAsync();
        }
        async Task<string?> Scalar(string sql)
        {
            await using var command = connection.CreateCommand(); command.CommandText = sql; return (await command.ExecuteScalarAsync())?.ToString();
        }
        await Execute("ALTER TABLE template_pages DROP COLUMN type_recognition; DELETE FROM schema_migrations WHERE version=29;");
        var history = await Scalar("SELECT group_concat(version||checksum||applied_at,'|') FROM (SELECT * FROM schema_migrations ORDER BY version);");
        await Execute("CREATE TRIGGER fail_v29 BEFORE INSERT ON schema_migrations WHEN NEW.version=29 BEGIN SELECT RAISE(ABORT,'TEST_V29_FAILURE'); END;");
        await using (var failing = new SqliteDesktopStore(options))
            await Assert.ThrowsAsync<SqliteException>(() => failing.InitializeAsync());
        Assert.Equal("0", await Scalar("SELECT count(*) FROM pragma_table_info('template_pages') WHERE name='type_recognition';"));
        await Execute("DROP TRIGGER fail_v29;");
        await using var upgraded = new SqliteDesktopStore(options);
        await upgraded.InitializeAsync();
        var revision = (await upgraded.GetRevisionAsync("revision", default))!;
        Assert.Equal(TemplatePageTypeRecognition.OrdinaryPage, revision.Pages[0].TypeRecognition);
        Assert.Equal(TemplatePageTypeRecognition.SizeTablePage, revision.Pages[1].TypeRecognition);
        Assert.Equal(history, await Scalar("SELECT group_concat(version||checksum||applied_at,'|') FROM (SELECT * FROM schema_migrations WHERE version<=28 ORDER BY version);"));
        Assert.Equal("ok", await Scalar("PRAGMA integrity_check;"));
    }

    private static void AssertTracks(TemplateRevision revision, TemplatePageContentType type)
    {
        var page = Assert.Single(revision.Pages);
        Assert.Equal(type, page.ContentType);
        Assert.Equal(TemplatePageTypeRecognition.SizeTablePage, page.TypeRecognition);
        Assert.Equal("new table", page.SizeTableCalibration!.LayerName);
        Assert.Equal("本款正面", Assert.Single(revision.FrameBindings).SelectionRequirement.Description);
        Assert.True(SizeTableCalibrationResolver.Matches(page.SizeTableCalibration, Inspection(page.SizeTableCalibration.TemplateSha256)));
    }

    private static void AssertEquivalentBinding(FrameBinding expected, FrameBinding actual)
    {
        Assert.Equal(JsonSerializer.Serialize(expected.LayerPath.Segments),
            JsonSerializer.Serialize(actual.LayerPath.Segments));
        Assert.Equal(JsonSerializer.Serialize(expected.ExpectedLayer), JsonSerializer.Serialize(actual.ExpectedLayer));
        Assert.Equal(JsonSerializer.Serialize(expected.SupersededLayers), JsonSerializer.Serialize(actual.SupersededLayers));
        Assert.Equal(JsonSerializer.Serialize(expected.SelectionRequirement),
            JsonSerializer.Serialize(actual.SelectionRequirement));
        Assert.Equal(JsonSerializer.Serialize(expected.LayoutIntent), JsonSerializer.Serialize(actual.LayoutIntent));
    }

    private static TemplateWorkspaceService Workspace(SqliteDesktopStore store, LocalAppPathPolicy paths, AtomicFileStore files) =>
        new(store, store, null!, TimeProvider.System, new JsonManifestStore(files), paths: paths, files: files);

    private static PsdInspectionDocument Inspection(string hash) => new(hash, 4, new(790, 1000, 8, "RGB"),
        2, 1, 0, 0, 0, "sha256:" + new string('b', 64), new(1, 1, 2), 1024, null,
        [new([0], 1, "photo", "smartobject", false, true, true, false, 255, 255, "norm", new(0, 0, 790, 1000), false, false, null,
            new("photo", "photo.jpg", "data", "jpg", "jpg", 4, false, [0, 0, 790, 0, 790, 1000, 0, 1000])),
         new([1], 2, "old table", "group", true, true, true, false, 255, 255, "norm", new(20, 200, 770, 900), false, false, null, null)]);
}
