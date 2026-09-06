using Desktop.Application.Files;
using Desktop.Application.Psd;
using Desktop.Application.Templates;
using Desktop.Domain.Templates;

namespace Desktop.Application.Tests;

public sealed class ReferenceLearningServiceTests
{
    [Fact]
    public async Task ExactEmbeddedPayloadRequiresReviewBeforeProjection()
    {
        using var root = new TempDirectory();
        var owned = Path.Combine(root.Path, "templates", "reference.psd");
        Directory.CreateDirectory(Path.GetDirectoryName(owned)!);
        await File.WriteAllBytesAsync(owned, [1, 2, 3]);
        var photo = new ReferencePhotoFingerprint(
            "asset_aaaaaaaaaaaaaaaaaaaaaaaa",
            "front.png",
            "front.png",
            3,
            "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            "image/png",
            100,
            200);
        var service = new ReferenceLearningService(
            new FixedInspectionPort(Inspection(photo.Sha256)),
            new FixedPhotoPort(new ReferencePhotoInventory(
                "sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
                [photo])),
            new RootPathPolicy(root.Path));

        var analyzed = await service.AnalyzeAsync(new ReferenceLearningRequest(
            "learn_01",
            "page_01",
            "templates/reference.psd",
            root.Path));

        Assert.True(analyzed.IsSuccess);
        var candidate = Assert.Single(analyzed.Document!.Candidates);
        Assert.Equal(ReferenceMatchState.Exact, candidate.MatchState);
        Assert.False(analyzed.Document.CanCommit);
        Assert.DoesNotContain(root.Path, System.Text.Json.JsonSerializer.Serialize(analyzed.Document));

        var reviewed = await service.ApplyReviewAsync(
            analyzed.Document,
            [new ReferenceFrameReview(
                candidate.FrameId,
                ReferenceReviewDecision.Accepted,
                SemanticLabel: "完整正面图")]);

        Assert.True(reviewed.IsSuccess);
        Assert.Equal(ReferenceLearningState.ReadyToCommit, reviewed.Document!.State);
        var binding = Assert.Single(ReferenceLearningRuleProjector.ProjectBindings(reviewed.Document));
        Assert.Contains(binding.SelectionRequirement.Criteria, criterion =>
            criterion.Key == "asset_id" && criterion.Value == photo.AssetId);
        Assert.Equal(1, Assert.Single(binding.LayerPath.Segments));
    }

    [Fact]
    public async Task StructuralDetectorFoldsOverlappingReplacementStackToTopmostLayer()
    {
        using var root = new TempDirectory();
        await File.WriteAllBytesAsync(Path.Combine(root.Path, "reference.psd"), [1]);
        var first = Photo("asset_stack_first", "first.png", 'a', 0, 0);
        var second = Photo("asset_stack_second", "second.png", 'b', 0, 100);
        var inspection = new PsdInspectionDocument(
            "1111111111111111111111111111111111111111111111111111111111111111",
            1,
            new PsdCanvasInspection(100, 200, 8, "RGB"),
            7,
            5,
            0,
            0,
            3,
            "2222222222222222222222222222222222222222222222222222222222222222",
            new PsdInspectionTiming(1, 1, 2),
            1024,
            null,
            [
                Layer(0, 1, "hidden", first.Sha256, visible: false, bounds: new PsdLayerBounds(0, 0, 100, 100)),
                Pixel(1, 2, new PsdLayerBounds(0, 0, 100, 200)),
                Layer(2, 3, "old_a", first.Sha256, bounds: new PsdLayerBounds(0, 0, 100, 100)),
                Layer(3, 4, "old_b", first.Sha256, bounds: new PsdLayerBounds(0, 0, 100, 100)),
                Layer(4, 5, "top", first.Sha256, bounds: new PsdLayerBounds(0, 0, 100, 100)),
                Layer(5, 6, "second", second.Sha256, bounds: new PsdLayerBounds(0, 100, 100, 200)),
                Layer(6, 7, "hidden_tail", second.Sha256, visible: false, bounds: new PsdLayerBounds(0, 0, 100, 100))
            ]);
        var service = new ReferenceLearningService(
            new FixedInspectionPort(inspection),
            new FixedPhotoPort(new ReferencePhotoInventory(
                "sha256:" + new string('c', 64), [first, second])),
            new RootPathPolicy(root.Path));

        var result = await service.AnalyzeAsync(new ReferenceLearningRequest(
            "learn_stack", "page_stack", "reference.psd", root.Path));

        Assert.True(result.IsSuccess);
        Assert.Equal(2, result.Document!.Candidates.Count);
        var top = Assert.Single(result.Document.Candidates, candidate => candidate.LayerPath.SequenceEqual([4]));
        Assert.Equal([2, 3], top.SupersededLayerEvidence.Select(layer => layer.LayerPath.Single()));
        Assert.Equal(2, result.Document.Candidates.Count(candidate => candidate.IsProduct));
    }

