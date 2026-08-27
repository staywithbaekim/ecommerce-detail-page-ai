using Desktop.Domain.Templates;

namespace Desktop.Domain.Tests;

public sealed class TemplateDomainTests
{
    private const string RuleDigest =
        "sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    [Theory]
    [InlineData(TemplateGenerationMode.OncePerProduct, 1, "TPL_PAGE_VARIANT_CAPACITY_FORBIDDEN")]
    [InlineData(TemplateGenerationMode.OncePerVariant, 2, "TPL_PAGE_VARIANT_CAPACITY_FORBIDDEN")]
    [InlineData(TemplateGenerationMode.VariantCapacityCombination, null, "TPL_PAGE_VARIANT_CAPACITY_INVALID")]
    [InlineData(TemplateGenerationMode.VariantCapacityCombination, 0, "TPL_PAGE_VARIANT_CAPACITY_INVALID")]
    public void PageConfigurationEnforcesCombinationCapacity(
        TemplateGenerationMode mode,
        int? capacity,
        string expectedCode)
    {
        var exception = Assert.Throws<TemplateRuleViolationException>(() => Page(
            "page_01",
            "revision_01",
            0,
            mode,
            capacity));

        Assert.Equal(expectedCode, exception.Code);
    }

    [Fact]
    public void IncrementInheritsStablePageAndFrameIds()
    {
        var page = Page("page_01", "revision_01", 0);
        var binding = Binding("frame_01", page.PageId);
        var committed = Commit(TemplateRevision.CreateDraft(
            "revision_01",
            "group_01",
            1,
            DateTimeOffset.UtcNow,
            [page],
            [binding]));
        var addedPage = Page("page_02", "revision_02", 1);

        var increment = TemplateRevision.CreateIncrement(
            "revision_02",
            committed,
            2,
            DateTimeOffset.UtcNow,
            [addedPage]);

        Assert.Equal(["page_01", "page_02"], increment.Pages.Select(item => item.PageId));
        Assert.Equal("revision_02", increment.Pages[0].RevisionId);
        Assert.Equal("resource_page_01", increment.Pages[0].TemplateResourceId);
        Assert.Equal("frame_01", Assert.Single(increment.FrameBindings).FrameId);
        Assert.Equal(committed.FrameBindings[0].ExpectedLayer.StructureSignature.Value,
            increment.FrameBindings[0].ExpectedLayer.StructureSignature.Value);
    }

    [Fact]
    public void RevisionStateMachineRequiresEvidenceAndCommittedRevisionIsImmutable()
    {
        var draft = TemplateRevision.CreateDraft("revision_01", "group_01", 1, DateTimeOffset.UtcNow);

        var missingEvidence = Assert.Throws<TemplateRuleViolationException>(() =>
            draft.AdvanceTo(TemplateRevisionState.ImportingCopy, new RevisionTransitionEvidence()));
        Assert.Equal("TPL_REVISION_TRANSITION_EVIDENCE_MISSING", missingEvidence.Code);

        var committed = Commit(draft);
        var immutable = Assert.Throws<TemplateRuleViolationException>(() =>
            committed.AdvanceTo(TemplateRevisionState.CommitFailed, new RevisionTransitionEvidence()));
        Assert.Equal("TPL_REVISION_TRANSITION_INVALID", immutable.Code);
    }

    [Fact]
    public void RevisionCommitRejectsAbsoluteOrEscapingManifestReference()
    {
        var committing = AdvanceToCommitting(
            TemplateRevision.CreateDraft("revision_01", "group_01", 1, DateTimeOffset.UtcNow));

        var absolute = Assert.Throws<TemplateRuleViolationException>(() => committing.Commit(
            @"C:\customer\revision-manifest.json",
            RuleDigest,
            new RevisionTransitionEvidence(CommitArtifactsConsistent: true)));
        var escaping = Assert.Throws<TemplateRuleViolationException>(() => committing.Commit(
            "../revision-manifest.json",
            RuleDigest,
            new RevisionTransitionEvidence(CommitArtifactsConsistent: true)));

        Assert.Equal("TPL_REVISION_MANIFEST_INVALID", absolute.Code);
        Assert.Equal("TPL_REVISION_MANIFEST_INVALID", escaping.Code);
    }

