using Desktop.Application.Psd;
using Desktop.Application.Files;
using Desktop.Application.Templates;
using Desktop.Domain.Templates;

namespace Desktop.Application.Tests;

public sealed class TemplateWorkspaceServiceTests
{
    private const string FileHash =
        "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private const string StructureHash =
        "abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789";

    [Fact]
    public async Task DraftConfigurationAndOrderFlowThroughWorkspaceCommands()
    {
        var templates = new MemoryTemplateRepository();
        var resources = new MemoryResourceRepository();
        var service = CreateService(templates, resources);
        var session = await service.BeginCreateAsync("中文模板组", CancellationToken.None);
        var front = Resource("resource_front", session, 790, 1252);
        var detail = Resource("resource_detail", session, 790, 1252);
        resources.Add(front, detail);
        var revision = (await templates.GetRevisionAsync(session.DraftRevisionId, CancellationToken.None))!
            .AddPage(Page("page_front", session.DraftRevisionId, 0, front.ResourceId))
            .AddPage(Page("page_detail", session.DraftRevisionId, 1, detail.ResourceId));
        await templates.SaveDraftRevisionAsync(revision, CancellationToken.None);

        await service.ConfigurePageAsync(
            session,
            "page_detail",
            "细节组合页",
            TemplatePageContentTypeOption.NormalComposition,
            TemplateGenerationModeOption.VariantCapacityCombination,
            4,
            enabled: true,
            CancellationToken.None);
        await service.ReorderPagesAsync(
            session,
            ["page_detail", "page_front"],
            CancellationToken.None);

        var library = await service.ListAsync(CancellationToken.None);
        var details = await service.GetDetailsAsync(session.GroupId, CancellationToken.None);
        var resumed = await service.ResumeDraftAsync(session.GroupId, CancellationToken.None);
        var draft = await service.GetDraftAsync(resumed, CancellationToken.None);

        Assert.Equal("中文模板组", Assert.Single(library).Name);
        Assert.Equal(2, library[0].PageCount);
        Assert.Equal("草稿", library[0].Status);
        Assert.NotNull(details);
        Assert.False(details.IsCommitted);
        Assert.Equal(session.DraftRevisionId, resumed.DraftRevisionId);
        Assert.Empty(resumed.BasePageIds);
        Assert.Equal(["page_detail", "page_front"], draft.Pages.Select(page => page.Page.PageId));
        Assert.Equal("细节组合页", draft.Pages[0].Page.Name);
        Assert.Equal(4, draft.Pages[0].Page.VariantCapacity);
    }

    [Fact]
    public async Task RemovingNewDraftPageLeavesSourceResourceUntouchedAndReordersDraft()
    {
        var templates = new MemoryTemplateRepository();
        var resources = new MemoryResourceRepository();
        var service = CreateService(templates, resources);
        var session = await service.BeginCreateAsync("可编辑模板", CancellationToken.None);
        var first = Resource("resource_first", session, 790, 1252);
        var second = Resource("resource_second", session, 790, 1252);
        resources.Add(first, second);
        var revision = (await templates.GetRevisionAsync(session.DraftRevisionId, CancellationToken.None))!
            .AddPage(Page("page_first", session.DraftRevisionId, 0, first.ResourceId))
            .AddPage(Page("page_second", session.DraftRevisionId, 1, second.ResourceId));
        await templates.SaveDraftRevisionAsync(revision, CancellationToken.None);

        await service.RemovePageAsync(session, "page_second", CancellationToken.None);

        var draft = await service.GetDraftAsync(session, CancellationToken.None);
        var remaining = Assert.Single(draft.Pages);
        Assert.Equal("page_first", remaining.Page.PageId);
        Assert.Equal(0, remaining.Page.Order);
        Assert.NotNull(await resources.GetByResourceIdAsync(second.ResourceId, CancellationToken.None));
    }