    [Fact]
    public async Task SmallMatchedInformationGraphicRemainsReviewableAsAProductFrame()
    {
        using var root = new TempDirectory();
        await File.WriteAllBytesAsync(Path.Combine(root.Path, "reference.psd"), [1]);
        var info = new ReferencePhotoFingerprint(
            "asset_info_graphic", "composition.png", "composition.png", 2956,
            new string('a', 64), "image/png", 239, 252);
        var service = new ReferenceLearningService(
            new FixedInspectionPort(Inspection(info.Sha256) with
            {
                Layers = [Inspection(info.Sha256).Layers[0] with
                {
                    Bounds = new PsdLayerBounds(0, 0, 100, 100),
                    SmartObject = Inspection(info.Sha256).Layers[0].SmartObject! with
                    {
                        Filename = "composition.png",
                        FileSize = 2956
                    }
                }]
            }),
            new FixedPhotoPort(new ReferencePhotoInventory(
                "sha256:" + new string('b', 64), [info])),
            new RootPathPolicy(root.Path));

        var result = await service.AnalyzeAsync(new ReferenceLearningRequest(
            "learn_info", "page_info", "reference.psd", root.Path));

        var candidate = Assert.Single(result.Document!.Candidates);
        Assert.True(candidate.IsProduct);
        Assert.False(candidate.IsAutoConfirmable);
        Assert.Equal(ReferenceReviewDecision.Pending, candidate.ReviewDecision);
        Assert.False(result.Document.CanCommit);

        var reviewed = await service.ApplyReviewAsync(
            result.Document,
            [new ReferenceFrameReview(
                candidate.FrameId,
                ReferenceReviewDecision.Accepted,
                SemanticLabel: "成分说明图")]);

        Assert.True(reviewed.IsSuccess);
        Assert.Single(ReferenceLearningRuleProjector.ProjectBindings(reviewed.Document!));
    }

    [Fact]
    public async Task SameLayerIdOnDifferentPagesProducesStableDistinctFrameIds()
    {
        using var root = new TempDirectory();
        await File.WriteAllBytesAsync(Path.Combine(root.Path, "first.psd"), [1]);
        await File.WriteAllBytesAsync(Path.Combine(root.Path, "second.psd"), [1]);
        var photo = new ReferencePhotoFingerprint(
            "asset_distinct_frame", "front.png", "front.png", 1,
            new string('a', 64), "image/png", 10, 10);
        var service = new ReferenceLearningService(
            new FixedInspectionPort(Inspection(photo.Sha256)),
            new FixedPhotoPort(new ReferencePhotoInventory(
                "sha256:" + new string('b', 64), [photo])),
            new RootPathPolicy(root.Path));

        var first = await service.AnalyzeAsync(new ReferenceLearningRequest(
            "learn_distinct_first", "page_first", "first.psd", root.Path));
        var firstAgain = await service.AnalyzeAsync(new ReferenceLearningRequest(
            "learn_distinct_first_retry", "page_first", "first.psd", root.Path));
        var second = await service.AnalyzeAsync(new ReferenceLearningRequest(
            "learn_distinct_second", "page_second", "second.psd", root.Path));

        Assert.Equal(first.Document!.Candidates[0].FrameId, firstAgain.Document!.Candidates[0].FrameId);
        Assert.NotEqual(first.Document.Candidates[0].FrameId, second.Document!.Candidates[0].FrameId);
    }

    [Fact]
    public async Task RecognizedLayerCueProvidesUsageWithoutManualLayoutReentry()
    {
        using var root = new TempDirectory();
        await File.WriteAllBytesAsync(Path.Combine(root.Path, "reference.psd"), [1]);
        var photo = new ReferencePhotoFingerprint(
            "asset_semantic_photo",
            "front.png",
            "front.png",
            1,
            new string('a', 64),
            "image/png",
            10,
            10);
        var service = new ReferenceLearningService(
            new FixedInspectionPort(Inspection(photo.Sha256)),
            new FixedPhotoPort(new ReferencePhotoInventory(
                "sha256:" + new string('b', 64),
                [photo])),
            new RootPathPolicy(root.Path));
        var analyzed = await service.AnalyzeAsync(new ReferenceLearningRequest(
            "learn_semantic", "page_01", "reference.psd", root.Path));

        var reviewed = await service.ApplyReviewAsync(
            analyzed.Document!,
            [new ReferenceFrameReview(
                analyzed.Document!.Candidates[0].FrameId,
                ReferenceReviewDecision.Accepted)]);

        Assert.True(reviewed.IsSuccess);
        Assert.Equal("完整正面图", analyzed.Document.Candidates[0].SemanticLabel);
        Assert.Equal("完整正面图", reviewed.Document!.Candidates[0].SemanticLabel);
        Assert.Equal(analyzed.Document.Candidates[0].LayoutIntent, reviewed.Document.Candidates[0].LayoutIntent);
    }

