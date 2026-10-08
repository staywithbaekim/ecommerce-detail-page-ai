using Desktop.Domain.Templates;
using Desktop.Application.Psd;
using Desktop.Application.Templates;
using Desktop.Infrastructure.Persistence;
using Desktop.Infrastructure.Files;
using Desktop.Infrastructure.Templates;

namespace Desktop.Infrastructure.Tests;

public sealed class TemplateRepositoryTests
{
    [Fact]
    public async Task ExactLayerNameWhitespaceSurvivesCommittedRevisionReopen()
    {
        using var directory = new TestDirectory();
        var options = new DesktopDataOptions(directory.Path);
        var now = DateTimeOffset.UtcNow;
        var page = Page("page_name", "revision_name", 0);
        var binding = FrameBinding.Create("frame_name", page.PageId, LayerPath.Create([2]),
            ExpectedLayerEvidence.Create(257, "\tfabric_detail ", true, false, false, false, 255, "normal",
                LayerStructureSignature.Create(RuleDigest)), [], SelectionRequirement.Create("面料近景"),
            LayoutIntent.Create("detail_crop", 1, 0, 0, TemplateRect.Create(0, 0, 790, 1252)));
        var draft = TemplateRevision.CreateDraft(page.RevisionId, "group_name", 1, now, [page], [binding]);
        await using (var store = new SqliteDesktopStore(options))
        {
            await store.InitializeAsync();
            await store.SaveGroupAsync(TemplateGroup.Create(draft.GroupId, "layer names", now), default);
            await store.SaveDraftRevisionAsync(draft, default);
            await store.CommitRevisionAsync(Commit(draft), null, default);
        }
        await using var reopened = new SqliteDesktopStore(options);
        await reopened.InitializeAsync();
        Assert.Equal("\tfabric_detail ", (await reopened.GetRevisionAsync(page.RevisionId, default))!.FrameBindings.Single().ExpectedLayer.NameBefore);
    }