    [Fact]
    public async Task RemovingOriginalIncrementPageIsBlocked()
    {
        var templates = new MemoryTemplateRepository();
        var resources = new MemoryResourceRepository();
        var now = DateTimeOffset.UtcNow;
        var committedRevision = TemplateRevision.Restore(
            "revision_base",
            "group_remove",
            1,
            TemplateRevisionState.Committed,
            "templates/groups/group_remove/revisions/revision_base/revision-manifest.json",
            $"sha256:{FileHash}",
            now,
            [Page("page_original", "revision_base", 0, "resource_original")],
            []);
        templates.Add(
            TemplateGroup.Restore("group_remove", "增量删除", committedRevision.RevisionId,
                TemplateGroupStatus.Active, now, now),
            committedRevision);
        resources.Add(Resource("resource_original", "group_remove", "revision_base", 790, 1252));
        var service = CreateService(templates, resources);
        var session = await service.BeginIncrementAsync("group_remove", CancellationToken.None);

        var exception = await Assert.ThrowsAsync<TemplateRuleViolationException>(() =>
            service.RemovePageAsync(session, "page_original", CancellationToken.None));

        Assert.Equal("TPL_INCREMENT_ORIGINAL_PAGE_READ_ONLY", exception.Code);
    }

    [Fact]
    public async Task CommitDraftThenCreateSnapshotFreezesRevisionAndPageSelection()
    {
        var templates = new MemoryTemplateRepository();
        var resources = new MemoryResourceRepository();
        var service = CreateService(templates, resources);
        var session = await service.BeginCreateAsync("可用模板", CancellationToken.None);
        var resource = Resource("resource_commit", session, 790, 1252);
        resources.Add(resource);
        var draft = (await templates.GetRevisionAsync(session.DraftRevisionId, CancellationToken.None))!
            .AddPage(Page("page_commit", session.DraftRevisionId, 0, resource.ResourceId))
            .AddFrameBinding(Binding("frame_257", "page_commit"));
        await templates.SaveDraftRevisionAsync(draft, CancellationToken.None);

        var committed = await service.CommitDraftAsync(session, CancellationToken.None);
        var snapshot = await service.CreateBatchSnapshotAsync(
            session.GroupId,
            "batch_commit",
            ["page_commit"],
            CancellationToken.None);
        var group = await templates.GetGroupAsync(session.GroupId, CancellationToken.None);

        Assert.True(committed.IsCommitted);
        Assert.Equal(committed.RevisionId, group!.CurrentRevisionId);
        Assert.Equal(committed.RevisionId, snapshot.RevisionId);
        Assert.Equal(["page_commit"], snapshot.PageIds);
        Assert.NotEmpty(snapshot.RulesDigest);
    }

    [Fact]
    public async Task IncrementKeepsOriginalPagesReadOnlyAndMarksOnlyNewPageAffected()
    {
        var templates = new MemoryTemplateRepository();
        var resources = new MemoryResourceRepository();
        var now = new DateTimeOffset(2026, 8, 28, 1, 0, 0, TimeSpan.Zero);
        var baseRevision = TemplateRevision.Restore(
            "revision_base",
            "group_existing",
            1,
            TemplateRevisionState.Committed,
            "templates/groups/group_existing/revisions/revision_base/revision-manifest.json",
            $"sha256:{FileHash}",
            now,
            [Page("page_original", "revision_base", 0, "resource_original")],
            []);
        templates.Add(
            TemplateGroup.Restore(
                "group_existing",
                "已有模板组",
                baseRevision.RevisionId,
                TemplateGroupStatus.Active,
                now,
                now),
            baseRevision);
        resources.Add(Resource("resource_original", "group_existing", "revision_base", 790, 1252));
        var service = CreateService(templates, resources);
        var session = await service.BeginIncrementAsync("group_existing", CancellationToken.None);

        var exception = await Assert.ThrowsAsync<TemplateRuleViolationException>(() =>
            service.ConfigurePageAsync(
                session,
                "page_original",
                "不允许修改",
                TemplatePageContentTypeOption.NormalComposition,
                TemplateGenerationModeOption.OncePerProduct,
                null,
                enabled: true,
                CancellationToken.None));

        var newResource = Resource("resource_new", session, 790, 1252);
        resources.Add(newResource);
        var increment = (await templates.GetRevisionAsync(session.DraftRevisionId, CancellationToken.None))!
            .AddPage(Page("page_new", session.DraftRevisionId, 1, newResource.ResourceId));
        await templates.SaveDraftRevisionAsync(increment, CancellationToken.None);
        await service.ConfigurePageAsync(
            session,
            "page_new",
            "新增细节页",
            TemplatePageContentTypeOption.NormalComposition,
            TemplateGenerationModeOption.OncePerVariant,
            null,
            enabled: true,
            CancellationToken.None);
        var draft = await service.GetDraftAsync(session, CancellationToken.None);
        var resumed = await service.ResumeDraftAsync("group_existing", CancellationToken.None);
        var library = await service.ListAsync(CancellationToken.None);

        Assert.Equal("TPL_INCREMENT_ORIGINAL_PAGE_READ_ONLY", exception.Code);
        Assert.Equal(session.DraftRevisionId, resumed.DraftRevisionId);
        Assert.Contains("page_original", resumed.BasePageIds);
        Assert.Equal("草稿", Assert.Single(library).Status);
        Assert.Equal(2, library[0].PageCount);
        Assert.False(draft.Pages.Single(page => page.Page.PageId == "page_original").IsAffected);
        Assert.True(draft.Pages.Single(page => page.Page.PageId == "page_new").IsAffected);
        Assert.Equal("新增细节页", draft.Pages.Single(page => page.Page.PageId == "page_new").Page.Name);
    }