    [Fact]
    public async Task AutoConfirmableCandidateRetainsExtractedLayoutWhenBatchReviewed()
    {
        using var root = new TempDirectory();
        await File.WriteAllBytesAsync(Path.Combine(root.Path, "reference.psd"), [1]);
        var photo = Photo(
            "asset_front_auto",
            "front.png",
            'a',
            0,
            200);
        var service = new ReferenceLearningService(
            new FixedInspectionPort(Inspection(photo.Sha256)),
            new FixedPhotoPort(new ReferencePhotoInventory(
                "sha256:" + new string('b', 64),
                [photo])),
            new RootPathPolicy(root.Path));

        var analyzed = await service.AnalyzeAsync(new ReferenceLearningRequest(
            "learn_auto_confirm",
            "page_auto_confirm",
            "reference.psd",
            root.Path));
        var candidate = Assert.Single(analyzed.Document!.Candidates);
        Assert.True(candidate.IsEvidenceClosed);
        Assert.True(candidate.IsAutoConfirmable);
        var extractedLayout = candidate.LayoutIntent;

        var reviewed = await service.ApplyReviewAsync(
            analyzed.Document,
            [new ReferenceFrameReview(
                candidate.FrameId,
                ReferenceReviewDecision.Accepted,
                candidate.ReferenceAssetId,
                candidate.SemanticLabel)]);

        Assert.True(reviewed.IsSuccess);
        Assert.Equal(ReferenceLearningState.ReadyToCommit, reviewed.Document!.State);
        Assert.Equal(extractedLayout, reviewed.Document.Candidates[0].LayoutIntent);
        Assert.True(reviewed.Document.Candidates[0].IsHighConfidence);
    }

    [Fact]
    public async Task BatchConfirmationLeavesLowConfidenceCandidatePendingAndBlocksCommit()
    {
        using var root = new TempDirectory();
        await File.WriteAllBytesAsync(Path.Combine(root.Path, "reference.psd"), [1]);
        var exact = Photo("asset_batch_exact", "front.png", 'a', 0, 100);
        var ambiguous = Photo("asset_batch_ambiguous", "detail.png", 'b', 100, 200);
        var inspection = Inspection(exact.Sha256) with
        {
            LayerCount = 2,
            SmartObjectCount = 2,
            Layers = [
                Inspection(exact.Sha256).Layers[0] with
                {
                    Bounds = new PsdLayerBounds(0, 0, 100, 100),
                    SmartObject = Inspection(exact.Sha256).Layers[0].SmartObject! with
                    {
                        TransformBox = [0, 0, 100, 0, 100, 100, 0, 100]
                    }
                },
                Inspection(ambiguous.Sha256).Layers[0] with
                {
                    Path = [1],
                    LayerId = 8,
                    Name = "detail",
                    Bounds = new PsdLayerBounds(0, 100, 100, 200),
                    SmartObject = Inspection(ambiguous.Sha256).Layers[0].SmartObject! with
                    {
                        UniqueId = "so_batch_02",
                        Filename = "detail.png",
                        TransformBox = [0, 100, 100, 100, 100, 200, 0, 200]
                    }
                }]
        };
        var service = new ReferenceLearningService(
            new FixedInspectionPort(inspection),
            new FixedPhotoPort(new ReferencePhotoInventory(
                "sha256:" + new string('c', 64),
                [exact, ambiguous, ambiguous with { AssetId = "asset_batch_ambiguous_copy" }])),
            new RootPathPolicy(root.Path));

        var analyzed = await service.AnalyzeAsync(new ReferenceLearningRequest(
            "learn_batch_gate",
            "page_batch_gate",
            "reference.psd",
            root.Path));
        var high = Assert.Single(analyzed.Document!.Candidates, candidate => candidate.IsAutoConfirmable);
        var low = Assert.Single(analyzed.Document.Candidates, candidate => !candidate.IsAutoConfirmable);

        var reviewed = await service.ApplyReviewAsync(
            analyzed.Document,
            [new ReferenceFrameReview(
                high.FrameId,
                ReferenceReviewDecision.Accepted,
                high.ReferenceAssetId,
                high.SemanticLabel)]);

        Assert.True(reviewed.IsSuccess);
        Assert.Equal(ReferenceReviewDecision.Accepted, reviewed.Document!.Candidates.Single(
            candidate => candidate.FrameId == high.FrameId).ReviewDecision);
        Assert.Equal(ReferenceReviewDecision.Pending, reviewed.Document.Candidates.Single(
            candidate => candidate.FrameId == low.FrameId).ReviewDecision);
        Assert.Equal(ReferenceLearningState.AwaitingReview, reviewed.Document.State);
        Assert.False(reviewed.Document.CanCommit);
    }

