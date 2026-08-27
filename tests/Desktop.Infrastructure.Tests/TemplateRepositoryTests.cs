using Desktop.Domain.Templates;
using Desktop.Infrastructure.Persistence;

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