    [Fact]
    public void CurrentPointerOnlyAcceptsCommittedRevisionAndExpectedPointer()
    {
        var now = DateTimeOffset.UtcNow;
        var group = TemplateGroup.Create("group_01", "Template", now);
        var draft = TemplateRevision.CreateDraft("revision_01", group.GroupId, 1, now);

        var notCommitted = Assert.Throws<TemplateRuleViolationException>(() =>
            group.PointTo(draft, null, now));
        Assert.Equal("TPL_REVISION_NOT_COMMITTED", notCommitted.Code);

        var committed = Commit(draft);
        var updated = group.PointTo(committed, null, now.AddMinutes(1));
        var stale = Assert.Throws<TemplateRuleViolationException>(() =>
            updated.PointTo(committed, null, now.AddMinutes(2)));

        Assert.Equal(committed.RevisionId, updated.CurrentRevisionId);
        Assert.Equal("TPL_REVISION_CONCURRENCY_CONFLICT", stale.Code);
    }

    [Fact]
    public void BatchSnapshotFreezesPagesAndCannotSwitchAfterAnalysisStarts()
    {
        var first = Commit(TemplateRevision.CreateDraft(
            "revision_01",
            "group_01",
            1,
            DateTimeOffset.UtcNow,
            [Page("page_01", "revision_01", 0)]));
        var second = Commit(TemplateRevision.CreateIncrement(
            "revision_02",
            first,
            2,
            DateTimeOffset.UtcNow,
            [Page("page_02", "revision_02", 1)]));
        var snapshot = BatchTemplateSnapshot.Create(
            "snapshot_01",
            "batch_01",
            first,
            ["page_01"],
            DateTimeOffset.UtcNow);

        var exception = Assert.Throws<TemplateRuleViolationException>(() =>
            snapshot.ReplaceForDraftBatch(
                "snapshot_02",
                second,
                ["page_01", "page_02"],
                analysisStarted: true,
                DateTimeOffset.UtcNow));

        Assert.Equal("TPL_BATCH_SNAPSHOT_FROZEN", exception.Code);
        Assert.Equal("revision_01", snapshot.RevisionId);
        Assert.Equal("page_01", Assert.Single(snapshot.Pages).PageId);
    }

    [Fact]
    public void FrameRegistrationRequiresBottomToTopStructuralEvidence()
    {
        var pathFailure = Assert.Throws<TemplateRuleViolationException>(() => LayerPath.Create([]));
        var signatureFailure = Assert.Throws<TemplateRuleViolationException>(() =>
            LayerStructureSignature.Create("missing"));

        Assert.Equal("TPL_FRAME_LAYER_PATH_INVALID", pathFailure.Code);
        Assert.Equal("TPL_FRAME_STRUCTURE_EVIDENCE_INCOMPLETE", signatureFailure.Code);
        Assert.Equal(LayerPath.PsdBottomToTop, Binding("frame_01", "page_01").LayerPath.Order);
    }

    internal static TemplatePage Page(
        string pageId,
        string revisionId,
        int order,
        TemplateGenerationMode mode = TemplateGenerationMode.OncePerProduct,
        int? capacity = null) => TemplatePage.Create(
            pageId,
            revisionId,
            order,
            pageId,
            TemplatePageContentType.NormalComposition,
            mode,
            capacity,
            enabled: true,
            $"resource_{pageId}");

    internal static FrameBinding Binding(string frameId, string pageId) => FrameBinding.Create(
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
        LayoutIntent.Create(
            "subject_complete",
            1,
            0,
            0,
            TemplateRect.Create(0, 0, 790, 1252)));

    internal static TemplateRevision Commit(TemplateRevision revision)
    {
        var committing = AdvanceToCommitting(revision);
        return committing.Commit(
            $"templates/groups/{revision.GroupId}/revisions/{revision.RevisionId}/revision-manifest.json",
            RuleDigest,
            new RevisionTransitionEvidence(CommitArtifactsConsistent: true));
    }

    private static TemplateRevision AdvanceToCommitting(TemplateRevision revision)
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
        return review.AdvanceTo(
            TemplateRevisionState.Committing,
            new RevisionTransitionEvidence(ConfigurationValid: true));
    }
}
