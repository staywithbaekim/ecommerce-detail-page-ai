using System.Security.Cryptography;
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

public sealed class ReferenceLearningCommitIntegrationTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 9, 5, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task MultiPageCommitPersistsSqliteMarkersAndSessionsAcrossRestartWithoutChangingSources()
    {
        using var appRoot = new TestDirectory();
        using var sourceRoot = new TestDirectory();
        var sourcePsd = await WriteSourceAsync(sourceRoot.Path, "reference.psd", [0x38, 0x42, 0x50, 0x53, 1, 2]);
        var sourcePhoto = await WriteSourceAsync(sourceRoot.Path, "front.png", PngBytes());
        var sourcePsdHash = await HashAsync(sourcePsd);
        var sourcePhotoHash = await HashAsync(sourcePhoto);
        var options = new DesktopDataOptions(appRoot.Path);
        var paths = new LocalAppPathPolicy(appRoot.Path);
        var files = new AtomicFileStore(paths);
        var manifests = new JsonManifestStore(files);
        var sessionStore = new JsonReferenceLearningSessionStore(
            files,
            new JsonSchemaContractValidator());
        var session = await PrepareIncrementAsync(options, files);
        var documents = new[]
        {
            ReadyDocument("learn_page_front", "page_front", "frame_front", "asset_reference_front"),
            ReadyDocument("learn_page_detail", "page_detail", "frame_detail", "asset_reference_detail")
        };
        foreach (var document in documents)
        {
            await sessionStore.SaveAsync(document, CancellationToken.None);
        }

        await using (var store = new SqliteDesktopStore(options))
        {
            await store.InitializeAsync();
            var workspace = Workspace(store, manifests);
            var service = new ReferenceLearningCommitService(
                store,
                workspace,
                sessionStore,
                new FixedTimeProvider(Now.AddMinutes(10)));

            var result = await service.CommitAsync(documents, session, CancellationToken.None);

            Assert.True(result.IsSuccess);
            var group = await store.GetGroupAsync(session.GroupId, CancellationToken.None);
            var committed = await store.GetRevisionAsync(session.DraftRevisionId, CancellationToken.None);
            Assert.Equal(session.DraftRevisionId, group!.CurrentRevisionId);
            Assert.Equal(TemplateRevisionState.Committed, committed!.State);
            Assert.Equal(3, committed.Pages.Length);
            Assert.Equal(3, committed.FrameBindings.Length);
            Assert.All(
                committed.FrameBindings.Where(binding => binding.PageId is "page_front" or "page_detail"),
                AssertLearningAuditCriteria);
            Assert.NotNull(await manifests.ReadMarkerAsync(RevisionDirectory(session), "ready.marker", CancellationToken.None));
            Assert.NotNull(await manifests.ReadMarkerAsync(RevisionDirectory(session), "committed.marker", CancellationToken.None));
            Assert.Equal(3, (await manifests.LoadAsync(committed.ManifestRef!, CancellationToken.None))!.Entries.Count);
            Assert.Equal(ReferenceLearningState.Committed,
                (await sessionStore.GetAsync("learn_page_front", CancellationToken.None))!.State);
            Assert.Equal(ReferenceLearningState.Committed,
                (await sessionStore.GetAsync("learn_page_detail", CancellationToken.None))!.State);
        }

        await using (var reopened = new SqliteDesktopStore(options))
        {
            await reopened.InitializeAsync();
            var reopenedSessions = new JsonReferenceLearningSessionStore(
                new AtomicFileStore(new LocalAppPathPolicy(appRoot.Path)),
                new JsonSchemaContractValidator());
            var group = await reopened.GetGroupAsync(session.GroupId, CancellationToken.None);
            var committed = await reopened.GetRevisionAsync(session.DraftRevisionId, CancellationToken.None);
            var baseRevision = await reopened.GetRevisionAsync("revision_base", CancellationToken.None);
            Assert.Equal(session.DraftRevisionId, group!.CurrentRevisionId);
            Assert.Equal(TemplateRevisionState.Committed, committed!.State);
            Assert.All(
                committed.FrameBindings.Where(binding => binding.PageId is "page_front" or "page_detail"),
                AssertLearningAuditCriteria);
            Assert.Equal(["frame_base"], baseRevision!.FrameBindings.Select(binding => binding.FrameId));
            Assert.Equal(TemplateRevisionState.Committed, baseRevision.State);
            Assert.Equal(ReferenceLearningState.Committed,
                (await reopenedSessions.GetAsync("learn_page_front", CancellationToken.None))!.State);
            Assert.Equal(ReferenceLearningState.Committed,
                (await reopenedSessions.GetAsync("learn_page_detail", CancellationToken.None))!.State);

            var duplicate = await new ReferenceLearningCommitService(
                    reopened,
                    Workspace(reopened, new JsonManifestStore(new AtomicFileStore(
                        new LocalAppPathPolicy(appRoot.Path)))),
                    reopenedSessions,
                    new FixedTimeProvider(Now.AddMinutes(20)))
                .CommitAsync(documents, session, CancellationToken.None);
            Assert.True(duplicate.IsSuccess);
            Assert.Equal(session.DraftRevisionId,
                (await reopened.GetGroupAsync(session.GroupId, CancellationToken.None))!.CurrentRevisionId);
        }

        Assert.Equal(sourcePsdHash, await HashAsync(sourcePsd));
        Assert.Equal(sourcePhotoHash, await HashAsync(sourcePhoto));
    }

    [Fact]
    public async Task MissingCommittedMarkerAfterSqliteCommitIsHealedByIdempotentRetry()
    {
        using var appRoot = new TestDirectory();
        var options = new DesktopDataOptions(appRoot.Path);
        var files = new AtomicFileStore(new LocalAppPathPolicy(appRoot.Path));
        var session = await PrepareIncrementAsync(options, files);
        var documents = new[]
        {
            ReadyDocument("learn_page_front", "page_front", "frame_front", "asset_reference_front"),
            ReadyDocument("learn_page_detail", "page_detail", "frame_detail", "asset_reference_detail")
        };

        await using var store = new SqliteDesktopStore(options);
        await store.InitializeAsync();
        var failingFiles = new FailingFileStore(
            files,
            path => path.EndsWith("committed.marker", StringComparison.Ordinal));
        var first = await new ReferenceLearningCommitService(
                store,
                Workspace(store, new JsonManifestStore(failingFiles)))
            .CommitAsync(documents, session, CancellationToken.None);

        Assert.False(first.IsSuccess);
        Assert.Equal("TPL_COMMITTED_MARKER_MISSING", first.Error!.Code);
        Assert.Equal(session.DraftRevisionId,
            (await store.GetGroupAsync(session.GroupId, CancellationToken.None))!.CurrentRevisionId);
        Assert.Null(await new JsonManifestStore(files).ReadMarkerAsync(
            RevisionDirectory(session),
            "committed.marker",
            CancellationToken.None));

        var recovered = await new ReferenceLearningCommitService(
                store,
                Workspace(store, new JsonManifestStore(files)))
            .CommitAsync(documents, session, CancellationToken.None);

        Assert.True(recovered.IsSuccess);
        Assert.NotNull(await new JsonManifestStore(files).ReadMarkerAsync(
            RevisionDirectory(session),
            "committed.marker",
            CancellationToken.None));
        Assert.Equal(TemplateRevisionState.Committed,
            (await store.GetRevisionAsync(session.DraftRevisionId, CancellationToken.None))!.State);
    }

    [Theory]
    [InlineData("revision-manifest.json")]
    [InlineData("ready.marker")]
    public async Task PreSqliteArtifactFailureKeepsBaseRevisionCurrent(string failedSuffix)
    {
        using var appRoot = new TestDirectory();
        var options = new DesktopDataOptions(appRoot.Path);
        var files = new AtomicFileStore(new LocalAppPathPolicy(appRoot.Path));
        var session = await PrepareIncrementAsync(options, files);
        var failingFiles = new FailingFileStore(
            files,
            path => path.EndsWith(failedSuffix, StringComparison.Ordinal));

        await using var store = new SqliteDesktopStore(options);
        await store.InitializeAsync();
        var result = await new ReferenceLearningCommitService(
                store,
                Workspace(store, new JsonManifestStore(failingFiles)))
            .CommitAsync(
                [
                    ReadyDocument("learn_page_front", "page_front", "frame_front", "asset_reference_front"),
                    ReadyDocument("learn_page_detail", "page_detail", "frame_detail", "asset_reference_detail")
                ],
                session,
                CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("revision_base",
            (await store.GetGroupAsync(session.GroupId, CancellationToken.None))!.CurrentRevisionId);
        var draft = await store.GetRevisionAsync(session.DraftRevisionId, CancellationToken.None);
        Assert.Equal(TemplateRevisionState.Draft, draft!.State);
        Assert.Contains(draft.FrameBindings, binding => binding.FrameId == "frame_front");
        Assert.Contains(draft.FrameBindings, binding => binding.FrameId == "frame_detail");
    }

    private static async Task<TemplateDraftSession> PrepareIncrementAsync(
        DesktopDataOptions options,
        AtomicFileStore files)
    {
        var group = TemplateGroup.Create("group_learning", "学习模板", Now);
        var baseResource = Resource("resource_base", group.GroupId, "revision_base", "page_base", 7);
        var basePage = Page("page_base", "revision_base", 0, baseResource.ResourceId);
        var baseDraft = TemplateRevision.CreateDraft(
            "revision_base",
            group.GroupId,
            1,
            Now,
            [basePage],
            [BaseBinding()]);
        var baseCommitted = Commit(baseDraft);
        var frontResource = Resource("resource_front", group.GroupId, "revision_learning", "page_front", 8);
        var detailResource = Resource("resource_detail", group.GroupId, "revision_learning", "page_detail", 9);
        var increment = TemplateRevision.CreateIncrement(
            "revision_learning",
            baseCommitted,
            2,
            Now.AddMinutes(1),
            [
                Page("page_front", "revision_learning", 1, frontResource.ResourceId),
                Page("page_detail", "revision_learning", 2, detailResource.ResourceId)
            ]);
        foreach (var resource in new[] { baseResource, frontResource, detailResource })
        {
            await files.WriteAtomicallyAsync(
                resource.InternalRelativePath,
                Enumerable.Repeat((byte)resource.Inspection.Layers[0].LayerId, checked((int)resource.Length)).ToArray(),
                CancellationToken.None);
        }

        await using var setup = new SqliteDesktopStore(options);
        await setup.InitializeAsync();
        await setup.SaveGroupAsync(group, CancellationToken.None);
        await setup.SaveImportedAsync("import_base", baseResource, CancellationToken.None);
        await setup.SaveImportedAsync("import_front", frontResource, CancellationToken.None);
        await setup.SaveImportedAsync("import_detail", detailResource, CancellationToken.None);
        await setup.SaveDraftRevisionAsync(baseDraft, CancellationToken.None);
        await setup.CommitRevisionAsync(baseCommitted, null, CancellationToken.None);
        await setup.SaveDraftRevisionAsync(increment, CancellationToken.None);
        return new TemplateDraftSession(
            group.GroupId,
            group.Name,
            increment.RevisionId,
            increment.RevisionNo,
            new HashSet<string>([basePage.PageId], StringComparer.Ordinal));
    }

    private static TemplateWorkspaceService Workspace(
        SqliteDesktopStore store,
        IManifestStore manifests) => new(
        store,
        store,
        new TemplatePageImportService(
            new UnusedSourceFilePort(),
            new UnusedInspectionPort(),
            store,
            new FixedTimeProvider(Now)),
        new FixedTimeProvider(Now),
        manifests);

    private static ImportedTemplateResource Resource(
        string resourceId,
        string groupId,
        string revisionId,
        string pageId,
        int layerId)
    {
        var bytes = Enumerable.Repeat((byte)layerId, 8).ToArray();
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var structure = "sha256:" + new string(layerId switch
        {
            7 => 'a',
            8 => 'b',
            _ => 'c'
        }, 64);
        return new ImportedTemplateResource(
            resourceId,
            groupId,
            revisionId,
            $"templates/groups/{groupId}/drafts/{revisionId}/staging/{resourceId}/template.psd",
            bytes.Length,
            hash,
            structure,
            Inspection(hash, bytes.Length, structure, layerId, pageId),
            Now);
    }

    private static PsdInspectionDocument Inspection(
        string hash,
        int length,
        string structure,
        int layerId,
        string pageId) => new(
        hash,
        length,
        new PsdCanvasInspection(790, 1252, 8, "RGB"),
        1,
        1,
        0,
        0,
        0,
        structure,
        new PsdInspectionTiming(1, 1, 2),
        1024,
        null,
        [new PsdLayerInspection(
            [0],
            layerId,
            pageId,
            "smartObject",
            false,
            true,
            true,
            false,
            255,
            255,
            "normal",
            new PsdLayerBounds(0, 0, 790, 1252),
            false,
            false,
            null,
            new PsdSmartObjectInspection(
                $"so_{layerId}",
                "asset.psb",
                "data",
                "psb",
                "psb",
                length,
                false,
                [0, 0, 790, 0, 790, 1252, 0, 1252])
            {
                EmbeddedContentAvailable = true,
                EmbeddedContentSha256 = hash
            })]);

    private static ReferenceLearningDocument ReadyDocument(
        string learningId,
        string pageId,
        string frameId,
        string assetId)
    {
        var photoHash = new string(pageId == "page_front" ? '1' : '2', 64);
        var photo = new ReferencePhotoFingerprint(
            assetId,
            pageId + ".png",
            pageId + ".png",
            8,
            photoHash,
            "image/png",
            790,
            1252);
        var layout = LayoutIntent.Create(
            "subject_cover",
            1.1,
            3,
            -2,
            TemplateRect.Create(0, 0, 790, 1252));
        var candidate = new ReferenceFrameCandidate(
            frameId,
            pageId,
            [0],
            pageId == "page_front" ? 8 : 9,
            "audit-only-layer-name",
            "sha256:" + new string(pageId == "page_front" ? 'b' : 'c', 64),
            true,
            true,
            false,
            false,
            false,
            255,
            "normal",
            [0, 0, 790, 0, 790, 1252, 0, 1252],
            ReferenceMatchState.Exact,
            assetId,
            photo.RelativePath,
            photoHash,
            pageId == "page_front" ? "完整正面图" : "商品细节图",
            1,
            layout,
            ReferenceReviewDecision.Accepted,
            PixelEvidence: new PsdLayerPixelEvidence(
                true,
                790 * 1252,
                790 * 1252,
                new PsdLayerBounds(0, 0, 790, 1252)),
            SemanticMetadata: new ReferenceSemanticMetadata(
                ReferenceSemanticSource.AiV2,
                ReferenceLearningAiProtocol.RequestSchemaVersion,
                ReferenceLearningAiProtocol.ResultSchemaVersion,
                ReferenceLearningAiService.ModelConfigVersion,
                ReferenceLearningAiProtocol.SemanticPromptVersion,
                "sha256:" + new string('e', 64),
                ["主体特征与参考图一致"],
                ["排除其他展示角度"]));
        var photos = new[] { photo };
        var candidates = new[] { candidate };
        var psdHash = "sha256:" + new string(pageId == "page_front" ? '3' : '4', 64);
        var structure = "sha256:" + new string(pageId == "page_front" ? 'b' : 'c', 64);
        var folderDigest = "sha256:" + new string('5', 64);
        return new ReferenceLearningDocument(
            learningId,
            pageId,
            $"templates/reference/{pageId}.psd",
            psdHash,
            8,
            structure,
            new PsdCanvasInspection(790, 1252, 8, "RGB"),
            folderDigest,
            photos,
            candidates,
            ReferenceLearningState.ReadyToCommit,
            ReferenceLearningDigest.Compute(psdHash, structure, folderDigest, photos, candidates),
            Now,
            Now);
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

    private static FrameBinding BaseBinding() => FrameBinding.Create(
        "frame_base",
        "page_base",
        LayerPath.Create([1]),
        ExpectedLayerEvidence.Create(
            7,
            "base",
            true,
            false,
            false,
            false,
            255,
            "normal",
            LayerStructureSignature.Create("sha256:" + new string('a', 64))),
        [],
        SelectionRequirement.Create(
            "选择目标款基础图。",
            [SelectionCriterion.Create("asset_id", "asset_base")]),
        LayoutIntent.Create("subject_cover", 1, 0, 0, TemplateRect.Create(0, 0, 790, 1252)));

    private static TemplateRevision Commit(TemplateRevision revision) => revision
        .AdvanceTo(TemplateRevisionState.ImportingCopy, new RevisionTransitionEvidence(SourceReadable: true))
        .AdvanceTo(TemplateRevisionState.StructureInspecting, new RevisionTransitionEvidence(CopyMatchesSource: true))
        .AdvanceTo(TemplateRevisionState.FrameBinding, new RevisionTransitionEvidence(StructureReadable: true))
        .AdvanceTo(TemplateRevisionState.Calibrating, new RevisionTransitionEvidence(RegisteredFramesHaveEvidence: true))
        .AdvanceTo(TemplateRevisionState.AwaitingReview, new RevisionTransitionEvidence(CalibrationComplete: true))
        .AdvanceTo(TemplateRevisionState.Committing, new RevisionTransitionEvidence(ConfigurationValid: true))
        .Commit(
            $"templates/groups/{revision.GroupId}/revisions/{revision.RevisionId}/revision-manifest.json",
            "sha256:" + new string('f', 64),
            new RevisionTransitionEvidence(CommitArtifactsConsistent: true));

    private static string RevisionDirectory(TemplateDraftSession session) =>
        $"templates/groups/{session.GroupId}/revisions/{session.DraftRevisionId}";

    private static void AssertLearningAuditCriteria(FrameBinding binding)
    {
        var criteria = binding.SelectionRequirement.Criteria.ToDictionary(
            criterion => criterion.Key,
            criterion => criterion.Value,
            StringComparer.Ordinal);
        Assert.Equal("true", criteria["reference_only"]);
        Assert.Equal(ReferenceLearningRuleProjector.ReferenceAuditVersion, criteria["reference_audit_version"]);
        Assert.Equal("sha256:" + new string('5', 64), criteria["reference_set_digest"]);
        Assert.Matches("^[12]{64}$", criteria["reference_hashes_00"]);
        Assert.Equal(ReferenceLearningAiService.ModelConfigVersion, criteria["semantic_model_version"]);
        Assert.Equal(ReferenceLearningAiProtocol.SemanticPromptVersion, criteria["semantic_prompt_version"]);
    }

    private static async Task<string> WriteSourceAsync(string root, string name, byte[] bytes)
    {
        var path = Path.Combine(root, name);
        await File.WriteAllBytesAsync(path, bytes);
        return path;
    }

    private static async Task<string> HashAsync(string path)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream)).ToLowerInvariant();
    }

    private static byte[] PngBytes() =>
        [137, 80, 78, 71, 13, 10, 26, 10, 0, 0, 0, 13, 73, 72, 68, 82,
            0, 0, 0, 1, 0, 0, 0, 1, 8, 2, 0, 0, 0];

    private sealed class FailingFileStore(
        IFileStore inner,
        Func<string, bool> shouldFail) : IFileStore
    {
        public Task WriteAtomicallyAsync(
            string relativePath,
            ReadOnlyMemory<byte> content,
            CancellationToken cancellationToken) => shouldFail(relativePath)
            ? throw new IOException("TEST_REFERENCE_LEARNING_WRITE_FAILURE")
            : inner.WriteAtomicallyAsync(relativePath, content, cancellationToken);

        public Task<byte[]?> ReadAsync(string relativePath, CancellationToken cancellationToken) =>
            inner.ReadAsync(relativePath, cancellationToken);

        public Task<string?> ComputeSha256Async(string relativePath, CancellationToken cancellationToken) =>
            inner.ComputeSha256Async(relativePath, cancellationToken);

        public Task<bool> DeleteAsync(string relativePath, CancellationToken cancellationToken) =>
            inner.DeleteAsync(relativePath, cancellationToken);
    }

    private sealed class UnusedSourceFilePort : ITemplateSourceFilePort
    {
        public Task<SourceFingerprintResult> FingerprintAsync(
            string sourcePsdPath,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<OwnedTemplateCopyResult> CopyVerifiedAsync(
            string sourcePsdPath,
            TemplateSourceFingerprint expectedSource,
            string destinationRelativePath,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<bool> DeleteOwnedCopyAsync(
            string internalRelativePath,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class UnusedInspectionPort : IPsdInspectionPort
    {
        public Task<PsdInspectionResult> InspectAsync(
            PsdInspectionRequest request,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
