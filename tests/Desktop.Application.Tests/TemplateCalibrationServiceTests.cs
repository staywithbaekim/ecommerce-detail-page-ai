using Desktop.Application.Psd;
using Desktop.Application.Templates;
using Desktop.Domain.Templates;

namespace Desktop.Application.Tests;

public sealed class TemplateCalibrationServiceTests
{
    [Theory]
    [InlineData("covered", true)]
    [InlineData("one_pixel", true)]
    [InlineData("contributing", false)]
    [InlineData("other_window", false)]
    [InlineData("other_parent", false)]
    [InlineData("gap", false)]
    [InlineData("missing_evidence", false)]
    [InlineData("linked", false)]
    [InlineData("nested_psd", false)]
    [InlineData("masked", false)]
    [InlineData("small_unrelated_image", false)]
    [InlineData("partial_target", false)]
    [InlineData("translucent_target", false)]
    [InlineData("smart_target", true)]
    [InlineData("unproven_smart_target", false)]
    public async Task ImageRulePersistsOnlyCoveredConsecutivePhotosInItsExplicitWindow(string mutation, bool expected)
    {
        var now = DateTimeOffset.UtcNow;
        var group = TemplateGroup.Create("group_cal", "Clipped stack", now);
        var revision = TemplateRevision.CreateDraft("revision_cal", group.GroupId, 1, now,
            [TemplatePage.Create("page_cal", "revision_cal", 0, "图片页", TemplatePageContentType.NormalComposition,
                TemplateGenerationMode.OncePerProduct, null, true, "resource_cal")]);
        var templates = new MemoryTemplateRepository(group, revision);
        var resource = Resource();
        var window = new PsdClippingBaseInspection([0, 0], 800, "window", "pixel", true, true,
            new(10, 20, 110, 120), false, false, 255, "norm");
        var target = resource.Inspection.Layers[0] with { Kind = "pixel", SmartObject = null, LayerId = 900, Path = [0, 2],
            Bounds = new(-10, -10, 150, 160), Clipping = true, ClippingBase = window, Opacity = 255, FillOpacity = 255, BlendMode = "norm",
            PixelEvidence = new(true, 10000, 9000, window.Bounds) { RasterImage = new(true, 500, .2) } };
        var old = target with { Kind = "smartobject", LayerId = 850, Path = [0, 1],
            SmartObject = resource.Inspection.Layers[0].SmartObject! with { Kind = "data", DetectedFileType = "jpg", IsPsd = false,
                EmbeddedContentAvailable = true, EmbeddedContentSha256 = new string('d', 64),
                TransformBox = [-10, -10, 150, -10, 150, 160, -10, 160] },
            PixelEvidence = new(true, 10000, mutation == "one_pixel" ? 1 : 0, null) };
        if (mutation == "contributing") old = old with { PixelEvidence = old.PixelEvidence with { CompositeContributionPixelCount = 100 } };
        if (mutation == "other_window") old = old with { ClippingBase = window with { LayerId = 801, Path = [1, 0] } };
        if (mutation == "other_parent") old = old with { Path = [1, 1] };
        if (mutation == "gap") old = old with { Path = [0, 0] };
        if (mutation == "missing_evidence") old = old with { PixelEvidence = old.PixelEvidence with { Available = false } };
        if (mutation == "linked") old = old with { SmartObject = old.SmartObject! with { Kind = "external" } };
        if (mutation == "nested_psd") old = old with { SmartObject = old.SmartObject! with { IsPsd = true } };
        if (mutation == "masked") old = old with { HasMask = true };
        if (mutation == "small_unrelated_image") old = old with { Bounds = new(10, 20, 20, 30) };
        if (mutation == "partial_target") target = target with { Bounds = new(10, 20, 100, 120) };
        if (mutation == "translucent_target") target = target with { Opacity = 100 };
        if (mutation == "smart_target") target = target with { Kind = "smartobject", SmartObject = old.SmartObject };
        if (mutation == "unproven_smart_target") target = target with { Kind = "smartobject", SmartObject = old.SmartObject! with { EmbeddedContentAvailable = false } };
        resource = resource with { Inspection = resource.Inspection with { LayerCount = 2, Layers = [old, target] } };
        var service = new TemplateCalibrationService(templates, new MemoryResourceRepository(resource));
        var session = new TemplateDraftSession(group.GroupId, group.Name, revision.RevisionId, 1, new HashSet<string>());
        var view = await service.LoadAsync(session, "page_cal", default);
        var frame = view.Frames.Single(f => f.LayerId == target.LayerId);
        Assert.Equal(expected ? 1 : 0, frame.SupersededLayers.Count);
        await service.SetSelectionRequirementAsync(session, "page_cal", frame.FrameId, "商品正面图", default);
        var binding = Assert.Single((await templates.GetRevisionAsync(revision.RevisionId, default))!.FrameBindings);
        Assert.Equal(expected ? 1 : 0, binding.SupersededLayers.Length);
        if (expected) Assert.Equal<int>([1, 2], Assert.Single(binding.SupersededLayers).LayerPath.Segments);
        var reopened = await new TemplateCalibrationService(templates, new MemoryResourceRepository(resource)).LoadAsync(session, "page_cal", default);
        Assert.Equal(expected ? 1 : 0, reopened.Frames.Single(f => f.LayerId == target.LayerId).SupersededLayers.Count);
    }