    [Fact]
    public async Task SizeCalibrationSurvivesRestartAndCommittedRulesCannotBeReplaced()
    {
        using var directory = new TestDirectory();
        var options = new DesktopDataOptions(directory.Path);
        var now = DateTimeOffset.UtcNow;
        var group = TemplateGroup.Create("group_size", "sizes", now);
        var layer = new SizeTableTemplateLayer(1, 257, "group", "old table", "sha256:" + new string('b', 64));
        var calibration = new SizeTableTemplateCalibration("size-table-template/v1", new string('a', 64), "new table", layer,
            [layer], TemplateRect.Create(10, 20, 700, 1000),
            new("Microsoft YaHei UI", 24, 1000, 56, 24, 12, "#FFFFFFFF", "#FFF2F4F7", "#FF101828", "#FF98A2B3"));
        var page = TemplatePage.Create("page_size", "revision_size", 0, "sizes", TemplatePageContentType.DynamicSizeTable,
            TemplateGenerationMode.OncePerProduct, null, true, "resource_size", sizeTableCalibration: calibration);
        var draft = TemplateRevision.CreateDraft(page.RevisionId, group.GroupId, 1, now, [page]);
        await using (var store = new SqliteDesktopStore(options))
        {
            await store.InitializeAsync();
            await store.SaveGroupAsync(group, default);
            await store.SaveDraftRevisionAsync(draft, default);
            await store.CommitRevisionAsync(Commit(draft), null, default);
        }
        await using var reopened = new SqliteDesktopStore(options);
        await reopened.InitializeAsync();
        var revision = (await reopened.GetRevisionAsync(page.RevisionId, default))!;
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(calibration),
            System.Text.Json.JsonSerializer.Serialize(revision.Pages[0].SizeTableCalibration));
        var changed = draft.CalibrateSizeTable(page.PageId, calibration with { LayerName = "changed" });
        var error = await Assert.ThrowsAsync<TemplateRuleViolationException>(() => reopened.CommitRevisionAsync(Commit(changed), null, default));
        Assert.Equal("TPL_REVISION_IMMUTABLE", error.Message);
        Assert.Equal("new table", (await reopened.GetRevisionAsync(page.RevisionId, default))!.Pages[0].SizeTableCalibration!.LayerName);
    }

    private const string RuleDigest =
        "sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    [Fact]
    public async Task CommitsAndReopensTemplateRevisionThroughSingleStore()
    {
        using var directory = new TestDirectory();
        var options = new DesktopDataOptions(directory.Path);
        var now = DateTimeOffset.UtcNow;
        var group = TemplateGroup.Create("group_01", "Template", now);
        var draft = TemplateRevision.CreateDraft(
            "revision_01",
            group.GroupId,
            1,
            now,
            [Page("page_01", "revision_01", 0)],
            [Binding("frame_01", "page_01")]);
        var committed = Commit(draft);

        await using (var store = new SqliteDesktopStore(options))
        {
            await store.InitializeAsync();
            await store.SaveGroupAsync(group, CancellationToken.None);
            await store.SaveDraftRevisionAsync(draft, CancellationToken.None);
            await store.CommitRevisionAsync(committed, null, CancellationToken.None);
            await store.CommitRevisionAsync(committed, null, CancellationToken.None);
        }

        await using var reopened = new SqliteDesktopStore(options);
        await reopened.InitializeAsync();
        var loadedGroup = await reopened.GetGroupAsync(group.GroupId, CancellationToken.None);
        var loadedRevision = await reopened.GetRevisionAsync(committed.RevisionId, CancellationToken.None);

        Assert.Equal(committed.RevisionId, loadedGroup!.CurrentRevisionId);
        Assert.Equal(TemplateRevisionState.Committed, loadedRevision!.State);
        Assert.Equal(RuleDigest, loadedRevision.RuleDigest);
        Assert.Equal("page_01", Assert.Single(loadedRevision.Pages).PageId);
        Assert.Equal("frame_01", Assert.Single(loadedRevision.FrameBindings).FrameId);
        Assert.Equal(2, Assert.Single(loadedRevision.FrameBindings[0].LayerPath.Segments));
    }

    [Fact]
    public async Task LatestDraftPageConfigurationAndOrderSurviveRestart()
    {
        using var directory = new TestDirectory();
        var options = new DesktopDataOptions(directory.Path);
        var now = new DateTimeOffset(2026, 8, 28, 1, 0, 0, TimeSpan.Zero);
        var group = TemplateGroup.Create("group_draft", "中文模板组", now);
        var draft = TemplateRevision.CreateDraft(
            "revision_draft",
            group.GroupId,
            1,
            now,
            [
                Page("page_front", "revision_draft", 0),
                Page("page_detail", "revision_draft", 1)
            ]);
        var configured = draft
            .ConfigurePage(
                "page_detail",
                "细节组合页",
                TemplatePageContentType.NormalComposition,
                TemplateGenerationMode.VariantCapacityCombination,
                4,
                enabled: true)
            .ReorderPages(["page_detail", "page_front"]);

        await using (var store = new SqliteDesktopStore(options))
        {
            await store.InitializeAsync();
            await store.SaveGroupAsync(group, CancellationToken.None);
            await store.SaveDraftRevisionAsync(configured, CancellationToken.None);
        }

        await using var reopened = new SqliteDesktopStore(options);
        await reopened.InitializeAsync();
        var groups = await reopened.ListGroupsAsync(CancellationToken.None);
        var restored = await reopened.GetLatestRevisionForGroupAsync(group.GroupId, CancellationToken.None);

        Assert.Equal("中文模板组", Assert.Single(groups).Name);
        Assert.NotNull(restored);
        Assert.Equal(["page_detail", "page_front"], restored.Pages.Select(page => page.PageId));
        Assert.Equal("细节组合页", restored.Pages[0].Name);
        Assert.Equal(4, restored.Pages[0].VariantCapacity);
    }

    [Fact]
    public async Task RenamedTemplateGroupSurvivesRestartWithoutChangingRevisionPointer()
    {
        using var directory = new TestDirectory();
        var options = new DesktopDataOptions(directory.Path);
        var now = new DateTimeOffset(2026, 9, 12, 1, 0, 0, TimeSpan.Zero);
        var group = TemplateGroup.Create("group_rename", "原名称", now);
        var committed = Commit(TemplateRevision.CreateDraft(
            "revision_rename",
            group.GroupId,
            1,
            now,
            [Page("page_rename", "revision_rename", 0)]));

        await using (var store = new SqliteDesktopStore(options))
        {
            await store.InitializeAsync();
            await store.SaveGroupAsync(group, CancellationToken.None);
            await store.CommitRevisionAsync(committed, null, CancellationToken.None);
            var workspace = new TemplateWorkspaceService(
                store,
                store,
                importer: null!,
                timeProvider: TimeProvider.System);
            await workspace.RenameTemplateGroupAsync(
                group.GroupId,
                "已重命名模板",
                CancellationToken.None);
        }

        await using var reopened = new SqliteDesktopStore(options);
        await reopened.InitializeAsync();
        var restored = await reopened.GetGroupAsync(group.GroupId, CancellationToken.None);

        Assert.Equal("已重命名模板", restored!.Name);
        Assert.Equal(committed.RevisionId, restored.CurrentRevisionId);
        Assert.NotNull(await reopened.GetRevisionAsync(committed.RevisionId, CancellationToken.None));
    }

    [Fact]
    public async Task PageEditReusesExistingSqliteDraftAndLegacyCalibrationMarker()
    {
        using var directory = new TestDirectory();
        var options = new DesktopDataOptions(directory.Path);
        var now = new DateTimeOffset(2026, 9, 7, 1, 0, 0, TimeSpan.Zero);
        var group = TemplateGroup.Create("group_edit_recovery", "页面编辑恢复", now);
        var committed = Commit(TemplateRevision.CreateDraft(
            "revision_edit_recovery_base",
            group.GroupId,
            1,
            now,
            [Page("page_detail", "revision_edit_recovery_base", 0)]));
        var draft = TemplateRevision.CreateIncrement(
            "revision_edit_recovery_draft",
            committed,
            2,
            now.AddMinutes(1),
            []);

        await using var store = new SqliteDesktopStore(options);
        await store.InitializeAsync();
        await store.SaveGroupAsync(group, CancellationToken.None);
        await store.CommitRevisionAsync(committed, null, CancellationToken.None);
        await store.SaveDraftRevisionAsync(draft, CancellationToken.None);
        await store.SaveCalibrationDraftAsync(
            CalibrationDraft.Create(
                $"calibration_{committed.RevisionId}_page_detail",
                committed.RevisionId,
                ["page_detail"],
                TemplateRevisionState.Calibrating,
                1),
            CancellationToken.None);

        var workspace = new TemplateWorkspaceService(
            store,
            store,
            importer: null!,
            timeProvider: TimeProvider.System);
        var resumed = await workspace.BeginPageEditAsync(
            group.GroupId,
            "page_detail",
            CancellationToken.None);

        Assert.Equal(draft.RevisionId, resumed.DraftRevisionId);
        Assert.Equal(2, resumed.RevisionNo);
        Assert.Contains("page_detail", resumed.EditablePageIds);
        Assert.Equal(draft.RevisionId, (await store.GetLatestRevisionForGroupAsync(
            group.GroupId,
            CancellationToken.None))!.RevisionId);
    }

    [Fact]
    public async Task FullEditCanBeSavedAsIndependentGroupWithoutChangingOriginalRevisionOrPsd()
    {
        using var directory = new TestDirectory();
        var options = new DesktopDataOptions(directory.Path);
        var now = new DateTimeOffset(2026, 9, 7, 2, 30, 0, TimeSpan.Zero);
        var psdBytes = new byte[] { 0x38, 0x42, 0x50, 0x53, 0x01, 0x02, 0x03 };
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(psdBytes)).ToLowerInvariant();
        var group = TemplateGroup.Create("group_full_clone", "原模板组", now);
        var resourcePath =
            "templates/groups/group_full_clone/revisions/revision_full_clone_base/template.psd";
        var resource = new ImportedTemplateResource(
            "resource_full_clone",
            group.GroupId,
            "revision_full_clone_base",
            resourcePath,
            psdBytes.Length,
            hash,
            "sha256:" + new string('b', 64),
            Inspection(),
            now);
        var draft = TemplateRevision.CreateDraft(
            "revision_full_clone_base",
            group.GroupId,
            1,
            now,
            [TemplatePage.Create(
                "page_full_clone",
                "revision_full_clone_base",
                0,
                "主图页",
                TemplatePageContentType.NormalComposition,
                TemplateGenerationMode.OncePerProduct,
                null,
                enabled: true,
                resource.ResourceId)],
            [Binding("frame_full_clone", "page_full_clone").Reconfigure(
                SelectionRequirement.Create(
                    "Choose the main product image.",
                    [SelectionCriterion.Create("asset_id", "asset_front")]))]);

        await using var store = new SqliteDesktopStore(options);
        await store.InitializeAsync();
        var paths = new LocalAppPathPolicy(directory.Path);
        var files = new AtomicFileStore(paths);
        await files.WriteAtomicallyAsync(resourcePath, psdBytes, CancellationToken.None);
        await store.SaveGroupAsync(group, CancellationToken.None);
        await store.SaveImportedAsync("import_full_clone", resource, CancellationToken.None);
        await store.CommitRevisionAsync(Commit(draft), null, CancellationToken.None);

        var workspace = new TemplateWorkspaceService(
            store,
            store,
            importer: null!,
            TimeProvider.System,
            new JsonManifestStore(files),
            new TemplateOwnedStorage(paths),
            paths,
            new TemplateSourceFilePort(paths),
            files);

        var session = await workspace.BeginFullEditAsync(group.GroupId, CancellationToken.None);
        var savedAs = await workspace.CommitDraftAsNewGroupAsync(
            session,
            "原模板组 副本",
            CancellationToken.None);

        var originalGroup = await store.GetGroupAsync(group.GroupId, CancellationToken.None);
        var originalRevision = await store.GetRevisionAsync(
            originalGroup!.CurrentRevisionId!,
            CancellationToken.None);
        var clonedRevision = await store.GetRevisionAsync(savedAs.RevisionId, CancellationToken.None);
        var clonedPage = Assert.Single(clonedRevision!.Pages);
        var clonedResource = await store.GetByResourceIdAsync(
            clonedPage.TemplateResourceId,
            CancellationToken.None);
        var clonedBytes = await files.ReadAsync(
            clonedResource!.InternalRelativePath,
            CancellationToken.None);

        Assert.NotEqual(group.GroupId, savedAs.GroupId);
        Assert.Equal("原模板组", originalGroup.Name);
        Assert.Equal(draft.RevisionId, originalGroup.CurrentRevisionId);
        Assert.Equal("page_full_clone", Assert.Single(originalRevision!.Pages).PageId);
        Assert.Equal("原模板组 副本", savedAs.Name);
        Assert.NotEqual("page_full_clone", clonedPage.PageId);
        Assert.NotEqual(resource.ResourceId, clonedPage.TemplateResourceId);
        Assert.NotEqual(resource.InternalRelativePath, clonedResource.InternalRelativePath);
        Assert.Equal(psdBytes, clonedBytes);
        Assert.Equal(
            "asset_front",
            Assert.Single(clonedRevision.FrameBindings).SelectionRequirement.Criteria.Single().Value);
    }

    [Fact]
    public async Task CommittedRevisionCannotBeOverwrittenByDraft()
    {
        using var directory = new TestDirectory();
        await using var store = new SqliteDesktopStore(new DesktopDataOptions(directory.Path));
        await store.InitializeAsync();
        var group = TemplateGroup.Create("group_01", "Template", DateTimeOffset.UtcNow);
        var draft = TemplateRevision.CreateDraft(
            "revision_01",
            group.GroupId,
            1,
            DateTimeOffset.UtcNow,
            [Page("page_01", "revision_01", 0)]);
        await store.SaveGroupAsync(group, CancellationToken.None);
        await store.SaveDraftRevisionAsync(draft, CancellationToken.None);
        await store.CommitRevisionAsync(Commit(draft), null, CancellationToken.None);

        var exception = await Assert.ThrowsAsync<TemplateRuleViolationException>(() =>
            store.SaveDraftRevisionAsync(draft, CancellationToken.None));

        Assert.Equal("TPL_REVISION_IMMUTABLE", exception.Code);
    }

    [Fact]
    public async Task IdempotentCommitRejectsChangedPageGraph()
    {
        using var directory = new TestDirectory();
        await using var store = new SqliteDesktopStore(new DesktopDataOptions(directory.Path));
        await store.InitializeAsync();
        var group = TemplateGroup.Create("group_01", "Template", DateTimeOffset.UtcNow);
        var committed = Commit(TemplateRevision.CreateDraft(
            "revision_01",
            group.GroupId,
            1,
            DateTimeOffset.UtcNow,
            [Page("page_01", "revision_01", 0)]));
        await store.SaveGroupAsync(group, CancellationToken.None);
        await store.CommitRevisionAsync(committed, null, CancellationToken.None);
        var changedGraph = TemplateRevision.Restore(
            committed.RevisionId,
            committed.GroupId,
            committed.RevisionNo,
            TemplateRevisionState.Committed,
            committed.ManifestRef,
            committed.RuleDigest,
            committed.CreatedAt,
            [Page("page_02", "revision_01", 0)],
            []);

        var exception = await Assert.ThrowsAsync<TemplateRuleViolationException>(() =>
            store.CommitRevisionAsync(changedGraph, null, CancellationToken.None));

        Assert.Equal("TPL_REVISION_IMMUTABLE", exception.Code);
    }

    [Fact]
    public async Task CalibrationDraftAndBatchSnapshotSurviveRestartAndSnapshotIsFrozen()
    {
        using var directory = new TestDirectory();
        var options = new DesktopDataOptions(directory.Path);
        var now = DateTimeOffset.UtcNow;
        var group = TemplateGroup.Create("group_01", "Template", now);
        var committed = Commit(TemplateRevision.CreateDraft(
            "revision_01",
            group.GroupId,
            1,
            now,
            [Page("page_01", "revision_01", 0)]));
        var draft = CalibrationDraft.Create(
            "draft_01",
            committed.RevisionId,
            ["page_02"],
            TemplateRevisionState.Calibrating,
            12);
        var snapshot = BatchTemplateSnapshot.Create(
            "snapshot_01",
            "batch_01",
            committed,
            ["page_01"],
            now);

        await using (var store = new SqliteDesktopStore(options))
        {
            await store.InitializeAsync();
            await store.SaveGroupAsync(group, CancellationToken.None);
            await store.CommitRevisionAsync(committed, null, CancellationToken.None);
            await store.SaveCalibrationDraftAsync(draft, CancellationToken.None);
            await store.SaveAsync(snapshot, CancellationToken.None);
        }

        await using var reopened = new SqliteDesktopStore(options);
        await reopened.InitializeAsync();
        var loadedDraft = await reopened.GetCalibrationDraftAsync(draft.DraftId, CancellationToken.None);
        var loadedSnapshot = await reopened.GetForBatchAsync(snapshot.BatchId, CancellationToken.None);
        var conflictingSnapshot = BatchTemplateSnapshot.Restore(
            "snapshot_02",
            snapshot.BatchId,
            snapshot.RevisionId,
            snapshot.Pages,
            snapshot.RulesDigest,
            snapshot.CreatedAt);
        var conflict = await Assert.ThrowsAsync<TemplateRuleViolationException>(() =>
            reopened.SaveAsync(conflictingSnapshot, CancellationToken.None));

        Assert.Equal(12, loadedDraft!.LastEventSequence);
        Assert.Equal("page_02", Assert.Single(loadedDraft.AffectedPageIds));
        Assert.Equal(snapshot.SnapshotId, loadedSnapshot!.SnapshotId);
        Assert.Equal("TPL_BATCH_SNAPSHOT_FROZEN", conflict.Code);
    }

    [Fact]
    public async Task CalibrationBindingGeometryAndPreviewStateSurviveServiceRestart()
    {
        using var directory = new TestDirectory();
        var options = new DesktopDataOptions(directory.Path);
        var now = DateTimeOffset.UtcNow;
        var group = TemplateGroup.Create("group_calibration_restart", "校准恢复", now);
        var revision = TemplateRevision.CreateDraft(
            "revision_calibration_restart",
            group.GroupId,
            1,
            now,
            [Page("page_calibration_restart", "revision_calibration_restart", 0)]);
        var resource = new ImportedTemplateResource(
            "resource_page_calibration_restart",
            group.GroupId,
            revision.RevisionId,
            "templates/groups/group_calibration_restart/drafts/revision_calibration_restart/staging/resource_page_calibration_restart/template.psd",
            4,
            new string('a', 64),
            "sha256:" + new string('b', 64),
            Inspection(),
            now);
        var session = new TemplateDraftSession(
            group.GroupId,
            group.Name,
            revision.RevisionId,
            1,
            new HashSet<string>(StringComparer.Ordinal));

        await using (var store = new SqliteDesktopStore(options))
        {
            await store.InitializeAsync();
            await store.SaveGroupAsync(group, CancellationToken.None);
            await store.SaveDraftRevisionAsync(revision, CancellationToken.None);
            await store.SaveImportedAsync("import_calibration_restart", resource, CancellationToken.None);
            var service = new TemplateCalibrationService(store, store, store);
            var initial = await service.LoadAsync(session, "page_calibration_restart", CancellationToken.None);
            var frameId = Assert.Single(initial.Frames).FrameId;
            await service.BindAssetAsync(
                session,
                "page_calibration_restart",
                frameId,
                "asset_restart",
                CancellationToken.None);
            await service.SetGeometryAsync(
                session,
                "page_calibration_restart",
                frameId,
                1.25,
                14,
                -6,
                CancellationToken.None);
            await store.SavePreviewStateAsync(
                TemplatePreviewState.NotPreviewed(
                    revision.RevisionId,
                    "page_calibration_restart",
                    now),
                CancellationToken.None);
        }

        await using var reopened = new SqliteDesktopStore(options);
        await reopened.InitializeAsync();
        var restored = await new TemplateCalibrationService(reopened, reopened, reopened).LoadAsync(
            session,
            "page_calibration_restart",
            CancellationToken.None);
        var preview = await reopened.GetPreviewStateAsync(
            revision.RevisionId,
            "page_calibration_restart",
            CancellationToken.None);
        var restoredRevision = await reopened.GetRevisionAsync(revision.RevisionId, CancellationToken.None);

        Assert.Equal("asset_restart", restored.SelectedAssetId);
        Assert.Equal(1.25, restored.SelectedScale);
        Assert.Equal(14, restored.SelectedOffsetX);
        Assert.Equal(-6, restored.SelectedOffsetY);
        Assert.Equal(TemplatePreviewStatus.NotPreviewed, preview!.Status);
        Assert.Equal(1.25, Assert.Single(restoredRevision!.FrameBindings).LayoutIntent.Scale);
    }

    [Fact]
    public async Task PageEditMigratesLegacyCalibrationStateAndRestoresLearningRule()
    {
        using var directory = new TestDirectory();
        var options = new DesktopDataOptions(directory.Path);
        var now = DateTimeOffset.UtcNow;
        var pageId = "page_legacy_calibration";
        var group = TemplateGroup.Create("group_legacy_calibration", "旧校准恢复", now);
        var committedDraft = TemplateRevision.CreateDraft(
            "revision_legacy_committed",
            group.GroupId,
            1,
            now,
            [Page(pageId, "revision_legacy_committed", 0)],
            [Binding("learn_frame_legacy", pageId).Reconfigure(
                SelectionRequirement.Create(
                    "商品展示图，与参考款保持同一正面视角。",
                    [
                        SelectionCriterion.Create("asset_id", "asset_reference"),
                        SelectionCriterion.Create("reference_only", "true"),
                        SelectionCriterion.Create("reference_file_name", "front.jpg")
                    ]),
                LayoutIntent.Create(
                    "subject_cover",
                    1.25,
                    14,
                    -6,
                    TemplateRect.Create(0, 0, 790, 1252))) ]);
        var committed = Commit(committedDraft);
        var draft = TemplateRevision.CreateIncrement(
            "revision_legacy_edit",
            committed,
            2,
            now,
            []);
        var resource = new ImportedTemplateResource(
            $"resource_{pageId}",
            group.GroupId,
            committed.RevisionId,
            $"templates/groups/{group.GroupId}/drafts/{committed.RevisionId}/staging/resource_{pageId}/template.psd",
            4,
            new string('a', 64),
            "sha256:" + new string('b', 64),
            Inspection(),
            now);
        var legacyDraftId = $"calibration_{committed.RevisionId}_{pageId}";
        var legacySnapshot = new CalibrationStateSnapshot(
            legacyDraftId,
            pageId,
            3,
            "asset_reference",
            "frame_257",
            [new TemplateCalibrationAsset("asset_reference", "front.jpg", "", 790, 1252)],
            [new CalibrationFrameState("frame_257", "asset_reference", 1.25, 14, -6)]);
        var session = new TemplateDraftSession(
            group.GroupId,
            group.Name,
            draft.RevisionId,
            draft.RevisionNo,
            new HashSet<string>(StringComparer.Ordinal) { pageId })
        {
            EditablePageIds = new HashSet<string>(StringComparer.Ordinal) { pageId }
        };

        await using var store = new SqliteDesktopStore(options);
        await store.InitializeAsync();
        await store.SaveGroupAsync(group, CancellationToken.None);
        await store.SaveDraftRevisionAsync(committedDraft, CancellationToken.None);
        await store.CommitRevisionAsync(committed, null, CancellationToken.None);
        await store.SaveDraftRevisionAsync(draft, CancellationToken.None);
        await store.SaveImportedAsync("import_legacy_calibration", resource, CancellationToken.None);
        await store.SaveCalibrationDraftAsync(
            CalibrationDraft.Create(
                legacyDraftId,
                committed.RevisionId,
                [pageId],
                TemplateRevisionState.Calibrating,
                legacySnapshot.Sequence),
            CancellationToken.None);
        await store.SaveCalibrationStateAsync(legacySnapshot, CancellationToken.None);

        var service = new TemplateCalibrationService(store, store, store);
        var restored = await service.LoadAsync(session, pageId, CancellationToken.None);
        var migrated = await store.GetCalibrationStateAsync(
            $"calibration_{draft.RevisionId}_{pageId}",
            pageId,
            CancellationToken.None);

        Assert.Equal("frame_257", restored.SelectedFrameId);
        Assert.Equal("asset_reference", restored.SelectedAssetId);
        Assert.Equal("商品展示图，与参考款保持同一正面视角。", restored.SelectionRequirement);
        Assert.Equal(1.25, restored.SelectedScale);
        Assert.Equal(14, restored.SelectedOffsetX);
        Assert.Equal(-6, restored.SelectedOffsetY);
        Assert.NotNull(migrated);
    }

    [Fact]
    public async Task FivePageSnapshotRemainsIsolatedAfterSixthPageCommitAndRestart()
    {
        using var directory = new TestDirectory();
        var options = new DesktopDataOptions(directory.Path);
        var now = DateTimeOffset.UtcNow;
        var group = TemplateGroup.Create("group_five_plus_one", "五加一", now);
        var fiveDraft = TemplateRevision.CreateDraft(
            "revision_five_pages",
            group.GroupId,
            1,
            now,
            Enumerable.Range(1, 5).Select(index => Page(
                $"page_{index}",
                "revision_five_pages",
                index - 1)));
        var fiveCommitted = Commit(fiveDraft);
        var oldSnapshot = BatchTemplateSnapshot.Create(
            "snapshot_old_five",
            "batch_old_five",
            fiveCommitted,
            fiveCommitted.Pages.Select(page => page.PageId),
            now);
        var sixDraft = TemplateRevision.CreateIncrement(
            "revision_six_pages",
            fiveCommitted,
            2,
            now.AddMinutes(1),
            [Page("page_6", "revision_six_pages", 5)]);
        var sixCommitted = Commit(sixDraft);
        var newSnapshot = BatchTemplateSnapshot.Create(
            "snapshot_new_six",
            "batch_new_six",
            sixCommitted,
            sixCommitted.Pages.Select(page => page.PageId),
            now.AddMinutes(1));

        await using (var store = new SqliteDesktopStore(options))
        {
            await store.InitializeAsync();
            await store.SaveGroupAsync(group, CancellationToken.None);
            await store.CommitRevisionAsync(fiveCommitted, null, CancellationToken.None);
            await store.SaveAsync(oldSnapshot, CancellationToken.None);
            await store.CommitRevisionAsync(sixCommitted, fiveCommitted.RevisionId, CancellationToken.None);
            await store.SaveAsync(newSnapshot, CancellationToken.None);
        }

        await using var reopened = new SqliteDesktopStore(options);
        await reopened.InitializeAsync();
        var restoredOld = await reopened.GetForBatchAsync("batch_old_five", CancellationToken.None);
        var restoredNew = await reopened.GetForBatchAsync("batch_new_six", CancellationToken.None);
        var restoredFive = await reopened.GetRevisionAsync(fiveCommitted.RevisionId, CancellationToken.None);
        var restoredSix = await reopened.GetRevisionAsync(sixCommitted.RevisionId, CancellationToken.None);
        var blockers = await reopened.GetBlockingBatchIdsAsync(group.GroupId, CancellationToken.None);
        var deletion = await Assert.ThrowsAsync<TemplateRuleViolationException>(() =>
            reopened.DeleteGroupAsync(group.GroupId, CancellationToken.None));

        Assert.Equal(5, restoredOld!.Pages.Length);
        Assert.Equal(6, restoredNew!.Pages.Length);
        Assert.DoesNotContain("page_6", restoredOld.Pages.Select(page => page.PageId));
        Assert.Contains("page_6", restoredNew.Pages.Select(page => page.PageId));
        Assert.Equal(
            restoredFive!.Pages.Select(page => (page.PageId, page.TemplateResourceId)),
            restoredSix!.Pages.Take(5).Select(page => (page.PageId, page.TemplateResourceId)));
        Assert.Equal(["batch_new_six", "batch_old_five"], blockers);
        Assert.Equal("TPL_DELETE_BLOCKED_BY_ACTIVE_TASK", deletion.Code);
    }

    [Fact]
    public async Task DeletingUnreferencedGroupRemovesOnlyOwnedTemplateFiles()
    {
        using var directory = new TestDirectory();
        using var sourceDirectory = new TestDirectory();
        var sourcePath = Path.Combine(sourceDirectory.Path, "source.psd");
        var sourceBytes = new byte[] { 1, 2, 3, 4 };
        await File.WriteAllBytesAsync(sourcePath, sourceBytes);
        var sourceHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(sourceBytes));
        var paths = new LocalAppPathPolicy(directory.Path);
        var files = new AtomicFileStore(paths);
        var group = TemplateGroup.Create("group_delete", "待删除", DateTimeOffset.UtcNow);
        var ownedPath = "templates/groups/group_delete/drafts/revision_delete/staging/resource_delete/template.psd";
        await files.WriteAtomicallyAsync(ownedPath, sourceBytes, CancellationToken.None);

        await using var store = new SqliteDesktopStore(new DesktopDataOptions(directory.Path));
        await store.InitializeAsync();
        await store.SaveGroupAsync(group, CancellationToken.None);
        var workspace = new TemplateWorkspaceService(
            store,
            store,
            importer: null!,
            TimeProvider.System,
            ownedStorage: new TemplateOwnedStorage(paths));

        await workspace.DeleteTemplateGroupAsync(group.GroupId, CancellationToken.None);

        Assert.Null(await store.GetGroupAsync(group.GroupId, CancellationToken.None));
        Assert.False(File.Exists(paths.ResolveOwnedPath(ownedPath)));
        Assert.Equal(sourceHash, Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(sourcePath))));
    }

    [Fact]
    public async Task LockedOwnedFileKeepsSqliteMetadataAndCanBeRetriedAfterRelease()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var directory = new TestDirectory();
        var paths = new LocalAppPathPolicy(directory.Path);
        var files = new AtomicFileStore(paths);
        var group = TemplateGroup.Create("group_delete_locked", "占用中的模板", DateTimeOffset.UtcNow);
        var ownedPath =
            "templates/groups/group_delete_locked/drafts/revision_delete/staging/resource_delete/template.psd";
        await files.WriteAtomicallyAsync(
            ownedPath,
            new byte[] { 1, 2, 3, 4 },
            CancellationToken.None);

        await using var store = new SqliteDesktopStore(new DesktopDataOptions(directory.Path));
        await store.InitializeAsync();
        await store.SaveGroupAsync(group, CancellationToken.None);
        var workspace = new TemplateWorkspaceService(
            store,
            store,
            importer: null!,
            TimeProvider.System,
            ownedStorage: new TemplateOwnedStorage(paths));

        await using (var locked = new FileStream(
                         paths.ResolveOwnedPath(ownedPath),
                         FileMode.Open,
                         FileAccess.Read,
                         FileShare.Read))
        {
            var failure = await Assert.ThrowsAsync<IOException>(() =>
                workspace.DeleteTemplateGroupAsync(group.GroupId, CancellationToken.None));

            Assert.Equal("TPL_DELETE_FILES_FAILED", failure.Message);
            Assert.NotNull(await store.GetGroupAsync(group.GroupId, CancellationToken.None));
            Assert.True(File.Exists(paths.ResolveOwnedPath(ownedPath)));
        }

        var blockers = await workspace.DeleteTemplateGroupAsync(group.GroupId, CancellationToken.None);

        Assert.Empty(blockers);
        Assert.Null(await store.GetGroupAsync(group.GroupId, CancellationToken.None));
        Assert.False(File.Exists(paths.ResolveOwnedPath(ownedPath)));
    }

    private static TemplatePage Page(string pageId, string revisionId, int order) => TemplatePage.Create(
        pageId,
        revisionId,
        order,
        pageId,
        TemplatePageContentType.NormalComposition,
        TemplateGenerationMode.OncePerProduct,
        null,
        enabled: true,
        $"resource_{pageId}");

    private static FrameBinding Binding(string frameId, string pageId) => FrameBinding.Create(
        frameId,
        pageId,
        LayerPath.Create([2]),
        ExpectedLayerEvidence.Create(
            257,
            "registered_frame",
            visible: true,
            clipping: false,
            userMask: false,
            vectorMask: false,
            byte.MaxValue,
            "normal",
            LayerStructureSignature.Create(
                "sha256:abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789")),
        [],
        SelectionRequirement.Create("Choose one suitable source."),
        LayoutIntent.Create("subject_complete", 1, 0, 0, TemplateRect.Create(0, 0, 790, 1252)));

    private static PsdInspectionDocument Inspection() => new(
        new string('a', 64),
        4,
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
            new PsdSmartObjectInspection("u1", "asset.psb", "embedded", "psb", "psb", 4, false, []))]);

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
            RuleDigest,
            new RevisionTransitionEvidence(CommitArtifactsConsistent: true));
    }
    [Fact]
    public async Task TemplateRevisionReadPathPreservesCommittedGraphAfterRestart()
    {
        using var directory = new TestDirectory();
        var options = new DesktopDataOptions(directory.Path);
        var now = DateTimeOffset.UtcNow;
        var group = TemplateGroup.Create("group_query_contract", "query contract", now);
        var draft = TemplateRevision.CreateDraft("revision_query_contract", group.GroupId, 1, now,
            [Page("page_query_contract", "revision_query_contract", 0)],
            [Binding("frame_query_contract", "page_query_contract")]);
        var committed = Commit(draft);
        await using (var store = new SqliteDesktopStore(options))
        {
            await store.InitializeAsync();
            await store.SaveGroupAsync(group, default);
            await store.SaveDraftRevisionAsync(draft, default);
            await store.CommitRevisionAsync(committed, null, default);
        }
        await using var reopened = new SqliteDesktopStore(options);
        await reopened.InitializeAsync();
        var loadedGroup = await reopened.GetGroupAsync(group.GroupId, default);
        var loadedRevision = await reopened.GetRevisionAsync(committed.RevisionId, default);
        var latest = await reopened.GetLatestRevisionForGroupAsync(group.GroupId, default);
        Assert.Equal(committed.RevisionId, loadedGroup!.CurrentRevisionId);
        Assert.Equal(committed.RevisionId, loadedRevision!.RevisionId);
        Assert.Equal(committed.RevisionId, latest!.RevisionId);
        Assert.Equal(committed.Pages.Select(page => page.PageId), loadedRevision.Pages.Select(page => page.PageId));
        Assert.Equal(committed.FrameBindings.Select(binding => binding.FrameId), loadedRevision.FrameBindings.Select(binding => binding.FrameId));
    }
}
