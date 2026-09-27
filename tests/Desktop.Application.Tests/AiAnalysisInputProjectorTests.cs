using Desktop.Application.Ai;
using Desktop.Domain.Inputs;
using Desktop.Domain.Templates;

namespace Desktop.Application.Tests;

public sealed class AiAnalysisInputProjectorTests
{
    [Fact]
    public void ProjectsOnlyReadyStyleAssetsAndSnapshotRequirements()
    {
        var now = DateTimeOffset.UtcNow;
        var revision = CommittedRevision(now);
        var batch = ReadyBatch(revision, now);

        var request = AiAnalysisInputProjector.Project(
            batch,
            revision,
            "style_01",
            new AiAnalysisProjectionOptions("corr_01", "consent_01", "model/proposed", "prompt/v1", "copy/v1"));

        Assert.Equal("ai-analysis/v1", request.SchemaVersion);
        Assert.Equal("batch_01", request.BatchId);
        Assert.Equal("style_01", request.ProductId);
        Assert.StartsWith("sha256:", request.RequirementsDigest, StringComparison.Ordinal);
        Assert.Equal(71, request.RequirementsDigest.Length);
        Assert.NotEqual("sha256:" + new string('d', 64), request.RequirementsDigest);
        Assert.Equal("asset_01", Assert.Single(request.Assets).AssetId);
        Assert.Equal("frame_01", Assert.Single(request.Requirements).RequirementId);
        Assert.DoesNotContain(request.Assets, asset => asset.AnalysisCopyReference.Contains(':'));
    }

    [Fact]
    public void DynamicSizeTablePageAddsSourceRequirementWithoutAProductFrame()
    {
        var now = DateTimeOffset.UtcNow;
        var revision = DynamicSizeTableRevision(now);
        var request = AiAnalysisInputProjector.Project(
            ReadyBatch(revision, now), revision, "style_01", Options());

        var requirement = Assert.Single(request.Requirements);
        Assert.Equal(AiAnalysisInputProjector.SizeTableRequirementId("page_01"), requirement.RequirementId);
        Assert.Equal("清晰可读的市场尺码表图片", requirement.SelectionRequirement);
        Assert.Empty(revision.FrameBindings);
    }

    [Fact]
    public void RequirementsDigestIsIndependentFromManifestAndChangesWithRequirements()
    {
        var now = DateTimeOffset.UtcNow;
        var revision = CommittedRevision(now);
        var batch = ReadyBatch(revision, now);
        var baseline = AiAnalysisInputProjector.Project(batch, revision, "style_01", Options());

        var differentManifest = BatchDraft.Restore(
            batch.BatchId, batch.ScanStatus, batch.InputType, batch.InputRoot, batch.OutputRoot,
            batch.TemplateSnapshot, batch.SelectionMode, batch.IncludeEditablePsd, batch.Styles,
            batch.Assets, batch.ManifestReference, "sha256:" + new string('e', 64), null,
            batch.CreatedAt, batch.UpdatedAt);
        var manifestChanged = AiAnalysisInputProjector.Project(
            differentManifest, revision, "style_01", Options());

        Assert.Equal(baseline.RequirementsDigest, manifestChanged.RequirementsDigest);
        Assert.NotEqual("sha256:" + new string('d', 64), baseline.RequirementsDigest);

        var changedBinding = FrameBinding.Create(
            "frame_01", "page_01", LayerPath.Create([1]),
            ExpectedLayerEvidence.Create(1, "frame", true, false, false, false, 255, "normal",
                LayerStructureSignature.Create("sha256:" + new string('b', 64))),
            [], SelectionRequirement.Create("选择背面商品图", [SelectionCriterion.Create("asset_id", "asset_01")]),
            LayoutIntent.Create("主体完整", 1, 0, 0, TemplateRect.Create(0, 0, 100, 100)));
        var changedRevision = TemplateRevision.Restore(
            revision.RevisionId, revision.GroupId, revision.RevisionNo, TemplateRevisionState.Committed,
            revision.ManifestRef, revision.RuleDigest, revision.CreatedAt, revision.Pages, [changedBinding]);

        var requirementChanged = AiAnalysisInputProjector.Project(
            batch, changedRevision, "style_01", Options());
        Assert.NotEqual(baseline.RequirementsDigest, requirementChanged.RequirementsDigest);
    }