    [Theory]
    [InlineData(1, 1, 1, false)]
    [InlineData(2, 100, 100, false)]
    [InlineData(10, 10, 63, false)]
    [InlineData(100, 100, 100, false)]
    [InlineData(10, 10, 64, true)]
    [InlineData(10, 10, 100, true)]
    public async Task RasterCandidateAndProductionEvidenceUseTheSameResidualGate(int width, int height, int count, bool allowed)
    {
        var resource = Resource();
        var layer = resource.Inspection.Layers[0] with
        {
            Kind = "pixel", SmartObject = null,
            PixelEvidence = new(true, 10000, count, new(0, 0, width, height)) { RasterImage = new(true, 500, .2) }
        };
        Assert.Equal(allowed, layer.PixelEvidence.HasReplaceableRasterContent);
        var now = DateTimeOffset.UtcNow;
        var group = TemplateGroup.Create("group_cal", "Raster gate", now);
        var revision = TemplateRevision.CreateDraft("revision_cal", group.GroupId, 1, now,
            [TemplatePage.Create("page_cal", "revision_cal", 0, "图片", TemplatePageContentType.NormalComposition,
                TemplateGenerationMode.OncePerProduct, null, true, "resource_cal")]);
        var service = new TemplateCalibrationService(new MemoryTemplateRepository(group, revision),
            new MemoryResourceRepository(resource with { Inspection = resource.Inspection with { Layers = [layer] } }));
        var view = await service.LoadAsync(new(group.GroupId, group.Name, revision.RevisionId, 1, new HashSet<string>()),
            "page_cal", default);
        Assert.Equal(allowed ? 1 : 0, view.Frames.Count);
    }