    [Fact]
    public async Task DraftBatchRequiresExplicitUpdateAndAnalysisStartedBatchIsFrozen()
    {
        var templates = new MemoryTemplateRepository();
        var resources = new MemoryResourceRepository();
        var now = new DateTimeOffset(2026, 8, 28, 3, 0, 0, TimeSpan.Zero);
        var fivePages = Enumerable.Range(1, 5)
            .Select(index => Page($"page_{index}", "revision_five", index - 1, $"resource_{index}"))
            .ToArray();
        var revisionFive = TemplateRevision.Restore(
            "revision_five",
            "group_snapshot",
            1,
            TemplateRevisionState.Committed,
            "templates/groups/group_snapshot/revisions/revision_five/revision-manifest.json",
            "sha256:" + new string('a', 64),
            now,
            fivePages,
            []);
        var group = TemplateGroup.Restore(
            "group_snapshot",
            "五加一模板",
            revisionFive.RevisionId,
            TemplateGroupStatus.Active,
            now,
            now);
        templates.Add(group, revisionFive);
        var service = CreateService(templates, resources);
        var oldSnapshot = await service.CreateBatchSnapshotAsync(
            group.GroupId,
            "batch_draft",
            fivePages.Select(page => page.PageId).ToArray(),
            CancellationToken.None);
        var sixPages = fivePages.Select(page => page.InheritTo("revision_six"))
            .Append(Page("page_6", "revision_six", 5, "resource_6"))
            .ToArray();
        var revisionSix = TemplateRevision.Restore(
            "revision_six",
            group.GroupId,
            2,
            TemplateRevisionState.Committed,
            "templates/groups/group_snapshot/revisions/revision_six/revision-manifest.json",
            "sha256:" + new string('b', 64),
            now.AddMinutes(1),
            sixPages,
            []);
        await templates.CommitRevisionAsync(revisionSix, revisionFive.RevisionId, CancellationToken.None);

        Assert.Equal(5, (await templates.GetForBatchAsync("batch_draft", CancellationToken.None))!.Pages.Length);
        var updated = await service.UpdateDraftBatchSnapshotAsync(
            group.GroupId,
            "batch_draft",
            sixPages.Select(page => page.PageId).ToArray(),
            analysisStarted: false,
            CancellationToken.None);
        var frozen = await Assert.ThrowsAsync<TemplateRuleViolationException>(() =>
            service.UpdateDraftBatchSnapshotAsync(
                group.GroupId,
                "batch_draft",
                sixPages.Select(page => page.PageId).ToArray(),
                analysisStarted: true,
                CancellationToken.None));

        Assert.NotEqual(oldSnapshot.SnapshotId, updated.SnapshotId);
        Assert.Equal(6, updated.PageIds.Count);
        Assert.Equal("TPL_BATCH_SNAPSHOT_FROZEN", frozen.Code);
    }