    [Fact]
    public void ProjectorRejectsForgedAcceptedCandidateWithoutExplicitSemantic()
    {
        var photo = new ReferencePhotoFingerprint(
            "asset_forged_semantic", "front.png", "front.png", 1,
            new string('a', 64), "image/png", 10, 10);
        var original = new ReferenceFrameCandidate(
            "frame_forged",
            "page_forged",
            [0],
            7,
            "audit-layer-name",
            "sha256:" + new string('b', 64),
            true,
            true,
            false,
            false,
            false,
            255,
            "normal",
            [0, 0, 10, 0, 10, 10, 0, 10],
            ReferenceMatchState.Exact,
            photo.AssetId,
            photo.RelativePath,
            photo.Sha256,
            "",
            1,
            LayoutIntent.Create("subject_cover", 1, 0, 0, TemplateRect.Create(0, 0, 10, 10)),
            ReferenceReviewDecision.Accepted);
        var photos = new[] { photo };
        var candidates = new[] { original };
        var psdHash = "sha256:" + new string('c', 64);
        var structure = "sha256:" + new string('b', 64);
        var folder = "sha256:" + new string('d', 64);
        var forged = new ReferenceLearningDocument(
            "learn_forged_semantic",
            "page_forged",
            "templates/reference.psd",
            psdHash,
            1,
            structure,
            new PsdCanvasInspection(10, 10, 8, "RGB"),
            folder,
            photos,
            candidates,
            ReferenceLearningState.ReadyToCommit,
            ReferenceLearningDigest.Compute(psdHash, structure, folder, photos, candidates),
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow);

        Assert.False(forged.CanCommit);
        Assert.Throws<TemplateRuleViolationException>(() =>
            ReferenceLearningRuleProjector.ProjectBindings(forged));
    }

    [Fact]
    public async Task MissingPayloadAndExternalLinkFailClosedAndCannotBeAcceptedWithoutAnExplicitPhoto()
    {
        using var root = new TempDirectory();
        var owned = Path.Combine(root.Path, "templates", "reference.psd");
        Directory.CreateDirectory(Path.GetDirectoryName(owned)!);
        await File.WriteAllBytesAsync(owned, [1]);
        var service = new ReferenceLearningService(
            new FixedInspectionPort(Inspection(
                "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc",
                kind: "external",
                embedded: false)),
            new FixedPhotoPort(new ReferencePhotoInventory(
                "sha256:dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd",
                [new ReferencePhotoFingerprint(
                    "asset_eeeeeeeeeeeeeeeeeeeeeeee",
                    "other.png",
                    "other.png",
                    1,
                    "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee",
                    "image/png",
                    10,
                    10)])),
            new RootPathPolicy(root.Path));

        var analyzed = await service.AnalyzeAsync(new ReferenceLearningRequest(
            "learn_02",
            "page_01",
            "templates/reference.psd",
            root.Path));
        Assert.Equal(ReferenceLearningState.RequiresManualCalibration, analyzed.Document!.State);
        var rejected = await service.ApplyReviewAsync(
            analyzed.Document,
            [new ReferenceFrameReview(
                analyzed.Document.Candidates[0].FrameId,
                ReferenceReviewDecision.ManualCalibration)]);
        Assert.Equal(ReferenceLearningState.RequiresManualCalibration, rejected.Document!.State);
        Assert.False(rejected.Document.CanCommit);
    }

    [Fact]
    public async Task ReviewCannotReferencePhotoOutsideTheReferenceInventory()
    {
        using var root = new TempDirectory();
        var owned = Path.Combine(root.Path, "reference.psd");
        await File.WriteAllBytesAsync(owned, [1]);
        var service = new ReferenceLearningService(
            new FixedInspectionPort(Inspection("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")),
            new FixedPhotoPort(new ReferencePhotoInventory(
                "sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
                [new ReferencePhotoFingerprint(
                    "asset_cccccccccccccccccccccc",
                    "ok.png",
                    "ok.png",
                    1,
                    "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc",
                    "image/png",
                    10,
                    10)])),
            new RootPathPolicy(root.Path));
        var analyzed = await service.AnalyzeAsync(new ReferenceLearningRequest(
            "learn_03", "page_01", "reference.psd", root.Path));
        var result = await service.ApplyReviewAsync(
            analyzed.Document!,
            [new ReferenceFrameReview(
                analyzed.Document!.Candidates[0].FrameId,
                ReferenceReviewDecision.Accepted,
                "asset_not_in_inventory")]);
        Assert.False(result.IsSuccess);
        Assert.Equal("TPL_LEARN_REVIEW_ASSET_INVALID", result.Error!.Code);
    }

