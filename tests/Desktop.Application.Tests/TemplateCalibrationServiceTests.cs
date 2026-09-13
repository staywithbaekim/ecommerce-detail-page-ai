using Desktop.Application.Psd;
using Desktop.Application.Templates;
using Desktop.Domain.Templates;

namespace Desktop.Application.Tests;

public sealed class TemplateCalibrationServiceTests
{
    [Fact]
    public async Task RegisteredSmartObjectCanBindMoveUndoAndRedo()
    {
        var now = new DateTimeOffset(2026, 8, 28, 2, 0, 0, TimeSpan.Zero);
        var group = TemplateGroup.Create("group_cal", "Calibration", now);
        var revision = TemplateRevision.CreateDraft(
            "revision_cal",
            group.GroupId,
            1,
            now,
            [TemplatePage.Create(
                "page_cal",
                "revision_cal",
                0,
                "单框页",
                TemplatePageContentType.NormalComposition,
                TemplateGenerationMode.OncePerProduct,
                null,
                enabled: true,
                "resource_cal")]);
        var templates = new MemoryTemplateRepository(group, revision);
        var resources = new MemoryResourceRepository(Resource());
        var service = new TemplateCalibrationService(templates, resources);
        var session = new TemplateDraftSession(
            group.GroupId,
            group.Name,
            revision.RevisionId,
            revision.RevisionNo,
            new HashSet<string>(StringComparer.Ordinal));

        var initial = await service.LoadAsync(session, "page_cal", CancellationToken.None);
        var frameId = Assert.Single(initial.Frames).FrameId;
        var bound = await service.BindAssetAsync(session, "page_cal", frameId, "asset_front", CancellationToken.None);
        var moved = await service.SetGeometryAsync(session, "page_cal", frameId, 1.4, 12, -8, CancellationToken.None);
        var undone = await service.ApplyHistoryAsync(session, "page_cal", CalibrationUndoAction.Undo, CancellationToken.None);
        var noOpAfterUndo = await service.SetGeometryAsync(session, "page_cal", frameId, 1, 0, 0, CancellationToken.None);
        var redone = await service.ApplyHistoryAsync(session, "page_cal", CalibrationUndoAction.Redo, CancellationToken.None);
        var cleared = await service.ClearAssetAsync(session, "page_cal", frameId, CancellationToken.None);
        var bindingCountAfterClear = (await templates.GetRevisionAsync(
            revision.RevisionId,
            CancellationToken.None))!.FrameBindings.Length;
        var restored = await service.ApplyHistoryAsync(session, "page_cal", CalibrationUndoAction.Undo, CancellationToken.None);
        var bindingCountAfterRestore = (await templates.GetRevisionAsync(
            revision.RevisionId,
            CancellationToken.None))!.FrameBindings.Length;

        Assert.False(initial.IsBlocked);
        Assert.Equal("asset_front", bound.SelectedAssetId);
        Assert.True(moved.CanUndo);
        Assert.True(undone.CanRedo);
        Assert.True(noOpAfterUndo.CanRedo);
        Assert.Equal("asset_front", redone.SelectedAssetId);
        Assert.False(cleared.Frames.Single().IsRegistered);
        Assert.Null(cleared.SelectedAssetId);
        Assert.Equal(0, bindingCountAfterClear);
        Assert.True(restored.Frames.Single().IsRegistered);
        Assert.Equal("asset_front", restored.SelectedAssetId);
        Assert.Equal(1, bindingCountAfterRestore);
        Assert.Equal(1.4, moved.CanUndo ? 1.4 : 0);
    }