    [Fact]
    public async Task CommitMarkersKeepOldPointerAndHealMissingCommittedMarker()
    {
        var templates = new MemoryTemplateRepository();
        var resources = new MemoryResourceRepository();
        var markers = new FaultingCommitArtifacts { ManifestFailures = 1, ReadyFailures = 1 };
        var service = CreateService(templates, resources, markers);
        var session = await service.BeginCreateAsync("原子提交", CancellationToken.None);
        var resource = Resource("resource_atomic", session, 790, 1252);
        resources.Add(resource);
        var draft = (await templates.GetRevisionAsync(session.DraftRevisionId, CancellationToken.None))!
            .AddPage(Page("page_atomic", session.DraftRevisionId, 0, resource.ResourceId))
            .AddFrameBinding(Binding("frame_atomic", "page_atomic"));
        await templates.SaveDraftRevisionAsync(draft, CancellationToken.None);

        var manifestFailure = await Assert.ThrowsAsync<IOException>(() =>
            service.CommitDraftAsync(session, CancellationToken.None));
        Assert.Equal("injected manifest failure", manifestFailure.Message);
        Assert.False(markers.ReadyWritten);
        Assert.Null((await templates.GetGroupAsync(session.GroupId, CancellationToken.None))!.CurrentRevisionId);

        var readyFailure = await Assert.ThrowsAsync<TemplateRuleViolationException>(() =>
            service.CommitDraftAsync(session, CancellationToken.None));
        Assert.Equal("TPL_REVISION_COMMIT_FAILED", readyFailure.Code);
        Assert.Null((await templates.GetGroupAsync(session.GroupId, CancellationToken.None))!.CurrentRevisionId);

        templates.CommitFailures = 1;
        var sqliteFailure = await Assert.ThrowsAsync<IOException>(() =>
            service.CommitDraftAsync(session, CancellationToken.None));
        Assert.Equal("injected commit failure", sqliteFailure.Message);
        Assert.True(markers.ReadyWritten);
        Assert.Null((await templates.GetGroupAsync(session.GroupId, CancellationToken.None))!.CurrentRevisionId);

        markers.CommittedFailures = 1;
        var markerFailure = await Assert.ThrowsAsync<TemplateRuleViolationException>(() =>
            service.CommitDraftAsync(session, CancellationToken.None));
        Assert.Equal("TPL_COMMITTED_MARKER_MISSING", markerFailure.Code);
        Assert.Equal(
            session.DraftRevisionId,
            (await templates.GetGroupAsync(session.GroupId, CancellationToken.None))!.CurrentRevisionId);

        var healed = await service.CommitDraftAsync(session, CancellationToken.None);
        Assert.True(healed.IsCommitted);
        Assert.True(markers.CommittedWritten);
    }

    private static TemplateWorkspaceService CreateService(
        MemoryTemplateRepository templates,
        MemoryResourceRepository resources,
        IManifestStore? manifests = null)
    {
        var time = new FixedTimeProvider(new DateTimeOffset(2026, 8, 28, 1, 0, 0, TimeSpan.Zero));
        return new TemplateWorkspaceService(
            templates,
            resources,
            new TemplatePageImportService(
                new UnusedSourceFiles(),
                new UnusedInspectionPort(),
                resources,
                time),
            time,
            manifests);
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
            enabled: true,
            resourceId);

    private static FrameBinding Binding(string frameId, string pageId) => FrameBinding.Create(
        frameId,
        pageId,
        LayerPath.Create([2]),
        ExpectedLayerEvidence.Create(
            257,
            "frame",
            true,
            false,
            false,
            false,
            byte.MaxValue,
            "normal",
            LayerStructureSignature.Create("sha256:" + StructureHash)),
        [],
        SelectionRequirement.Create(
            "Choose one user-selected asset.",
            [SelectionCriterion.Create("asset_id", "asset_front")]),
        LayoutIntent.Create("subject_complete", 1, 0, 0, TemplateRect.Create(0, 0, 790, 1252)));

    private static ImportedTemplateResource Resource(
        string resourceId,
        TemplateDraftSession session,
        int width,
        int height) => Resource(resourceId, session.GroupId, session.DraftRevisionId, width, height);

    private static ImportedTemplateResource Resource(
        string resourceId,
        string groupId,
        string draftId,
        int width,
        int height) => new(
            resourceId,
            groupId,
            draftId,
            $"templates/groups/{groupId}/drafts/{draftId}/staging/{resourceId}/template.psd",
            3,
            FileHash,
            StructureHash,
            new PsdInspectionDocument(
                FileHash,
                3,
                new PsdCanvasInspection(width, height, 8, "RGB"),
                1,
                1,
                0,
                0,
                0,
                StructureHash,
                new PsdInspectionTiming(1, 1, 2),
                1024,
                null,
                [new PsdLayerInspection(
                    [2],
                    257,
                    "frame",
                    "smartObject",
                    false,
                    true,
                    true,
                    false,
                    100,
                    100,
                    "normal",
                    new PsdLayerBounds(0, 0, width, height),
                    false,
                    false,
                    null,
                    new PsdSmartObjectInspection("u1", "asset.psb", "embedded", "psb", "psb", 3, false, []))]),
            new DateTimeOffset(2026, 8, 28, 1, 0, 0, TimeSpan.Zero));