    [Fact]
    public async Task PartialReviewCannotBecomeReadyOrProjectRules()
    {
        using var root = new TempDirectory();
        var owned = Path.Combine(root.Path, "reference.psd");
        await File.WriteAllBytesAsync(owned, [1]);
        var first = new ReferencePhotoFingerprint(
            "asset_aaaaaaaaaaaaaaaaaaaaaaaa", "one.png", "one.png", 1,
            new string('a', 64), "image/png", 10, 10);
        var second = new ReferencePhotoFingerprint(
            "asset_bbbbbbbbbbbbbbbbbbbbbbbb", "two.png", "two.png", 1,
            new string('b', 64), "image/png", 10, 10);
        var inspection = Inspection(first.Sha256) with
        {
            LayerCount = 2,
            SmartObjectCount = 2,
            Layers = [
                Inspection(first.Sha256).Layers[0] with
                {
                    Bounds = new PsdLayerBounds(0, 0, 100, 100),
                    SmartObject = Inspection(first.Sha256).Layers[0].SmartObject! with
                    {
                        TransformBox = [0, 0, 100, 0, 100, 100, 0, 100]
                    }
                },
                Inspection(second.Sha256).Layers[0] with
                {
                    Path = [1],
                    LayerId = 8,
                    Name = "back",
                    Bounds = new PsdLayerBounds(0, 100, 100, 200),
                    SmartObject = Inspection(second.Sha256).Layers[0].SmartObject! with
                    {
                        UniqueId = "so_02",
                        Filename = "two.png",
                        TransformBox = [0, 100, 100, 100, 100, 200, 0, 200]
                    }
                }]
        };
        var service = new ReferenceLearningService(
            new FixedInspectionPort(inspection),
            new FixedPhotoPort(new ReferencePhotoInventory(
                "sha256:" + new string('c', 64), [first, second])),
            new RootPathPolicy(root.Path));

        var analyzed = await service.AnalyzeAsync(new ReferenceLearningRequest(
            "learn_partial", "page_01", "reference.psd", root.Path));
        var reviewed = await service.ApplyReviewAsync(
            analyzed.Document!,
            [new ReferenceFrameReview(
                analyzed.Document!.Candidates[0].FrameId,
                ReferenceReviewDecision.Accepted,
                SemanticLabel: "完整正面图")]);

        Assert.True(reviewed.IsSuccess);
        Assert.Equal(ReferenceLearningState.AwaitingReview, reviewed.Document!.State);
        Assert.False(reviewed.Document.CanCommit);
        Assert.Throws<TemplateRuleViolationException>(() =>
            ReferenceLearningRuleProjector.ProjectBindings(reviewed.Document));
    }

    [Fact]
    public async Task LearnedBindingMarksReferenceAssetAsProvenanceOnly()
    {
        using var root = new TempDirectory();
        var owned = Path.Combine(root.Path, "reference.psd");
        await File.WriteAllBytesAsync(owned, [1]);
        var photo = new ReferencePhotoFingerprint(
            "asset_aaaaaaaaaaaaaaaaaaaaaaaa", "front.png", "front.png", 1,
            new string('a', 64), "image/png", 10, 10);
        var service = new ReferenceLearningService(
            new FixedInspectionPort(Inspection(photo.Sha256)),
            new FixedPhotoPort(new ReferencePhotoInventory(
                "sha256:" + new string('b', 64), [photo])),
            new RootPathPolicy(root.Path));
        var analyzed = await service.AnalyzeAsync(new ReferenceLearningRequest(
            "learn_target", "page_01", "reference.psd", root.Path));
        var reviewed = await service.ApplyReviewAsync(
            analyzed.Document!,
            [new ReferenceFrameReview(
                analyzed.Document!.Candidates[0].FrameId,
                ReferenceReviewDecision.Accepted,
                SemanticLabel: "完整正面图")]);
        var binding = Assert.Single(ReferenceLearningRuleProjector.ProjectBindings(
            reviewed.Document!));
        Assert.Contains(binding.SelectionRequirement.Criteria, criterion =>
            criterion.Key == "asset_id" && criterion.Value == photo.AssetId);
        Assert.Contains(binding.SelectionRequirement.Criteria, criterion =>
            criterion.Key == "reference_only" && criterion.Value == "true");
    }

