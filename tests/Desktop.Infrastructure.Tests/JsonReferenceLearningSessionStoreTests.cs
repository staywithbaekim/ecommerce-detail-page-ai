using Desktop.Application.Psd;
using Desktop.Application.Templates;
using Desktop.Domain.Templates;
using Desktop.Infrastructure.Files;
using Desktop.Infrastructure.Templates;
using Desktop.Contracts.Validation;

namespace Desktop.Infrastructure.Tests;

public sealed class JsonReferenceLearningSessionStoreTests
{
    [Fact]
    public async Task RoundTripSurvivesRestartAndDoesNotPersistAbsoluteSourcePath()
    {
        using var directory = new TestDirectory();
        var pathPolicy = new LocalAppPathPolicy(directory.Path);
        var store = new JsonReferenceLearningSessionStore(
            new AtomicFileStore(pathPolicy),
            new JsonSchemaContractValidator());
        var document = Document();

        await store.SaveAsync(document, CancellationToken.None);
        var restarted = new JsonReferenceLearningSessionStore(
            new AtomicFileStore(new LocalAppPathPolicy(directory.Path)),
            new JsonSchemaContractValidator());
        var loaded = await restarted.GetAsync(document.LearningId, CancellationToken.None);

        Assert.NotNull(loaded);
        Assert.Equal("reference-learning/v1", loaded!.SchemaVersion);
        Assert.Equal(document.RulesDigest, loaded.RulesDigest);
        var loadedCandidate = Assert.Single(loaded.Candidates);
        Assert.Equal([2], Assert.Single(loadedCandidate.SupersededLayerEvidence)
            .LayerPath);
        Assert.Equal([1], loadedCandidate.StructuralWindowLayerPath);
        Assert.True(loadedCandidate.IsProduct);
        var persisted = await File.ReadAllTextAsync(Path.Combine(
            directory.Path,
            "learning",
            "reference-sessions",
            document.LearningId + ".json"));
        Assert.Contains("reference-learning/v1", persisted, StringComparison.Ordinal);
        Assert.DoesNotContain(directory.Path, persisted, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CorruptSessionReturnsNullInsteadOfRestoringPartialState()
    {
        using var directory = new TestDirectory();
        var pathPolicy = new LocalAppPathPolicy(directory.Path);
        var files = new AtomicFileStore(pathPolicy);
        await files.WriteAtomicallyAsync(
            "learning/reference-sessions/learn_corrupt.json",
            "{not-json"u8.ToArray(),
            CancellationToken.None);
        var store = new JsonReferenceLearningSessionStore(files);

        Assert.Null(await store.GetAsync("learn_corrupt", CancellationToken.None));
    }

    [Fact]
    public async Task AwaitingReviewCandidateCanPersistBeforeUserProvidesSemanticLabel()
    {
        using var directory = new TestDirectory();
        var store = new JsonReferenceLearningSessionStore(
            new AtomicFileStore(new LocalAppPathPolicy(directory.Path)),
            new JsonSchemaContractValidator());
        var original = Document();
        var pending = original.Candidates[0] with
        {
            SemanticLabel = "",
            ReviewDecision = ReferenceReviewDecision.Pending
        };
        var document = original.WithCandidates(
            [pending],
            ReferenceLearningState.AwaitingReview,
            null,
            original.UpdatedAt.AddMinutes(1));

        await store.SaveAsync(document, CancellationToken.None);
        var loaded = await store.GetAsync(document.LearningId, CancellationToken.None);

        Assert.NotNull(loaded);
        Assert.Equal(ReferenceLearningState.AwaitingReview, loaded!.State);
        Assert.Equal("", Assert.Single(loaded.Candidates).SemanticLabel);
        Assert.False(loaded.CanCommit);
    }

    private static ReferenceLearningDocument Document()
    {
        var layout = LayoutIntent.Create(
            "subject_cover",
            1,
            0,
            0,
            TemplateRect.Create(0, 0, 10, 10));
        var candidate = new ReferenceFrameCandidate(
            "learn_frame_1", "page_01", [0], 1, "front", "sha256:" + new string('c', 64),
            true, true, false, false, false, 255, "normal",
            [0, 0, 10, 0, 10, 10, 0, 10],
            ReferenceMatchState.Exact,
            "asset_aaaaaaaaaaaaaaaaaaaaaaaa",
            "front.png",
            new string('a', 64),
            "正面商品图",
            1,
            layout,
            ReferenceReviewDecision.Accepted)
        {
            SupersededLayers = [new ReferenceSupersededLayerEvidence([2], 2, true, true)],
            WindowLayerPath = [1],
            StructuralReason = "替换栈栈顶；已折叠 1 个被覆盖旧图层。"
        };
        var candidates = new[] { candidate };
        var photos = new[]
        {
            new ReferencePhotoFingerprint(
                "asset_aaaaaaaaaaaaaaaaaaaaaaaa", "front.png", "front.png", 3,
                new string('a', 64), "image/png", 10, 10)
        };
        return new ReferenceLearningDocument(
            "learn_roundtrip", "page_01", "templates/reference.psd",
            "sha256:" + new string('b', 64), 3, "sha256:" + new string('c', 64),
            new PsdCanvasInspection(10, 10, 8, "RGB"),
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
}