    private sealed class MemoryTemplateRepository : ITemplateRepository, IBatchSnapshotRepository
    {
        private readonly Dictionary<string, TemplateGroup> _groups = new(StringComparer.Ordinal);
        private readonly Dictionary<string, TemplateRevision> _revisions = new(StringComparer.Ordinal);
        private readonly Dictionary<string, CalibrationDraft> _drafts = new(StringComparer.Ordinal);
        private readonly Dictionary<string, BatchTemplateSnapshot> _snapshots = new(StringComparer.Ordinal);
        public int CommitFailures { get; set; }

        public void Add(TemplateGroup group, TemplateRevision revision)
        {
            _groups.Add(group.GroupId, group);
            _revisions.Add(revision.RevisionId, revision);
        }

        public Task<IReadOnlyList<TemplateGroup>> ListGroupsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<TemplateGroup>>(
                _groups.Values.OrderByDescending(group => group.UpdatedAt).ToArray());

        public Task SaveGroupAsync(TemplateGroup group, CancellationToken cancellationToken)
        {
            _groups[group.GroupId] = group;
            return Task.CompletedTask;
        }

        public Task<TemplateGroup?> GetGroupAsync(string groupId, CancellationToken cancellationToken)
        {
            _groups.TryGetValue(groupId, out var group);
            return Task.FromResult(group);
        }

        public Task SaveDraftRevisionAsync(TemplateRevision revision, CancellationToken cancellationToken)
        {
            _revisions[revision.RevisionId] = revision;
            return Task.CompletedTask;
        }

        public Task<TemplateRevision?> GetRevisionAsync(string revisionId, CancellationToken cancellationToken)
        {
            _revisions.TryGetValue(revisionId, out var revision);
            return Task.FromResult(revision);
        }

        public Task<TemplateRevision?> GetLatestRevisionForGroupAsync(
            string groupId,
            CancellationToken cancellationToken) => Task.FromResult(
                _revisions.Values
                    .Where(revision => string.Equals(revision.GroupId, groupId, StringComparison.Ordinal))
                    .OrderByDescending(revision => revision.RevisionNo)
                    .FirstOrDefault());

        public Task CommitRevisionAsync(
            TemplateRevision committedRevision,
            string? expectedCurrentRevisionId,
            CancellationToken cancellationToken)
        {
            if (CommitFailures > 0)
            {
                CommitFailures--;
                throw new IOException("injected commit failure");
            }
            _revisions[committedRevision.RevisionId] = committedRevision;
            var group = _groups[committedRevision.GroupId];
            _groups[group.GroupId] = group.PointTo(
                committedRevision,
                expectedCurrentRevisionId,
                committedRevision.CreatedAt);
            return Task.CompletedTask;
        }

        public Task SaveCalibrationDraftAsync(CalibrationDraft draft, CancellationToken cancellationToken)
        {
            _drafts[draft.DraftId] = draft;
            return Task.CompletedTask;
        }

        public Task<CalibrationDraft?> GetCalibrationDraftAsync(
            string draftId,
            CancellationToken cancellationToken)
        {
            _drafts.TryGetValue(draftId, out var draft);
            return Task.FromResult(draft);
        }

        public Task<IReadOnlyList<string>> GetBlockingBatchIdsAsync(
            string groupId,
            CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<string>>(
                _snapshots.Values
                    .Where(snapshot => _revisions.TryGetValue(snapshot.RevisionId, out var revision)
                        && string.Equals(revision.GroupId, groupId, StringComparison.Ordinal))
                    .Select(snapshot => snapshot.BatchId)
                    .ToArray());

        public Task DeleteGroupAsync(string groupId, CancellationToken cancellationToken)
        {
            _groups.Remove(groupId);
            foreach (var revisionId in _revisions.Values
                         .Where(revision => string.Equals(revision.GroupId, groupId, StringComparison.Ordinal))
                         .Select(revision => revision.RevisionId)
                         .ToArray())
            {
                _revisions.Remove(revisionId);
            }
            return Task.CompletedTask;
        }

        public Task SaveAsync(BatchTemplateSnapshot snapshot, CancellationToken cancellationToken)
        {
            _snapshots[snapshot.BatchId] = snapshot;
            return Task.CompletedTask;
        }

        public Task<BatchTemplateSnapshot?> GetForBatchAsync(string batchId, CancellationToken cancellationToken)
        {
            _snapshots.TryGetValue(batchId, out var snapshot);
            return Task.FromResult(snapshot);
        }

        public Task ReplaceDraftAsync(
            BatchTemplateSnapshot snapshot,
            string expectedSnapshotId,
            CancellationToken cancellationToken)
        {
            if (!_snapshots.TryGetValue(snapshot.BatchId, out var existing)
                || !string.Equals(existing.SnapshotId, expectedSnapshotId, StringComparison.Ordinal))
            {
                throw new TemplateRuleViolationException("TPL_BATCH_SNAPSHOT_FROZEN");
            }
            _snapshots[snapshot.BatchId] = snapshot;
            return Task.CompletedTask;
        }
    }