    [Fact]
    public async Task ExplicitReviewOfNonHashMatchIsRecordedAsManualInsteadOfExact()
    {
        using var root = new TempDirectory();
        await File.WriteAllBytesAsync(Path.Combine(root.Path, "reference.psd"), [1]);
        var photo = new ReferencePhotoFingerprint(
            "asset_manual_photo", "manual.png", "manual.png", 1,
            new string('d', 64), "image/png", 10, 10);
        var service = new ReferenceLearningService(
            new FixedInspectionPort(Inspection(new string('a', 64))),
            new FixedPhotoPort(new ReferencePhotoInventory(
                "sha256:" + new string('b', 64), [photo])),
            new RootPathPolicy(root.Path));
        var analyzed = await service.AnalyzeAsync(new ReferenceLearningRequest(
            "learn_manual", "page_01", "reference.psd", root.Path));

        var reviewed = await service.ApplyReviewAsync(
            analyzed.Document!,
            [new ReferenceFrameReview(
                analyzed.Document!.Candidates[0].FrameId,
                ReferenceReviewDecision.Accepted,
                photo.AssetId,
                "人工确认正面")]);

        var candidate = Assert.Single(reviewed.Document!.Candidates);
        Assert.Equal(ReferenceMatchState.ManuallyConfirmed, candidate.MatchState);
        Assert.Equal("人工确认正面", candidate.SemanticLabel);
        Assert.True(reviewed.Document.CanCommit);
    }

    [Fact]
    public async Task PersistedSessionMustBeResumedAndStaleReviewIsRejected()
    {
        using var root = new TempDirectory();
        await File.WriteAllBytesAsync(Path.Combine(root.Path, "reference.psd"), [1]);
        var photo = new ReferencePhotoFingerprint(
            "asset_aaaaaaaaaaaaaaaaaaaaaaaa", "front.png", "front.png", 1,
            new string('a', 64), "image/png", 10, 10);
        var sessions = new MemorySessionStore();
        var first = new ReferenceLearningService(
            new FixedInspectionPort(Inspection(photo.Sha256)),
            new FixedPhotoPort(new ReferencePhotoInventory(
                "sha256:" + new string('b', 64), [photo])),
            new RootPathPolicy(root.Path),
            sessions);
        var analyzed = await first.AnalyzeAsync(new ReferenceLearningRequest(
            "learn_resume", "page_01", "reference.psd", root.Path));

        var restarted = new ReferenceLearningService(
            new FixedInspectionPort(Inspection(photo.Sha256)),
            new FixedPhotoPort(new ReferencePhotoInventory(
                "sha256:" + new string('b', 64), [photo])),
            new RootPathPolicy(root.Path),
            sessions);
        var resumed = await restarted.ResumeAsync("learn_resume");
        var reviewed = await restarted.ApplyReviewAsync(
            resumed.Document!,
            [new ReferenceFrameReview(
                resumed.Document!.Candidates[0].FrameId,
                ReferenceReviewDecision.Accepted,
                SemanticLabel: "完整正面图")]);
        var stale = await restarted.ApplyReviewAsync(
            analyzed.Document!,
            [new ReferenceFrameReview(
                analyzed.Document!.Candidates[0].FrameId,
                ReferenceReviewDecision.Accepted,
                SemanticLabel: "完整正面图")]);

        Assert.Equal(ReferenceLearningState.ReadyToCommit, reviewed.Document!.State);
        Assert.False(stale.IsSuccess);
        Assert.Equal("TPL_LEARN_SESSION_STALE", stale.Error!.Code);
    }

    [Fact]
    public async Task StrictResumeRejectsChangedReferencePhotoInventory()
    {
        using var root = new TempDirectory();
        var psdBytes = new byte[] { 1 };
        await File.WriteAllBytesAsync(Path.Combine(root.Path, "reference.psd"), psdBytes);
        var psdHash = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(psdBytes)).ToLowerInvariant();
        var photo = new ReferencePhotoFingerprint(
            "asset_resume_photo",
            "front.png",
            "front.png",
            1,
            new string('a', 64),
            "image/png",
            10,
            10);
        var photoPort = new MutablePhotoPort(new ReferencePhotoInventory(
            "sha256:" + new string('b', 64),
            [photo]));
        var sessions = new MemorySessionStore();
        var service = new ReferenceLearningService(
            new FixedInspectionPort(Inspection(photo.Sha256) with { FileSha256 = psdHash }),
            photoPort,
            new RootPathPolicy(root.Path),
            sessions);
        var request = new ReferenceLearningRequest(
            "learn_changed", "page_01", "reference.psd", root.Path);
        var analyzed = await service.AnalyzeAsync(request);
        Assert.True(analyzed.IsSuccess);
        photoPort.Inventory = new ReferencePhotoInventory(
            "sha256:" + new string('c', 64),
            [photo with { Sha256 = new string('d', 64) }]);

        var restarted = new ReferenceLearningService(
            new FixedInspectionPort(Inspection(photo.Sha256) with { FileSha256 = psdHash }),
            photoPort,
            new RootPathPolicy(root.Path),
            sessions);
        var resumed = await restarted.ResumeAsync(request);