    [Fact]
    public async Task RebindingAssetPreservesExplicitRuleAndNeverPersistsSelectionPromptAsRule()
    {
        var now = new DateTimeOffset(2026, 8, 31, 12, 0, 0, TimeSpan.Zero);
        var group = TemplateGroup.Create("group_rule", "Rule", now);
        var revision = TemplateRevision.CreateDraft(
            "revision_rule",
            group.GroupId,
            1,
            now,
            [TemplatePage.Create(
                "page_rule",
                "revision_rule",
                0,
                "主图页",
                TemplatePageContentType.NormalComposition,
                TemplateGenerationMode.OncePerProduct,
                null,
                enabled: true,
                "resource_cal")]);
        var templates = new MemoryTemplateRepository(group, revision);
        var service = new TemplateCalibrationService(
            templates,
            new MemoryResourceRepository(Resource()));
        var session = new TemplateDraftSession(
            group.GroupId,
            group.Name,
            revision.RevisionId,
            revision.RevisionNo,
            new HashSet<string>(StringComparer.Ordinal));
        var frameId = Assert.Single((await service.LoadAsync(
            session,
            "page_rule",
            CancellationToken.None)).Frames).FrameId;

        var first = await service.BindAssetAsync(
            session,
            "page_rule",
            frameId,
            "asset_first",
            CancellationToken.None);
        Assert.Equal("商品主体清晰完整，构图适合当前图片框。", first.SelectionRequirement);

        await service.SetSelectionRequirementAsync(
            session,
            "page_rule",
            frameId,
            "正面全景图，商品主体完整。",
            CancellationToken.None);
        var rebound = await service.BindAssetAsync(
            session,
            "page_rule",
            frameId,
            "asset_second",
            CancellationToken.None);
        var persisted = Assert.Single((await templates.GetRevisionAsync(
            revision.RevisionId,
            CancellationToken.None))!.FrameBindings);

        Assert.Equal("正面全景图，商品主体完整。", rebound.SelectionRequirement);
        Assert.Equal("正面全景图，商品主体完整。", persisted.SelectionRequirement.Description);
        Assert.Equal("asset_second", Assert.Single(persisted.SelectionRequirement.Criteria).Value);
    }

    [Fact]
    public async Task MissingStructureEvidenceBlocksCalibration()
    {
        var now = DateTimeOffset.UtcNow;
        var group = TemplateGroup.Create("group_block", "Blocked", now);
        var revision = TemplateRevision.CreateDraft(
            "revision_block",
            group.GroupId,
            1,
            now,
            [TemplatePage.Create(
                "page_block",
                "revision_block",
                0,
                "无框页",
                TemplatePageContentType.NormalComposition,
                TemplateGenerationMode.OncePerProduct,
                null,
                enabled: true,
                "resource_block")]);
        var resources = new MemoryResourceRepository(Resource("resource_block", smartObject: false));
        var service = new TemplateCalibrationService(
            new MemoryTemplateRepository(group, revision),
            resources);
        var session = new TemplateDraftSession(
            group.GroupId,
            group.Name,
            revision.RevisionId,
            revision.RevisionNo,
            new HashSet<string>(StringComparer.Ordinal));

        var result = await service.LoadAsync(session, "page_block", CancellationToken.None);

        Assert.True(result.IsBlocked);
        Assert.Empty(result.Frames);
    }

    [Fact]
    public async Task ReadOnlySourceAssetIsRegisteredWithOpaqueIdAndLocalReference()
    {
        var now = DateTimeOffset.UtcNow;
        var group = TemplateGroup.Create("group_cal", "Calibration", now);
        var revision = TemplateRevision.CreateDraft(
            "revision_cal",
            group.GroupId,
            1,
            now,
            [TemplatePage.Create(
                "page_cal",
                "revision_cal",
                0,
                "单框页",
                TemplatePageContentType.NormalComposition,
                TemplateGenerationMode.OncePerProduct,
                null,
                enabled: true,
                "resource_cal")]);
        var service = new TemplateCalibrationService(
            new MemoryTemplateRepository(group, revision),
            new MemoryResourceRepository(Resource()),
            assetSources: new FixedAssetSourcePort());
        var session = new TemplateDraftSession(
            group.GroupId,
            group.Name,
            revision.RevisionId,
            revision.RevisionNo,
            new HashSet<string>(StringComparer.Ordinal));
        var initial = await service.LoadAsync(session, "page_cal", CancellationToken.None);

        var result = await service.BindReadOnlyAssetAsync(
            session,
            "page_cal",
            Assert.Single(initial.Frames).FrameId,
            @"C:\readonly\代表款.png",
            CancellationToken.None);

        var asset = Assert.Single(result.Assets);
        Assert.Equal("asset_hash", asset.AssetId);
        Assert.Equal("代表款.png", asset.DisplayName);
        Assert.Equal(@"C:\readonly\代表款.png", asset.SourceReference);
    }

