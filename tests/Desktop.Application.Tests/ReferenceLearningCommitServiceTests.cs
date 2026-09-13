using Desktop.Application.Psd;
using Desktop.Application.Templates;
using Desktop.Contracts.Ai;
using Desktop.Domain.Templates;

namespace Desktop.Application.Tests;

public sealed class ReferenceLearningCommitServiceTests
{
    [Fact]
    public async Task ReadyLearningAddsReferenceOnlyBindingsAndMarksSessionCommitted()
    {
        var revision = DraftRevision();
        var templates = new StubTemplateRepository(revision);
        var workspace = new StubWorkspace();
        var sessions = new CapturingSessionStore();
        var service = new ReferenceLearningCommitService(
            templates,
            workspace,
            sessions,
            new FixedTimeProvider());

        var result = await service.CommitAsync(
            ReadyDocument(),
            DraftSession(),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.True(workspace.CommitCalled);
        Assert.Equal(ReferenceLearningState.Committed, result.Document!.State);
        Assert.Equal(ReferenceLearningState.Committed, sessions.Saved!.State);
        var binding = Assert.Single(templates.Revision.FrameBindings);
        Assert.Contains(binding.SelectionRequirement.Criteria, item =>
            item.Key == "asset_id" && item.Value == "asset_reference_front");
        Assert.Contains(binding.SelectionRequirement.Criteria, item =>
            item.Key == "reference_only" && item.Value == "true");
        Assert.Contains(binding.SelectionRequirement.Criteria, item =>
            item.Key == "reference_audit_version" && item.Value == "reference-audit/v1");
        Assert.Contains(binding.SelectionRequirement.Criteria, item =>
            item.Key == "reference_set_digest" && item.Value == "sha256:" + new string('d', 64));
        Assert.Contains(binding.SelectionRequirement.Criteria, item =>
            item.Key == "reference_hashes_00" && item.Value == new string('a', 64));
        Assert.Contains(binding.SelectionRequirement.Criteria, item =>
            item.Key == "semantic_model_version" && item.Value == ReferenceLearningAiService.ModelConfigVersion);
    }

    [Fact]
    public async Task WorkspaceCommitFailureKeepsLearningUncommittedAndDraftRecoverable()
    {
        var templates = new StubTemplateRepository(DraftRevision());
        var workspace = new StubWorkspace { FailCommit = true };
        var sessions = new CapturingSessionStore();
        var service = new ReferenceLearningCommitService(templates, workspace, sessions);

        var result = await service.CommitAsync(
            ReadyDocument(),
            DraftSession(),
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("TPL_REVISION_CONCURRENCY_CONFLICT", result.Error!.Code);
        Assert.Equal(TemplateRevisionState.Draft, templates.Revision.State);
        Assert.Single(templates.Revision.FrameBindings);
        Assert.Null(sessions.Saved);
    }

    [Fact]
    public async Task MultipleReviewedPagesAreAddedBeforeSingleWorkspaceCommit()
    {
        var revision = DraftRevision(
            Page("page_learn", "revision_learn", 0, "resource_template"),
            Page("page_detail", "revision_learn", 1, "resource_detail"));
        var templates = new StubTemplateRepository(revision);
        var workspace = new StubWorkspace();
        var service = new ReferenceLearningCommitService(templates, workspace);

        var result = await service.CommitAsync(
            [
                ReadyDocument(),
                ReadyDocument("page_detail", "learn_frame_8", "asset_reference_detail")
            ],
            DraftSession(),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.True(workspace.CommitCalled);
        Assert.Equal(2, templates.Revision.FrameBindings.Length);
        Assert.Equal(
            ["page_detail", "page_learn"],
            templates.Revision.FrameBindings.Select(binding => binding.PageId).OrderBy(value => value));
    }

    [Fact]
    public async Task InvalidCommittedSessionCheckpointIsReportedAfterTemplateCommit()
    {
        var templates = new StubTemplateRepository(DraftRevision());
        var workspace = new StubWorkspace();
        var service = new ReferenceLearningCommitService(
            templates,
            workspace,
            new InvalidSessionStore());

        var result = await service.CommitAsync(
            ReadyDocument(),
            DraftSession(),
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.True(workspace.CommitCalled);
        Assert.Equal("TPL_LEARN_COMMIT_CHECKPOINT_FAILED", result.Error!.Code);
        Assert.True(result.Error.Retryable);
    }

    [Fact]
    public async Task DuplicateFrameIdentityAcrossPagesFailsClosedBeforeDraftMutation()
    {
        var revision = DraftRevision(
            Page("page_learn", "revision_learn", 0, "resource_template"),
            Page("page_detail", "revision_learn", 1, "resource_detail"));
        var templates = new StubTemplateRepository(revision);
        var workspace = new StubWorkspace();
        var service = new ReferenceLearningCommitService(templates, workspace);

        var result = await service.CommitAsync(
            [
                ReadyDocument("page_learn", "duplicate_frame", "asset_reference_front"),
                ReadyDocument("page_detail", "duplicate_frame", "asset_reference_detail")
            ],
            DraftSession(),
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("TPL_LEARN_BINDING_SET_INVALID", result.Error!.Code);
        Assert.Empty(templates.Revision.FrameBindings);
        Assert.False(workspace.CommitCalled);
    }

    private static TemplateDraftSession DraftSession() => new(
        "group_learn", "学习模板", "revision_learn", 2, new HashSet<string>());

    private static TemplateRevision DraftRevision(params TemplatePage[] pages)
    {
        return TemplateRevision.CreateDraft(
            "revision_learn", "group_learn", 2,
            new DateTimeOffset(2026, 9, 5, 0, 0, 0, TimeSpan.Zero),
            pages.Length == 0
                ? [Page("page_learn", "revision_learn", 0, "resource_template")]
                : pages);
    }

    private static TemplatePage Page(
        string pageId,
        string revisionId,
        int order,
        string resourceId) => TemplatePage.Create(
        pageId,
        revisionId,
        order,
        pageId,
        TemplatePageContentType.NormalComposition,
        TemplateGenerationMode.OncePerProduct,
        null,
        true,
        resourceId);

    private static ReferenceLearningDocument ReadyDocument(
        string pageId = "page_learn",
        string frameId = "learn_frame_7",
        string assetId = "asset_reference_front")
    {
        var layout = LayoutIntent.Create(
            "subject_cover", 1, 0, 0, TemplateRect.Create(0, 0, 100, 200));
        var candidate = new ReferenceFrameCandidate(
            frameId, pageId, [0], 7, "front",
            "sha256:" + new string('c', 64),
            true, true, false, false, false, 255, "normal",
            [0, 0, 100, 0, 100, 200, 0, 200],
            ReferenceMatchState.Exact,
            assetId,
            "front.png",
            new string('a', 64),
            "正面商品图",
            1,
            layout,
            ReferenceReviewDecision.Accepted,
            PixelEvidence: new PsdLayerPixelEvidence(
                true,
                20_000,
                20_000,
                new PsdLayerBounds(0, 0, 100, 200)),
            SemanticMetadata: new ReferenceSemanticMetadata(
                ReferenceSemanticSource.AiV2,
                "reference-learning-ai/v2",
                "reference-learning-ai-result/v2",
                ReferenceLearningAiService.ModelConfigVersion,
                ReferenceLearningAiProtocol.SemanticPromptVersion,
                "sha256:" + new string('e', 64),
                ["主体完整"],
                ["排除其他姿态"]));
        var candidates = new[] { candidate };
        var photos = new[]
        {
            new ReferencePhotoFingerprint(
                assetId, "front.png", "front.png", 3,
                new string('a', 64), "image/png", 100, 200)
        };
        return new ReferenceLearningDocument(
            "learn_commit_" + pageId, pageId, "templates/reference.psd",
            "sha256:" + new string('b', 64), 3,
            "sha256:" + new string('c', 64),
            new PsdCanvasInspection(100, 200, 8, "RGB"),
            "sha256:" + new string('d', 64),
            photos,
            candidates,
            ReferenceLearningState.ReadyToCommit,
            ReferenceLearningDigest.Compute(
                "sha256:" + new string('b', 64),
                "sha256:" + new string('c', 64),
                "sha256:" + new string('d', 64),
                photos,
                candidates),
            new DateTimeOffset(2026, 9, 5, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 9, 5, 0, 0, 0, TimeSpan.Zero));
    }

    private sealed class StubTemplateRepository(TemplateRevision revision) : ITemplateRepository
    {
        public TemplateRevision Revision { get; private set; } = revision;

        public Task<TemplateRevision?> GetRevisionAsync(string revisionId, CancellationToken cancellationToken) =>
            Task.FromResult<TemplateRevision?>(revisionId == Revision.RevisionId ? Revision : null);

        public Task SaveDraftRevisionAsync(TemplateRevision revision, CancellationToken cancellationToken)
        {
            Revision = revision;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<TemplateGroup>> ListGroupsAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task SaveGroupAsync(TemplateGroup group, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<TemplateGroup?> GetGroupAsync(string groupId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<TemplateRevision?> GetLatestRevisionForGroupAsync(string groupId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task CommitRevisionAsync(TemplateRevision committedRevision, string? expectedCurrentRevisionId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task SaveCalibrationDraftAsync(CalibrationDraft draft, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<CalibrationDraft?> GetCalibrationDraftAsync(string draftId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<string>> GetBlockingBatchIdsAsync(string groupId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task DeleteGroupAsync(string groupId, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class StubWorkspace : ITemplateWorkspaceService
    {
        public bool CommitCalled { get; private set; }
        public bool FailCommit { get; init; }

        public Task<TemplateGroupDetails> CommitDraftAsync(TemplateDraftSession session, CancellationToken cancellationToken)
        {
            CommitCalled = true;
            if (FailCommit)
            {
                throw new TemplateRuleViolationException("TPL_REVISION_CONCURRENCY_CONFLICT");
            }

            return Task.FromResult(new TemplateGroupDetails(
                session.GroupId,
                session.GroupName,
                session.DraftRevisionId,
                session.RevisionNo,
                [],
                true,
                new DateTimeOffset(2026, 9, 5, 1, 0, 0, TimeSpan.Zero)));
        }

        public Task<TemplateGroupDetails> CommitDraftAsNewGroupAsync(
            TemplateDraftSession session,
            string newGroupName,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<TemplateLibraryItem>> ListAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<TemplateGroupDetails?> GetDetailsAsync(string groupId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<TemplateGroupDetails?> GetCommittedDetailsAsync(string groupId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task RenameTemplateGroupAsync(string groupId, string newName, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<TemplateDraftSession> BeginCreateAsync(string groupName, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<TemplateDraftSession> BeginIncrementAsync(string groupId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<TemplateDraftSession> BeginPageEditAsync(string groupId, string pageId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<TemplateDraftSession> BeginFullEditAsync(string groupId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<TemplateDraftSession> ResumeDraftAsync(string groupId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<TemplateWorkspaceImportResult> ImportPageAsync(TemplateDraftSession session, string commandId, string resourceId, string sourcePsdPath, string pageName, TemplateDuplicateDecision duplicateDecision, string? selectedDuplicateResourceId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<TemplateDraftView> GetDraftAsync(TemplateDraftSession session, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task ConfigurePageAsync(TemplateDraftSession session, string pageId, string name, TemplatePageContentTypeOption contentType, TemplateGenerationModeOption generationMode, int? variantCapacity, bool enabled, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task ReorderPagesAsync(TemplateDraftSession session, IReadOnlyList<string> orderedPageIds, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task RemovePageAsync(TemplateDraftSession session, string pageId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<TemplateBatchSnapshotView> CreateBatchSnapshotAsync(string groupId, string batchId, IReadOnlyList<string> selectedPageIds, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<TemplateBatchSnapshotView> UpdateDraftBatchSnapshotAsync(string groupId, string batchId, IReadOnlyList<string> selectedPageIds, bool analysisStarted, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<string>> DeleteTemplateGroupAsync(string groupId, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class CapturingSessionStore : IReferenceLearningSessionStore
    {
        public ReferenceLearningDocument? Saved { get; private set; }

        public Task SaveAsync(ReferenceLearningDocument document, CancellationToken cancellationToken)
        {
            Saved = document;
            return Task.CompletedTask;
        }

        public Task<ReferenceLearningDocument?> GetAsync(string learningId, CancellationToken cancellationToken) =>
            Task.FromResult(Saved is not null && Saved.LearningId == learningId ? Saved : null);
    }

    private sealed class InvalidSessionStore : IReferenceLearningSessionStore
    {
        public Task SaveAsync(ReferenceLearningDocument document, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("TPL_LEARN_SESSION_SCHEMA_INVALID");

        public Task<ReferenceLearningDocument?> GetAsync(
            string learningId,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() =>
            new(2026, 9, 5, 2, 0, 0, TimeSpan.Zero);
    }
}
