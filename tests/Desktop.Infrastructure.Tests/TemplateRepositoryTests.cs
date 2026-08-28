using Desktop.Domain.Templates;
using Desktop.Application.Psd;
using Desktop.Application.Templates;
using Desktop.Infrastructure.Persistence;
using Desktop.Infrastructure.Files;
using Desktop.Infrastructure.Templates;

namespace Desktop.Infrastructure.Tests;

public sealed class TemplateRepositoryTests
{
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
        await store.DeleteGroupAsync(group.GroupId, CancellationToken.None);
        await new TemplateOwnedStorage(paths).DeleteGroupFilesAsync(group.GroupId, CancellationToken.None);

        Assert.Null(await store.GetGroupAsync(group.GroupId, CancellationToken.None));
        Assert.False(File.Exists(paths.ResolveOwnedPath(ownedPath)));
        Assert.Equal(sourceHash, Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(sourcePath))));
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
}