    [Theory]
    [InlineData(1500, 2008, 4009)] // observed full-window perimeter on a returned model page
    [InlineData(1467, 1179, 2351)] // observed full-window perimeter on a returned detail page
    public async Task SavedPsdPerimeterOfCoveredPhotoIsNotAnotherEditableImageSlot(int width, int height, int pixels)
    {
        var (resource, old, upper) = ClippedPhotoPair(width, height, pixels);
        var now = DateTimeOffset.UtcNow;
        var group = TemplateGroup.Create("group_cal", "Covered photo", now);
        var revision = TemplateRevision.CreateDraft("revision_cal", group.GroupId, 1, now,
            [TemplatePage.Create("page_cal", "revision_cal", 0, "图片页", TemplatePageContentType.NormalComposition,
                TemplateGenerationMode.OncePerProduct, null, true, "resource_cal")]);
        var templates = new MemoryTemplateRepository(group, revision);
        var service = new TemplateCalibrationService(templates, new MemoryResourceRepository(resource));
        var session = new TemplateDraftSession(group.GroupId, group.Name, revision.RevisionId, 1, new HashSet<string>());
        var view = await service.LoadAsync(session, "page_cal", default);
        var frames = view.Frames;
        Assert.Single(frames);
        Assert.DoesNotContain(frames, frame => frame.LayerId == old.LayerId);
        var picture = Assert.Single(frames, frame => frame.LayerId == upper.LayerId);
        Assert.True(picture.HasCompositeContribution);
        Assert.Empty(picture.SupersededLayers); // never silently hide a still-contributing layer
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SetSelectionRequirementAsync(
            session, "page_cal", $"frame_{old.LayerId}", "商品图", default));
        await service.SetSelectionRequirementAsync(session, "page_cal", picture.FrameId, "商品图", default);
        Assert.Equal(upper.LayerId, Assert.Single((await templates.GetRevisionAsync(revision.RevisionId, default))!
            .FrameBindings).ExpectedLayer.LayerId);
        Assert.Single(view.Frames);
    }

    [Theory]
    [InlineData("interior_photo")]
    [InlineData("different_window")]
    [InlineData("nonadjacent")]
    [InlineData("partial_upper")]
    [InlineData("partial_lower")]
    [InlineData("masked_upper")]
    [InlineData("linked_lower")]
    [InlineData("unavailable_evidence")]
    [InlineData("real_small_photo")]
    [InlineData("pixel_lower")]
    public async Task PhotosWithoutTheExactCoveredPerimeterPatternRemainDistinct(string mutation)
    {
        var (resource, old, upper) = ClippedPhotoPair(1500, 2008, 4009);
        var window = old.ClippingBase!;
        if (mutation == "interior_photo") old = old with { PixelEvidence = old.PixelEvidence with
            { CompositeContributionBounds = new(1, 2367, 1499, 4373) } };
        if (mutation == "different_window") upper = upper with
            { ClippingBase = window with { LayerId = 999, Path = [2, 0, 0] } };
        if (mutation == "nonadjacent") upper = upper with { Path = [1, 0, 4] };
        if (mutation == "partial_upper") upper = upper with { PixelEvidence = upper.PixelEvidence with
            { CompositeContributionPixelCount = 10000 } };
        if (mutation == "partial_lower") old = old with { PixelEvidence = old.PixelEvidence with
            { CompositeContributionBounds = new(0, 2366, 100, 4374) } };
        if (mutation == "masked_upper") upper = upper with { HasMask = true };
        if (mutation == "linked_lower") old = old with { SmartObject = old.SmartObject! with { Kind = "external" } };
        if (mutation == "unavailable_evidence") upper = upper with
            { PixelEvidence = upper.PixelEvidence with { Available = false } };
        if (mutation == "real_small_photo") old = old with { PixelEvidence = old.PixelEvidence with
            { CompositeContributionPixelCount = 10000 } };
        if (mutation == "pixel_lower") old = old with { Kind = "pixel", SmartObject = null,
            PixelEvidence = old.PixelEvidence with { RasterImage = new(true, 500, .2) } };
        var inspection = resource.Inspection with { Layers = [old, upper] };
        var now = DateTimeOffset.UtcNow;
        var group = TemplateGroup.Create("group_cal", "Independent photos", now);
        var revision = TemplateRevision.CreateDraft("revision_cal", group.GroupId, 1, now,
            [TemplatePage.Create("page_cal", "revision_cal", 0, "图片页", TemplatePageContentType.NormalComposition,
                TemplateGenerationMode.OncePerProduct, null, true, "resource_cal")]);
        var view = await new TemplateCalibrationService(new MemoryTemplateRepository(group, revision),
            new MemoryResourceRepository(resource with { Inspection = inspection }))
            .LoadAsync(new(group.GroupId, group.Name, revision.RevisionId, 1, new HashSet<string>()), "page_cal", default);
        if (mutation == "pixel_lower")
            Assert.DoesNotContain(view.Frames, frame => frame.LayerId == old.LayerId);
        else
            Assert.Contains(view.Frames, frame => frame.LayerId == old.LayerId);
    }

    private static (ImportedTemplateResource Resource, PsdLayerInspection Old, PsdLayerInspection Upper)
        ClippedPhotoPair(int width, int height, int residual)
    {
        var resource = Resource();
        var window = new PsdClippingBaseInspection([1, 0, 0], 167, "window", "pixel", true, true,
            new(0, 2366, width, 2366 + height), false, false, 255, "norm");
        var photo = resource.Inspection.Layers[0] with
        {
            Kind = "smartobject", Path = [1, 0, 2], LayerId = 299, Name = "old photo",
            Bounds = new(-1306, 1079, width + 476, 2366 + height + 1081), Clipping = true,
            ClippingBase = window, Opacity = 255, FillOpacity = 255, BlendMode = "norm",
            SmartObject = resource.Inspection.Layers[0].SmartObject! with
            { Kind = "data", DetectedFileType = "jpg", IsPsd = false,
                EmbeddedContentAvailable = true, EmbeddedContentSha256 = new string('a', 64) },
            PixelEvidence = new(true, width * (long)height, residual, window.Bounds)
        };
        var upper = photo with { Path = [1, 0, 3], LayerId = 380, Name = "upper photo",
            PixelEvidence = photo.PixelEvidence with { CompositeContributionPixelCount = width * (long)height - 1000 } };
        return (resource with { Inspection = resource.Inspection with
            { Canvas = new(width, 2366 + height + 100, 8, "RGB"), Layers = [photo, upper], LayerCount = 2 } }, photo, upper);
    }

    [Fact]
    public async Task VisibleRasterPhotographUsesExistingRuleEntryAndKeepsItsRealDisplayWindow()
    {
        var now = DateTimeOffset.UtcNow;
        var group = TemplateGroup.Create("group_cal", "Raster photo", now);
        var revision = TemplateRevision.CreateDraft("revision_cal", group.GroupId, 1, now,
            [TemplatePage.Create("page_cal", "revision_cal", 0, "照片", TemplatePageContentType.NormalComposition,
                TemplateGenerationMode.OncePerProduct, null, true, "resource_cal")]);
        var templates = new MemoryTemplateRepository(group, revision);
        var resource = Resource();
        var photo = resource.Inspection.Layers[0] with
        {
            Kind = "pixel", SmartObject = null, Bounds = new(-100, -200, 1000, 2000),
            PixelEvidence = new(true, 10000, 9000, new(10, 20, 110, 120))
                { RasterImage = new(true, 500, .2) }
        };
        var decoration = photo with { Path = [2], LayerId = 258,
            PixelEvidence = photo.PixelEvidence with { RasterImage = new(true, 2, .001) } };
        var covered = photo with { Path = [3], LayerId = 259,
            PixelEvidence = photo.PixelEvidence with { CompositeContributionPixelCount = 0, CompositeContributionBounds = null } };
        resource = resource with { Inspection = resource.Inspection with
            { SmartObjectCount = 0, LayerCount = 3, Layers = [photo, decoration, covered] } };
        var service = new TemplateCalibrationService(templates, new MemoryResourceRepository(resource));
        var session = new TemplateDraftSession(group.GroupId, group.Name, revision.RevisionId, 1, new HashSet<string>());
        var initial = await service.LoadAsync(session, "page_cal", default);
        var target = Assert.Single(initial.Frames);
        Assert.Equal(TemplateLayerKind.Pixel, target.LayerKind);
        await service.SetSelectionRequirementAsync(session, "page_cal", target.FrameId, "商品正面图", default);
        var binding = Assert.Single((await templates.GetRevisionAsync(revision.RevisionId, default))!.FrameBindings);
        Assert.Equal(TemplateLayerKind.Pixel, binding.ExpectedLayer.Kind);
        Assert.Equal(TemplateRect.Create(10, 20, 110, 120), binding.LayoutIntent.SafeBounds);
        var reopened = await new TemplateCalibrationService(templates, new MemoryResourceRepository(resource))
            .LoadAsync(session, "page_cal", default);
        Assert.Equal("商品正面图", Assert.Single(reopened.Frames).SelectionRequirement);
    }

    [Fact]
    public async Task NewBindingConvertsNestedWorkerPathAndKeepsCanvasPosition()
    {
        var now = DateTimeOffset.UtcNow;
        var group = TemplateGroup.Create("group_cal", "Calibration", now);
        var revision = TemplateRevision.CreateDraft("revision_cal", group.GroupId, 1, now,
            [TemplatePage.Create("page_cal", "revision_cal", 0, "位置校准", TemplatePageContentType.NormalComposition,
                TemplateGenerationMode.OncePerProduct, null, true, "resource_cal")]);
        var templates = new MemoryTemplateRepository(group, revision);
        var resource = Resource();
        var layer = resource.Inspection.Layers[0] with { Path = [0, 2], Bounds = new(123, 234, 423, 634) };
        resource = resource with { Inspection = resource.Inspection with { Layers = [layer] } };
        var service = new TemplateCalibrationService(templates, new MemoryResourceRepository(resource));
        var session = new TemplateDraftSession(group.GroupId, group.Name, revision.RevisionId, 1, new HashSet<string>());
        var view = await service.LoadAsync(session, "page_cal", CancellationToken.None);
        await service.BindAssetAsync(session, "page_cal", Assert.Single(view.Frames).FrameId, "asset_front", CancellationToken.None);
        var binding = Assert.Single((await templates.GetRevisionAsync(revision.RevisionId, CancellationToken.None))!.FrameBindings);
        Assert.Equal<int>([1, 3], binding.LayerPath.Segments);
        Assert.Equal(TemplateRect.Create(123, 234, 423, 634), binding.LayoutIntent.SafeBounds);
        Assert.Equal([0, 2], layer.Path);
    }

    [Theory]
    [InlineData(TemplateLayerKind.SmartObject, false)]
    [InlineData(TemplateLayerKind.SmartObject, true)]
    [InlineData(TemplateLayerKind.Pixel, false)]
    [InlineData(TemplateLayerKind.Pixel, true)]
    public async Task VisibleWindowIsUsedForNewRulesAndReopenedOldBindings(TemplateLayerKind kind, bool savedBefore)
    {
        var now = DateTimeOffset.UtcNow;
        var group = TemplateGroup.Create("group_cal", "Visible window", now);
        var old = BindingWithoutAsset();
        var binding = FrameBinding.Create(old.FrameId, old.PageId, LayerPath.Create([3]),
            ExpectedLayerEvidence.Create(257, "frame", true, true, false, false, 100, "normal",
                old.ExpectedLayer.StructureSignature, kind), [], old.SelectionRequirement, old.LayoutIntent);
        var revision = TemplateRevision.CreateDraft("revision_cal", group.GroupId, 1, now,
            [TemplatePage.Create("page_cal", "revision_cal", 0, "规则页", TemplatePageContentType.NormalComposition,
                TemplateGenerationMode.OncePerProduct, null, true, "resource_cal")], savedBefore ? [binding] : []);
        var templates = new MemoryTemplateRepository(group, revision);
        var resource = Resource();
        var photo = resource.Inspection.Layers[0] with
        {
            Kind = kind == TemplateLayerKind.Pixel ? "pixel" : "smartObject",
            SmartObject = kind == TemplateLayerKind.Pixel ? null : resource.Inspection.Layers[0].SmartObject,
            Bounds = new(-100, -200, 1000, 1600), Clipping = true,
            PixelEvidence = new(true, 1980000, 54000, new(80, 90, 260, 390))
                { RasterImage = kind == TemplateLayerKind.Pixel ? new(true, 5000, .5) : null }
        };
        resource = resource with { Inspection = resource.Inspection with { Layers = [photo] } };
        var service = new TemplateCalibrationService(templates, new MemoryResourceRepository(resource));
        var session = new TemplateDraftSession(group.GroupId, group.Name, revision.RevisionId, 1, new HashSet<string>());
        var view = await service.LoadAsync(session, "page_cal", default);
        var frame = Assert.Single(view.Frames);
        Assert.Equal(new TemplateFramePreviewBounds(80, 90, 180, 300), frame.PreviewBounds);
        await service.SetSelectionRequirementAsync(session, "page_cal", frame.FrameId, "商品整体", default);
        var saved = Assert.Single((await templates.GetRevisionAsync(revision.RevisionId, default))!.FrameBindings);
        Assert.Equal(TemplateRect.Create(80, 90, 260, 390), saved.LayoutIntent.SafeBounds);
        if (savedBefore) Assert.Equal(TemplateRect.Create(0, 0, 790, 1252), binding.LayoutIntent.SafeBounds);
    }

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
    public async Task RuleOnlyRequirementCanBeSavedAndReloadedWithoutInventingAnAsset()
    {
        var now = DateTimeOffset.UtcNow;
        var group = TemplateGroup.Create("group_rule_only", "Rule only", now);
        var revision = TemplateRevision.CreateDraft(
            "revision_rule_only",
            group.GroupId,
            1,
            now,
            [TemplatePage.Create(
                "page_rule_only",
                "revision_rule_only",
                0,
                "PSD 结构页",
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

        var initial = await service.LoadAsync(session, "page_rule_only", CancellationToken.None);
        var frameId = Assert.Single(initial.Frames).FrameId;
        var saved = await service.SetSelectionRequirementAsync(
            session,
            "page_rule_only",
            frameId,
            "商品正面图，主体完整且保持同一颜色版本。",
            CancellationToken.None);
        var reloaded = await service.LoadAsync(session, "page_rule_only", CancellationToken.None);
        var binding = Assert.Single((await templates.GetRevisionAsync(
            revision.RevisionId,
            CancellationToken.None))!.FrameBindings);

        Assert.False(saved.Frames.Single().IsRegistered);
        Assert.True(saved.Frames.Single().HasPersistedBinding);
        Assert.Equal("商品正面图，主体完整且保持同一颜色版本。", reloaded.Frames.Single().SelectionRequirement);
        Assert.DoesNotContain(binding.SelectionRequirement.Criteria, item => item.Key == "asset_id");
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task ManualRuleRegistrationUsesCompositeEvidenceAndRejectsUnsupportedCandidates(bool contributes, bool masked)
    {
        var now = DateTimeOffset.UtcNow;
        var group = TemplateGroup.Create("group_cal", "Manual rule", now);
        var revision = TemplateRevision.CreateDraft("revision_cal", group.GroupId, 1, now,
            [TemplatePage.Create("page_cal", "revision_cal", 0, "规则页", TemplatePageContentType.NormalComposition,
                TemplateGenerationMode.OncePerProduct, null, true, "resource_cal")]);
        var templates = new MemoryTemplateRepository(group, revision);
        var resource = Resource();
        var layer = Assert.Single(resource.Inspection.Layers) with
        {
            HasMask = masked,
            PixelEvidence = new(true, 100, contributes ? 100 : 0,
                contributes ? new PsdLayerBounds(10, 20, 110, 120) : null)
        };
        resource = resource with { Inspection = resource.Inspection with { Layers = [layer] } };
        var service = new TemplateCalibrationService(templates, new MemoryResourceRepository(resource));
        var session = new TemplateDraftSession(group.GroupId, group.Name, revision.RevisionId, revision.RevisionNo,
            new HashSet<string>(StringComparer.Ordinal));
        var initial = await service.LoadAsync(session, "page_cal", default);
        var target = Assert.Single(initial.Frames);
        Assert.Equal(contributes, target.HasCompositeContribution);
        Assert.Equal(contributes ? new TemplateFramePreviewBounds(10, 20, 100, 100) : null, target.PreviewBounds);
        if (contributes && !masked)
        {
            var saved = await service.SetSelectionRequirementAsync(session, "page_cal", target.FrameId, "假模特正面图", default);
            Assert.True(Assert.Single(saved.Frames).HasPersistedBinding);
            Assert.Null(Assert.Single(saved.Frames).BoundAssetId);
        }
        else
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                service.SetSelectionRequirementAsync(session, "page_cal", target.FrameId, "假模特正面图", default));
            Assert.Equal("TPL_PSD_UNSUPPORTED_STRUCTURE", error.Message);
            Assert.Empty((await templates.GetRevisionAsync(revision.RevisionId, default))!.FrameBindings);
        }
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
        Assert.Equal(new TemplateFramePreviewBounds(0, 0, 790, 1252), Assert.Single(view.Frames).PreviewBounds);
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