    [Fact]
    public async Task BindingOneCandidateDoesNotRegisterEverySmartObject()
    {
        var now = DateTimeOffset.UtcNow;
        var group = TemplateGroup.Create("group_cal", "Calibration", now);
        var revision = TemplateRevision.CreateDraft(
            "revision_cal",
            group.GroupId,
            1,
            now,
            [TemplatePage.Create(
                "page_cal",
                "revision_cal",
                0,
                "双候选页",
                TemplatePageContentType.NormalComposition,
                TemplateGenerationMode.OncePerProduct,
                null,
                enabled: true,
                "resource_cal")]);
        var service = new TemplateCalibrationService(
            new MemoryTemplateRepository(group, revision),
            new MemoryResourceRepository(ResourceWithTwoSmartObjects()));
        var session = new TemplateDraftSession(
            group.GroupId,
            group.Name,
            revision.RevisionId,
            revision.RevisionNo,
            new HashSet<string>(StringComparer.Ordinal));
        var initial = await service.LoadAsync(session, "page_cal", CancellationToken.None);

        var result = await service.BindAssetAsync(
            session,
            "page_cal",
            initial.Frames[0].FrameId,
            "asset_selected",
            CancellationToken.None);

        Assert.Equal(2, result.Frames.Count);
        Assert.True(result.Frames[0].IsRegistered);
        Assert.False(result.Frames[1].IsRegistered);
    }

    [Fact]
    public async Task MaskedCandidateIsShownButLocalBindingIsBlockedInsteadOfGuessingGeometry()
    {
        var now = DateTimeOffset.UtcNow;
        var group = TemplateGroup.Create("group_cal", "Calibration", now);
        var revision = TemplateRevision.CreateDraft(
            "revision_cal",
            group.GroupId,
            1,
            now,
            [TemplatePage.Create(
                "page_cal",
                "revision_cal",
                0,
                "蒙版页",
                TemplatePageContentType.NormalComposition,
                TemplateGenerationMode.OncePerProduct,
                null,
                enabled: true,
                "resource_cal")]);
        var baseResource = Resource();
        var maskedLayer = baseResource.Inspection.Layers[0] with { HasMask = true };
        var maskedResource = baseResource with
        {
            Inspection = baseResource.Inspection with { Layers = [maskedLayer], MaskCount = 1 }
        };
        var service = new TemplateCalibrationService(
            new MemoryTemplateRepository(group, revision),
            new MemoryResourceRepository(maskedResource));
        var session = new TemplateDraftSession(
            group.GroupId,
            group.Name,
            revision.RevisionId,
            revision.RevisionNo,
            new HashSet<string>(StringComparer.Ordinal));
        var initial = await service.LoadAsync(session, "page_cal", CancellationToken.None);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => service.BindAssetAsync(
            session,
            "page_cal",
            Assert.Single(initial.Frames).FrameId,
            "asset_selected",
            CancellationToken.None));