    private sealed class MemoryResourceRepository : ITemplateResourceRepository
    {
        private readonly Dictionary<string, ImportedTemplateResource> _resources = new(StringComparer.Ordinal);
        private readonly Dictionary<string, ImportedTemplateResource> _commands = new(StringComparer.Ordinal);

        public void Add(params ImportedTemplateResource[] resources)
        {
            foreach (var resource in resources)
            {
                _resources[resource.ResourceId] = resource;
            }
        }

        public Task<ImportedTemplateResource?> GetByResourceIdAsync(
            string resourceId,
            CancellationToken cancellationToken)
        {
            _resources.TryGetValue(resourceId, out var resource);
            return Task.FromResult(resource);
        }

        public Task<ImportedTemplateResource?> GetByCommandIdAsync(
            string commandId,
            CancellationToken cancellationToken)
        {
            _commands.TryGetValue(commandId, out var resource);
            return Task.FromResult(resource);
        }

        public Task<IReadOnlyList<ImportedTemplateResource>> FindBySha256Async(
            string sha256,
            CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<ImportedTemplateResource>>(
                _resources.Values.Where(resource => string.Equals(
                    resource.Sha256,
                    sha256,
                    StringComparison.OrdinalIgnoreCase)).ToArray());

        public Task<ImportedTemplateResource> SaveImportedAsync(
            string commandId,
            ImportedTemplateResource resource,
            CancellationToken cancellationToken)
        {
            _resources[resource.ResourceId] = resource;
            _commands[commandId] = resource;
            return Task.FromResult(resource);
        }

        public Task<ImportedTemplateResource> RecordExistingSelectionAsync(
            string commandId,
            ImportedTemplateResource resource,
            CancellationToken cancellationToken)
        {
            _commands[commandId] = resource;
            return Task.FromResult(resource);
        }
    }

    private sealed class UnusedSourceFiles : ITemplateSourceFilePort
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

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    private sealed class FaultingCommitArtifacts : IManifestStore, IRevisionCommitMarkerStore
    {
        private RevisionCommitMarker? _ready;
        private RevisionCommitMarker? _committed;
        public int ManifestFailures { get; set; }
        public int ReadyFailures { get; set; }
        public int CommittedFailures { get; set; }
        public bool ReadyWritten => _ready is not null;
        public bool CommittedWritten => _committed is not null;

        public Task SaveAsync(string relativePath, AppManifest manifest, CancellationToken cancellationToken)
        {
            if (ManifestFailures > 0)
            {
                ManifestFailures--;
                throw new IOException("injected manifest failure");
            }
            return Task.CompletedTask;
        }

        public Task<AppManifest?> LoadAsync(string relativePath, CancellationToken cancellationToken) =>
            Task.FromResult<AppManifest?>(null);

        public Task WriteReadyMarkerAsync(
            string revisionDirectory,
            RevisionCommitMarker marker,
            CancellationToken cancellationToken)
        {
            if (ReadyFailures > 0)
            {
                ReadyFailures--;
                throw new IOException("injected ready marker failure");
            }
            _ready = marker;
            return Task.CompletedTask;
        }

        public Task WriteCommittedMarkerAsync(
            string revisionDirectory,
            RevisionCommitMarker marker,
            CancellationToken cancellationToken)
        {
            if (CommittedFailures > 0)
            {
                CommittedFailures--;
                throw new IOException("injected committed marker failure");
            }
            _committed = marker;
            return Task.CompletedTask;
        }

        public Task<RevisionCommitMarker?> ReadMarkerAsync(
            string revisionDirectory,
            string markerName,
            CancellationToken cancellationToken) => Task.FromResult(
                markerName == "ready.marker" ? _ready : _committed);
    }
}