    [Fact]
    public void RequirementsDigestEscapesDelimiterAndNewlineContent()
    {
        var now = DateTimeOffset.UtcNow;
        var firstRevision = RevisionWithRequirement("A|B", "C\nD", now);
        var secondRevision = RevisionWithRequirement("A", "B|C\nD", now);
        var first = AiAnalysisInputProjector.Project(ReadyBatch(firstRevision, now), firstRevision, "style_01", Options());
        var second = AiAnalysisInputProjector.Project(ReadyBatch(secondRevision, now), secondRevision, "style_01", Options());

        Assert.NotEqual(first.RequirementsDigest, second.RequirementsDigest);
    }

    [Fact]
    public void RejectsDraftAndScanningBatchesBeforeReadingAssets()
    {
        var now = DateTimeOffset.UtcNow;
        var revision = CommittedRevision(now);
        var draft = BatchDraft.Create(
            "batch_01", @"C:\\input", @"C:\\output", Snapshot(revision, now),
            BatchSelectionMode.Automatic, false, now);

        var exception = Assert.Throws<InvalidOperationException>(() => AiAnalysisInputProjector.Project(
            draft, revision, "style_01", Options()));

        Assert.Equal("AI_INPUT_BATCH_NOT_READY", exception.Message);
    }

    [Fact]
    public void RejectsManifestReferenceAndRuleDigestDrift()
    {
        var now = DateTimeOffset.UtcNow;
        var revision = CommittedRevision(now);
        var batch = ReadyBatch(revision, now);

        var referenceException = Assert.Throws<InvalidOperationException>(() => AiAnalysisInputProjector.Project(
            BatchDraft.Restore(
                batch.BatchId, batch.ScanStatus, batch.InputType, batch.InputRoot, batch.OutputRoot,
                batch.TemplateSnapshot, batch.SelectionMode, batch.IncludeEditablePsd, batch.Styles,
                batch.Assets, "batches/batch_01/asset-manifest/other.json", batch.ManifestDigest,
                null, batch.CreatedAt, batch.UpdatedAt), revision, "style_01", Options()));
        Assert.Equal("AI_INPUT_MANIFEST_REFERENCE_INVALID", referenceException.Message);

        var mismatchedRevision = TemplateRevision.Restore(
            revision.RevisionId, revision.GroupId, revision.RevisionNo, TemplateRevisionState.Committed,
            revision.ManifestRef, "sha256:" + new string('e', 64), revision.CreatedAt,
            revision.Pages, revision.FrameBindings);
        var digestException = Assert.Throws<InvalidOperationException>(() => AiAnalysisInputProjector.Project(
            batch, mismatchedRevision, "style_01", Options()));
        Assert.Equal("AI_INPUT_TEMPLATE_RULE_DIGEST_MISMATCH", digestException.Message);
    }

    [Fact]
    public void RejectsSnapshotWithoutSelectionRequirements()
    {
        var now = DateTimeOffset.UtcNow;
        var revision = TemplateRevision.Restore(
            "revision_01", "group_01", 1, TemplateRevisionState.Committed,
            "templates/revision_01/manifest.json", "sha256:" + new string('d', 64), now,
            [TemplatePage.Create("page_01", "revision_01", 0, "详情页", TemplatePageContentType.NormalComposition,
                TemplateGenerationMode.OncePerProduct, null, true, "resource_01")], []);
        var batch = ReadyBatch(revision, now);

        var exception = Assert.Throws<InvalidOperationException>(() => AiAnalysisInputProjector.Project(
            batch, revision, "style_01", Options()));
        Assert.Equal("AI_INPUT_SELECTION_REQUIREMENTS_MISSING", exception.Message);
    }

    [Fact]
    public void RejectsReferenceContentBeforeCreatingPaidRequest()
    {
        var now = DateTimeOffset.UtcNow;
        var revision = LearnedRevision(now, new string('a', 64));
        var batch = ReadyBatch(revision, now);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            AiAnalysisInputProjector.Project(batch, revision, "style_01", Options()));