        Assert.True(initial.IsBlocked);
        Assert.Equal("TPL_PSD_UNSUPPORTED_STRUCTURE", exception.Message);
    }

    [Fact]
    public async Task IncrementalBasePageCanBeInspectedButCannotBeRecalibrated()
    {
        var now = DateTimeOffset.UtcNow;
        var group = TemplateGroup.Create("group_cal", "Calibration", now);
        var revision = TemplateRevision.CreateDraft(
            "revision_cal",
            group.GroupId,
            2,
            now,
            [TemplatePage.Create(
                "page_cal",
                "revision_cal",
                0,
                "原页面",
                TemplatePageContentType.NormalComposition,
                TemplateGenerationMode.OncePerProduct,
                null,
                enabled: true,
                "resource_cal")]);
        var service = new TemplateCalibrationService(
            new MemoryTemplateRepository(group, revision),
            new MemoryResourceRepository(Resource()));
        var session = new TemplateDraftSession(
            group.GroupId,
            group.Name,
            revision.RevisionId,
            revision.RevisionNo,
            new HashSet<string>(StringComparer.Ordinal) { "page_cal" });
        var initial = await service.LoadAsync(session, "page_cal", CancellationToken.None);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => service.BindAssetAsync(
            session,
            "page_cal",
            Assert.Single(initial.Frames).FrameId,
            "asset_selected",
            CancellationToken.None));

        Assert.True(initial.IsReadOnly);
        Assert.Equal("TPL_INCREMENT_ORIGINAL_PAGE_READ_ONLY", exception.Message);
    }

    [Fact]
    public async Task ClearingLegacyEmptyCalibrationBindingRemovesPersistedFrameRule()
    {
        var now = DateTimeOffset.UtcNow;
        var group = TemplateGroup.Create("group_cal", "Calibration", now);
        var revision = TemplateRevision.CreateDraft(
                "revision_cal",
                group.GroupId,
                1,
                now,
                [TemplatePage.Create(
                    "page_cal",
                    "revision_cal",
                    0,
                    "旧草稿页",
                    TemplatePageContentType.NormalComposition,
                    TemplateGenerationMode.OncePerProduct,
                    null,
                    enabled: true,
                    "resource_cal")])
            .AddFrameBinding(BindingWithoutAsset());
        var templates = new MemoryTemplateRepository(group, revision);
        var service = new TemplateCalibrationService(
            templates,
            new MemoryResourceRepository(Resource()));
        var session = new TemplateDraftSession(
            group.GroupId,
            group.Name,
            revision.RevisionId,
            revision.RevisionNo,
            new HashSet<string>(StringComparer.Ordinal));

        var initial = await service.LoadAsync(session, "page_cal", CancellationToken.None);
        var frame = Assert.Single(initial.Frames);
        var cleared = await service.ClearAssetAsync(
            session,
            "page_cal",
            frame.FrameId,
            CancellationToken.None);
        var persisted = await templates.GetRevisionAsync(revision.RevisionId, CancellationToken.None);

        Assert.False(frame.IsRegistered);
        Assert.True(frame.HasPersistedBinding);
        Assert.False(Assert.Single(cleared.Frames).HasPersistedBinding);
        Assert.Empty(persisted!.FrameBindings);
    }

    [Fact]
    public async Task LoadingEditDraftSelectsPersistedRuleAndShowsItsDescription()
    {
        var now = DateTimeOffset.UtcNow;
        var group = TemplateGroup.Create("group_rule_view", "Rule view", now);
        var revision = TemplateRevision.CreateDraft(
                "revision_rule_view",
                group.GroupId,
                1,
                now,
                [TemplatePage.Create(
                    "page_cal",
                    "revision_rule_view",
                    0,
                    "规则页",
                    TemplatePageContentType.NormalComposition,
                    TemplateGenerationMode.OncePerProduct,
                    null,
                    enabled: true,
                    "resource_cal")])
            .AddFrameBinding(FrameBinding.Create(
                "frame_257",
                "page_cal",
                LayerPath.Create([2]),
                ExpectedLayerEvidence.Create(
                    257,
                    "frame",
                    visible: true,
                    clipping: false,
                    userMask: false,
                    vectorMask: false,
                    100,
                    "normal",
                    LayerStructureSignature.Create("sha256:" + new string('b', 64))),
                [],
                SelectionRequirement.Create(
                    "完整正面图，商品主体完整。",
                    [SelectionCriterion.Create("asset_id", "reference_asset")]),
                LayoutIntent.Create(
                    "subject_complete",
                    1.2,
                    4,
                    -3,
                    TemplateRect.Create(0, 0, 790, 1252))));
        var templates = new MemoryTemplateRepository(group, revision);
        var service = new TemplateCalibrationService(
            templates,
            new MemoryResourceRepository(Resource()));
        var session = new TemplateDraftSession(
            group.GroupId,
            group.Name,
            revision.RevisionId,
            revision.RevisionNo,
            new HashSet<string>(StringComparer.Ordinal));

        var view = await service.LoadAsync(session, "page_cal", CancellationToken.None);

        Assert.Equal("frame_257", view.SelectedFrameId);
        Assert.Equal("完整正面图，商品主体完整。", view.SelectionRequirement);
        Assert.True(Assert.Single(view.Frames).HasPersistedBinding);
        Assert.True(Assert.Single(view.Frames).IsRegistered);
        Assert.Equal(1.2, view.SelectedScale);
        Assert.Equal(4, view.SelectedOffsetX);
        Assert.Equal(-3, view.SelectedOffsetY);
    }

    [Fact]
    public async Task LoadingReferenceLearningBindingMatchesItsPsdEvidenceAndKeepsItEditable()
    {
        var now = DateTimeOffset.UtcNow;
        var group = TemplateGroup.Create("group_learning_rule", "Learning rule", now);
        var revision = TemplateRevision.CreateDraft(
                "revision_learning_rule",
                group.GroupId,
                1,
                now,
                [TemplatePage.Create(
                    "page_cal",
                    "revision_learning_rule",
                    0,
                    "学习页",
                    TemplatePageContentType.NormalComposition,
                    TemplateGenerationMode.OncePerProduct,
                    null,
                    enabled: true,
                    "resource_cal")])
            .AddFrameBinding(FrameBinding.Create(
                "learn_frame_257_2",
                "page_cal",
                LayerPath.Create([2]),
                ExpectedLayerEvidence.Create(
                    257,
                    "frame",
                    visible: true,
                    clipping: false,
                    userMask: false,
                    vectorMask: false,
                    100,
                    "normal",
                    LayerStructureSignature.Create("sha256:" + new string('b', 64))),
                [],
                SelectionRequirement.Create(
                    "商品展示图，与参考款保持同一正面视角。",
                    [
                        SelectionCriterion.Create("asset_id", "asset_reference"),
                        SelectionCriterion.Create("reference_only", "true"),
                        SelectionCriterion.Create("reference_file_name", "front.jpg")
                    ]),
                LayoutIntent.Create(
                    "subject_cover",
                    1.2,
                    4,
                    -3,
                    TemplateRect.Create(0, 0, 790, 1252))));
        var templates = new MemoryTemplateRepository(group, revision);
        var service = new TemplateCalibrationService(
            templates,
            new MemoryResourceRepository(Resource()));
        var session = new TemplateDraftSession(
            group.GroupId,
            group.Name,
            revision.RevisionId,
            revision.RevisionNo,
            new HashSet<string>(StringComparer.Ordinal));

        var view = await service.LoadAsync(session, "page_cal", CancellationToken.None);

        Assert.Equal("frame_257", view.SelectedFrameId);
        Assert.Equal("asset_reference", view.SelectedAssetId);
        Assert.Equal("商品展示图，与参考款保持同一正面视角。", view.SelectionRequirement);
        Assert.Equal(1.2, view.SelectedScale);
        Assert.Equal(4, view.SelectedOffsetX);
        Assert.Equal(-3, view.SelectedOffsetY);
        Assert.Equal("front.jpg", Assert.Single(view.Assets).DisplayName);
        Assert.True(Assert.Single(view.Frames).IsRegistered);

        var edited = await service.SetSelectionRequirementAsync(
            session,
            "page_cal",
            "frame_257",
            "商品展示图，与目标款保持同一正面视角。",
            CancellationToken.None);
        var persisted = await templates.GetRevisionAsync(revision.RevisionId, CancellationToken.None);

        Assert.Equal("商品展示图，与目标款保持同一正面视角。", edited.SelectionRequirement);
        Assert.Equal("learn_frame_257_2", Assert.Single(persisted!.FrameBindings).FrameId);
        Assert.Equal(
            "商品展示图，与目标款保持同一正面视角。",
            Assert.Single(persisted.FrameBindings).SelectionRequirement.Description);
        Assert.Contains(
            Assert.Single(persisted.FrameBindings).SelectionRequirement.Criteria,
            item => item.Key == "reference_only" && item.Value == "true");
    }

    [Fact]
    public async Task LoadedFramesKeepIndependentAssetRuleAndGeometryProjections()
    {
        var now = DateTimeOffset.UtcNow;
        var group = TemplateGroup.Create("group_frame_projection", "Frame projection", now);
        var revision = TemplateRevision.CreateDraft(
                "revision_frame_projection",
                group.GroupId,
                1,
                now,
                [TemplatePage.Create(
                    "page_cal",
                    "revision_frame_projection",
                    0,
                    "多框页",
                    TemplatePageContentType.NormalComposition,
                    TemplateGenerationMode.OncePerProduct,
                    null,
                    enabled: true,
                    "resource_cal")])
            .AddFrameBinding(CreateBinding(
                "frame_257",
                257,
                [2],
                "frame",
                "asset_front",
                "商品正面图",
                1.1,
                3,
                -2))
            .AddFrameBinding(CreateBinding(
                "frame_258",
                258,
                [3],
                "decorative_smart_object",
                "asset_sleeve",
                "袖口细节图",
                1.45,
                -9,
                7));
        var service = new TemplateCalibrationService(
            new MemoryTemplateRepository(group, revision),
            new MemoryResourceRepository(ResourceWithTwoSmartObjects()));
        var session = new TemplateDraftSession(
            group.GroupId,
            group.Name,
            revision.RevisionId,
            revision.RevisionNo,
            new HashSet<string>(StringComparer.Ordinal));

        var view = await service.LoadAsync(session, "page_cal", CancellationToken.None);

        var front = Assert.Single(view.Frames, frame => frame.FrameId == "frame_257");
        Assert.Equal("asset_front", front.BoundAssetId);
        Assert.Equal("商品正面图", front.SelectionRequirement);
        Assert.Equal(1.1, front.Scale);
        Assert.Equal(3, front.OffsetX);
        Assert.Equal(-2, front.OffsetY);

        var sleeve = Assert.Single(view.Frames, frame => frame.FrameId == "frame_258");
        Assert.Equal("asset_sleeve", sleeve.BoundAssetId);
        Assert.Equal("袖口细节图", sleeve.SelectionRequirement);
        Assert.Equal(1.45, sleeve.Scale);
        Assert.Equal(-9, sleeve.OffsetX);
        Assert.Equal(7, sleeve.OffsetY);
    }

    private static ImportedTemplateResource Resource(string id = "resource_cal", bool smartObject = true) => new(
        id,
        "group_cal",
        "revision_cal",
        $"templates/groups/group_cal/drafts/revision_cal/staging/{id}/template.psd",
        10,
        new string('a', 64),
        "sha256:" + new string('b', 64),
        new PsdInspectionDocument(
            new string('a', 64),
            10,
            new PsdCanvasInspection(790, 1252, 8, "RGB"),
            smartObject ? 1 : 0,
            smartObject ? 1 : 0,
            0,
            0,
            0,
            new string('b', 64),
            new PsdInspectionTiming(1, 2, 3),
            1024,
            null,
            smartObject
                ? [new PsdLayerInspection(
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
                    new PsdLayerBounds(0, 0, 790, 1252),
                    false,
                    false,
                    null,
                    new PsdSmartObjectInspection("u1", "asset.psb", "embedded", "psb", "psb", 10, false, []))]
                : []),
        DateTimeOffset.UtcNow);

    private static FrameBinding BindingWithoutAsset() => FrameBinding.Create(
        "frame_257",
        "page_cal",
        LayerPath.Create([2]),
        ExpectedLayerEvidence.Create(
            257,
            "frame",
            visible: true,
            clipping: false,
            userMask: false,
            vectorMask: false,
            100,
            "normal",
            LayerStructureSignature.Create("sha256:" + new string('b', 64))),
        [],
        SelectionRequirement.Create("请选择一张代表款商品图放入当前图片框。"),
        LayoutIntent.Create(
            "subject_complete",
            1,
            0,
            0,
            TemplateRect.Create(0, 0, 790, 1252)));

    private static FrameBinding CreateBinding(
        string frameId,
        int layerId,
        IReadOnlyList<int> layerPath,
        string layerName,
        string assetId,
        string requirement,
        double scale,
        double offsetX,
        double offsetY) => FrameBinding.Create(
        frameId,
        "page_cal",
        LayerPath.Create(layerPath),
        ExpectedLayerEvidence.Create(
            layerId,
            layerName,
            visible: true,
            clipping: false,
            userMask: false,
            vectorMask: false,
            100,
            "normal",
            LayerStructureSignature.Create("sha256:" + new string('b', 64))),
        [],
        SelectionRequirement.Create(
            requirement,
            [SelectionCriterion.Create("asset_id", assetId)]),
        LayoutIntent.Create(
            "subject_complete",
            scale,
            offsetX,
            offsetY,
            TemplateRect.Create(0, 0, 790, 1252)));

    private static ImportedTemplateResource ResourceWithTwoSmartObjects()
    {
        var first = Resource();
        var second = first.Inspection.Layers[0] with
        {
            Path = [3],
            LayerId = 258,
            Name = "decorative_smart_object",
            SmartObject = first.Inspection.Layers[0].SmartObject! with { UniqueId = "u2" }
        };
        return first with
        {
            Inspection = first.Inspection with
            {
                SmartObjectCount = 2,
                Layers = [first.Inspection.Layers[0], second]
            }
        };
    }

    private sealed class MemoryTemplateRepository(TemplateGroup group, TemplateRevision revision) : ITemplateRepository
    {
        private TemplateRevision _revision = revision;

        public Task<IReadOnlyList<TemplateGroup>> ListGroupsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<TemplateGroup>>([group]);
        public Task SaveGroupAsync(TemplateGroup value, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<TemplateGroup?> GetGroupAsync(string groupId, CancellationToken cancellationToken) => Task.FromResult<TemplateGroup?>(group);
        public Task SaveDraftRevisionAsync(TemplateRevision value, CancellationToken cancellationToken)
        {
            _revision = value;
            return Task.CompletedTask;
        }
        public Task<TemplateRevision?> GetRevisionAsync(string revisionId, CancellationToken cancellationToken) => Task.FromResult<TemplateRevision?>(_revision);
        public Task<TemplateRevision?> GetLatestRevisionForGroupAsync(string groupId, CancellationToken cancellationToken) => Task.FromResult<TemplateRevision?>(_revision);
        public Task CommitRevisionAsync(TemplateRevision value, string? expectedCurrentRevisionId, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task SaveCalibrationDraftAsync(CalibrationDraft draft, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<CalibrationDraft?> GetCalibrationDraftAsync(string draftId, CancellationToken cancellationToken) => Task.FromResult<CalibrationDraft?>(null);
        public Task<IReadOnlyList<string>> GetBlockingBatchIdsAsync(string groupId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<string>>([]);
        public Task DeleteGroupAsync(string groupId, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class MemoryResourceRepository(ImportedTemplateResource resource) : ITemplateResourceRepository
    {
        public Task<ImportedTemplateResource?> GetByResourceIdAsync(string resourceId, CancellationToken cancellationToken) => Task.FromResult<ImportedTemplateResource?>(resource);
        public Task<ImportedTemplateResource?> GetByCommandIdAsync(string commandId, CancellationToken cancellationToken) => Task.FromResult<ImportedTemplateResource?>(null);
        public Task<IReadOnlyList<ImportedTemplateResource>> FindBySha256Async(string sha256, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<ImportedTemplateResource>>([resource]);
        public Task<ImportedTemplateResource> SaveImportedAsync(string commandId, ImportedTemplateResource value, CancellationToken cancellationToken) => Task.FromResult(value);
        public Task<ImportedTemplateResource> RecordExistingSelectionAsync(string commandId, ImportedTemplateResource value, CancellationToken cancellationToken) => Task.FromResult(value);
    }

    private sealed class FixedAssetSourcePort : ICalibrationAssetSourcePort
    {
        public Task<TemplateCalibrationAsset> InspectReadOnlyAsync(
            string sourcePath,
            CancellationToken cancellationToken) => Task.FromResult(new TemplateCalibrationAsset(
                "asset_hash",
                "代表款.png",
                sourcePath,
                1200,
                1600));
    }
}