        Assert.False(resumed.IsSuccess);
        Assert.Equal("TPL_LEARN_SOURCE_CHANGED", resumed.Error!.Code);
    }

    [Fact]
    public async Task StrictResumeRejectsChangedPsdEvenWhenLengthIsUnchanged()
    {
        using var root = new TempDirectory();
        var psdPath = Path.Combine(root.Path, "reference.psd");
        var originalBytes = new byte[] { 1, 2, 3 };
        await File.WriteAllBytesAsync(psdPath, originalBytes);
        var originalHash = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(originalBytes)).ToLowerInvariant();
        var photo = new ReferencePhotoFingerprint(
            "asset_psd_change",
            "front.png",
            "front.png",
            3,
            new string('a', 64),
            "image/png",
            10,
            10);
        var photos = new ReferencePhotoInventory("sha256:" + new string('b', 64), [photo]);
        var sessions = new MemorySessionStore();
        var request = new ReferenceLearningRequest(
            "learn_psd_changed", "page_01", "reference.psd", root.Path);
        var service = new ReferenceLearningService(
            new FixedInspectionPort(Inspection(photo.Sha256) with { FileSha256 = originalHash }),
            new FixedPhotoPort(photos),
            new RootPathPolicy(root.Path),
            sessions);
        Assert.True((await service.AnalyzeAsync(request)).IsSuccess);
        await File.WriteAllBytesAsync(psdPath, [3, 2, 1]);

        var resumed = await new ReferenceLearningService(
                new FixedInspectionPort(Inspection(photo.Sha256) with { FileSha256 = originalHash }),
                new FixedPhotoPort(photos),
                new RootPathPolicy(root.Path),
                sessions)
            .ResumeAsync(request);

        Assert.False(resumed.IsSuccess);
        Assert.Equal("TPL_LEARN_SOURCE_CHANGED", resumed.Error!.Code);
    }

    [Fact]
    public async Task SessionWriteFailureDoesNotActivateUnpersistedCandidate()
    {
        using var root = new TempDirectory();
        await File.WriteAllBytesAsync(Path.Combine(root.Path, "reference.psd"), [1]);
        var photo = new ReferencePhotoFingerprint(
            "asset_write_failure", "front.png", "front.png", 1,
            new string('a', 64), "image/png", 10, 10);
        var sessions = new FailingSessionStore();
        var service = new ReferenceLearningService(
            new FixedInspectionPort(Inspection(photo.Sha256)),
            new FixedPhotoPort(new ReferencePhotoInventory(
                "sha256:" + new string('b', 64), [photo])),
            new RootPathPolicy(root.Path),
            sessions);

        var analyzed = await service.AnalyzeAsync(new ReferenceLearningRequest(
            "learn_write_failure", "page_01", "reference.psd", root.Path));
        var attempted = Assert.IsType<ReferenceLearningDocument>(sessions.Attempted);
        var review = await service.ApplyReviewAsync(
            attempted,
            [new ReferenceFrameReview(
                attempted.Candidates[0].FrameId,
                ReferenceReviewDecision.Accepted,
                SemanticLabel: "完整正面图")]);

        Assert.False(analyzed.IsSuccess);
        Assert.Equal("TPL_LEARN_SESSION_WRITE_FAILED", analyzed.Error!.Code);
        Assert.False(review.IsSuccess);
        Assert.Equal("TPL_LEARN_SESSION_STALE", review.Error!.Code);
    }

    [Fact]
    public async Task CancelPersistsClosedSessionAndPreventsRuleProjection()
    {
        using var root = new TempDirectory();
        await File.WriteAllBytesAsync(Path.Combine(root.Path, "reference.psd"), [1]);
        var photo = new ReferencePhotoFingerprint(
            "asset_cancel", "front.png", "front.png", 1,
            new string('a', 64), "image/png", 10, 10);
        var sessions = new MemorySessionStore();
        var service = new ReferenceLearningService(
            new FixedInspectionPort(Inspection(photo.Sha256)),
            new FixedPhotoPort(new ReferencePhotoInventory(
                "sha256:" + new string('b', 64), [photo])),
            new RootPathPolicy(root.Path),
            sessions);
        var analyzed = await service.AnalyzeAsync(new ReferenceLearningRequest(
            "learn_cancel", "page_01", "reference.psd", root.Path));

        var cancelled = await service.CancelAsync(analyzed.Document!);
        var restored = await sessions.GetAsync("learn_cancel", CancellationToken.None);

        Assert.Equal(ReferenceLearningState.Cancelled, cancelled.Document!.State);
        Assert.Equal(ReferenceLearningState.Cancelled, restored!.State);
        Assert.Throws<TemplateRuleViolationException>(() =>
            ReferenceLearningRuleProjector.ProjectBindings(cancelled.Document));
    }

    private static PsdInspectionDocument Inspection(
        string embeddedHash,
        string kind = "data",
        bool embedded = true) => new(
        "1111111111111111111111111111111111111111111111111111111111111111",
        3,
        new PsdCanvasInspection(100, 200, 8, "RGB"),
        1,
        1,
        0,
        0,
        1,
        "2222222222222222222222222222222222222222222222222222222222222222",
        new PsdInspectionTiming(1, 1, 2),
        1024,
        null,
        [new PsdLayerInspection(
            [0],
            7,
            "front",
            "smartobject",
            false,
            true,
            true,
            true,
            255,
            255,
            "normal",
            new PsdLayerBounds(0, 0, 100, 200),
            false,
            false,
            null,
            new PsdSmartObjectInspection(
                "so_01",
                "front.png",
                kind,
                "png",
                "png",
                3,
                false,
                [0, 0, 100, 0, 100, 200, 0, 200])
            {
                EmbeddedContentSha256 = embeddedHash,
                EmbeddedContentAvailable = embedded
            })]);

    private static ReferencePhotoFingerprint Photo(
        string assetId,
        string fileName,
        char hashCharacter,
        int top,
        int bottom) => new(
            assetId,
            fileName,
            fileName,
            4096,
            new string(hashCharacter, 64),
            "image/png",
            1000,
            1000);

    private static PsdLayerInspection Layer(
        int path,
        int layerId,
        string name,
        string embeddedHash,
        bool visible = true,
        PsdLayerBounds? bounds = null) =>
        new(
            [path],
            layerId,
            name,
            "smartobject",
            false,
            visible,
            visible,
            true,
            255,
            255,
            "normal",
            bounds ?? new PsdLayerBounds(0, 0, 100, 100),
            false,
            false,
            null,
            new PsdSmartObjectInspection(
                $"so_{layerId}",
                name + ".png",
                "data",
                "png",
                "png",
                4096,
                false,
                [0, 0, 100, 0, 100, 100, 0, 100])
            {
                EmbeddedContentSha256 = embeddedHash,
                EmbeddedContentAvailable = true
            });

    private static PsdLayerInspection Pixel(
        int path,
        int layerId,
        PsdLayerBounds bounds) =>
        new(
            [path],
            layerId,
            "pixel",
            "pixel",
            false,
            true,
            true,
            false,
            255,
            255,
            "normal",
            bounds,
            false,
            false,
            null,
            null);

    private sealed class FixedInspectionPort(PsdInspectionDocument document) : IPsdInspectionPort
    {
        public Task<PsdInspectionResult> InspectAsync(PsdInspectionRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(PsdInspectionResult.Success(document));
    }

    private sealed class FixedPhotoPort(ReferencePhotoInventory inventory) : IReferencePhotoFingerprintPort
    {
        public Task<ReferencePhotoInventory> ScanAsync(string referencePhotoDirectory, CancellationToken cancellationToken) =>
            Task.FromResult(inventory);
    }

    private sealed class MutablePhotoPort(ReferencePhotoInventory inventory) : IReferencePhotoFingerprintPort
    {
        public ReferencePhotoInventory Inventory { get; set; } = inventory;

        public Task<ReferencePhotoInventory> ScanAsync(
            string referencePhotoDirectory,
            CancellationToken cancellationToken) => Task.FromResult(Inventory);
    }

    private sealed class MemorySessionStore : IReferenceLearningSessionStore
    {
        private readonly Dictionary<string, ReferenceLearningDocument> documents = new(StringComparer.Ordinal);

        public Task SaveAsync(ReferenceLearningDocument document, CancellationToken cancellationToken)
        {
            documents[document.LearningId] = document;
            return Task.CompletedTask;
        }

        public Task<ReferenceLearningDocument?> GetAsync(string learningId, CancellationToken cancellationToken) =>
            Task.FromResult(documents.GetValueOrDefault(learningId));
    }

    private sealed class FailingSessionStore : IReferenceLearningSessionStore
    {
        public ReferenceLearningDocument? Attempted { get; private set; }

        public Task SaveAsync(ReferenceLearningDocument document, CancellationToken cancellationToken)
        {
            Attempted = document;
            throw new IOException("TEST_REFERENCE_LEARNING_SESSION_WRITE_FAILED");
        }

        public Task<ReferenceLearningDocument?> GetAsync(
            string learningId,
            CancellationToken cancellationToken) => Task.FromResult<ReferenceLearningDocument?>(null);
    }

    private sealed class RootPathPolicy(string root) : IPathPolicy
    {
        private readonly string normalized = Path.GetFullPath(root);
        public string ResolveOwnedPath(string relativePath) => Path.GetFullPath(Path.Combine(normalized, relativePath));
        public bool IsOwnedPath(string path) => Path.GetFullPath(path).StartsWith(normalized, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "tpl-learn-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }
        public string Path { get; }
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