        Assert.Equal("AI_INPUT_REFERENCE_ASSET_FORBIDDEN", exception.Message);
    }

    [Fact]
    public void LearnedReferenceAuditParticipatesInRequirementsDigestWithoutEnteringPrompt()
    {
        var now = DateTimeOffset.UtcNow;
        var firstRevision = LearnedRevision(now, new string('b', 64));
        var secondRevision = LearnedRevision(now, new string('c', 64));
        var batch = ReadyBatch(firstRevision, now);

        var first = AiAnalysisInputProjector.Project(batch, firstRevision, "style_01", Options());
        var second = AiAnalysisInputProjector.Project(batch, secondRevision, "style_01", Options());

        Assert.NotEqual(first.RequirementsDigest, second.RequirementsDigest);
        var requirement = Assert.Single(first.Requirements);
        Assert.DoesNotContain(new string('b', 64), requirement.SelectionRequirement, StringComparison.Ordinal);
        Assert.DoesNotContain("reference", requirement.SelectionRequirement, StringComparison.OrdinalIgnoreCase);
    }

    private static AiAnalysisProjectionOptions Options() =>
        new("corr_01", "consent_01", "model/proposed", "prompt/v1", "copy/v1");

    private static BatchDraft ReadyBatch(TemplateRevision revision, DateTimeOffset now)
    {
        var snapshot = Snapshot(revision, now);
        var draft = BatchDraft.Create(
            "batch_01", @"C:\\input", @"C:\\output", snapshot,
            BatchSelectionMode.Automatic, false, now).BeginScanning(now.AddSeconds(1));
        return draft.MarkScanReady(
            BatchInputType.SingleStyle,
            [StyleDraft.Create("style_01", "SKU-01", "", 0, ["asset_01"])],
            [SourceAssetMetadata.CreateReadable(
                "asset_01", "style_01", "商品/1.jpg", "1.jpg", 10, new string('a', 64),
                100, 100, SourceImageFormat.Jpeg, "image/jpeg")],
            "batches/batch_01/asset-manifest/manifest.json",
            "sha256:" + new string('d', 64),
            now.AddSeconds(2));
    }

    private static BatchTemplateSnapshot Snapshot(TemplateRevision revision, DateTimeOffset now) =>
        BatchTemplateSnapshot.Create("snapshot_01", "batch_01", revision, ["page_01"], now);

    private static TemplateRevision CommittedRevision(DateTimeOffset now)
        => RevisionWithRequirement("选择完整商品图", "主体完整", now);

    private static TemplateRevision DynamicSizeTableRevision(DateTimeOffset now)
    {
        var page = TemplatePage.Create(
            "page_01", "revision_01", 0, "尺码表", TemplatePageContentType.DynamicSizeTable,
            TemplateGenerationMode.OncePerProduct, null, true, "resource_01");
        return TemplateRevision.Restore(
            "revision_01", "group_01", 1, TemplateRevisionState.Committed,
            "templates/revision_01/manifest.json", "sha256:" + new string('d', 64), now,
            [page], []);
    }

    private static TemplateRevision RevisionWithRequirement(string selection, string layout, DateTimeOffset now)
    {
        var page = TemplatePage.Create(
            "page_01", "revision_01", 0, "详情页", TemplatePageContentType.NormalComposition,
            TemplateGenerationMode.OncePerProduct, null, true, "resource_01");
        var binding = FrameBinding.Create(
            "frame_01", "page_01", LayerPath.Create([1]),
            ExpectedLayerEvidence.Create(1, "frame", true, false, false, false, 255, "normal",
                LayerStructureSignature.Create("sha256:" + new string('b', 64))),
            [], SelectionRequirement.Create(selection, [SelectionCriterion.Create("asset_id", "asset_01")]),
            LayoutIntent.Create(layout, 1, 0, 0, TemplateRect.Create(0, 0, 100, 100)));
        return TemplateRevision.Restore(
            "revision_01", "group_01", 1, TemplateRevisionState.Committed,
            "templates/revision_01/manifest.json", "sha256:" + new string('d', 64), now,
            [page], [binding]);
    }

    private static TemplateRevision LearnedRevision(DateTimeOffset now, string referenceHash)
    {
        var page = TemplatePage.Create(
            "page_01", "revision_01", 0, "详情页", TemplatePageContentType.NormalComposition,
            TemplateGenerationMode.OncePerProduct, null, true, "resource_01");
        var binding = FrameBinding.Create(
            "frame_01", "page_01", LayerPath.Create([1]),
            ExpectedLayerEvidence.Create(1, "frame", true, false, false, false, 255, "normal",
                LayerStructureSignature.Create("sha256:" + new string('b', 64))),
            [],
            SelectionRequirement.Create(
                "模特坐姿半身，身体朝左，商品主体无遮挡",
                [
                    SelectionCriterion.Create("reference_only", "true"),
                    SelectionCriterion.Create("semantic_source", "AiV2"),
                    SelectionCriterion.Create("reference_audit_version", "reference-audit/v1"),
                    SelectionCriterion.Create("reference_set_digest", "sha256:" + new string('d', 64)),
                    SelectionCriterion.Create("reference_hashes_00", referenceHash)
                ]),
            LayoutIntent.Create("主体完整", 1, 0, 0, TemplateRect.Create(0, 0, 100, 100)));
        return TemplateRevision.Restore(
            "revision_01", "group_01", 1, TemplateRevisionState.Committed,
            "templates/revision_01/manifest.json", "sha256:" + new string('d', 64), now,
            [page], [binding]);
    }
}
