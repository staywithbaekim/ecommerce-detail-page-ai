using Desktop.Application.Ai;
using Desktop.Application.Psd;
using Desktop.Application.Templates;
using Desktop.Contracts.Ai;
using Desktop.Contracts.Validation;
using Desktop.Domain.Inputs;
using Desktop.Domain.Templates;

namespace Desktop.Application.Tests;

public sealed class ReferenceLearningAiServiceTests
{
    [Fact]
    public async Task GeneratesReusableCategorySemanticsAndHitsVersionedCache()
    {
        var provider = new Provider(request => SemanticResult(request,
            "人物正面端坐图"));
        var cache = new MemoryCache();
        var service = CreateService(provider, cache: cache);
        var command = new ReferenceSemanticGenerationCommand(Document(), "C:\\reference", "模特展示页");

        var first = await service.GenerateSelectionRequirementsAsync(command);
        var second = await service.GenerateSelectionRequirementsAsync(command);

        Assert.True(first.IsSuccess);
        Assert.False(first.CacheHit);
        Assert.True(second.CacheHit);
        Assert.Equal(1, provider.Calls);
        var candidate = first.Document.Candidates[0];
        Assert.Equal("人物正面端坐图", candidate.SemanticLabel);
        Assert.Equal(ReferenceSemanticSource.AiV2, candidate.SemanticMetadata!.Source);
        Assert.Equal(ReferenceLearningAiProtocol.ResultSchemaVersion, candidate.SemanticMetadata.ResultSchemaVersion);
        Assert.NotEqual(command.Document.RulesDigest, first.Document.RulesDigest);
        Assert.Equal(AiAnalysisCopyProfiles.WicBoundedV4, provider.LastRequest!.CompressionProfileVersion);
        Assert.DoesNotContain("图层名称", provider.LastRequest.Frames[0].FrameContext, StringComparison.Ordinal);
        Assert.DoesNotContain("竖版构图", provider.LastRequest.Frames[0].FrameContext, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GeneratesSemanticsForWholeReferenceGroupWithOneProviderRequest()
    {
        var provider = new Provider(request => SemanticResult(request, "人物正面站立图"));
        var service = CreateService(provider);

        var result = await service.GenerateSelectionRequirementsAsync(
        [
            new ReferenceSemanticGenerationCommand(Document("learn_01", "page_01", "frame_01"), "C:\\reference", "第一页"),
            new ReferenceSemanticGenerationCommand(Document("learn_02", "page_02", "frame_02"), "C:\\reference", "第二页")
        ]);

        Assert.True(result.IsSuccess);
        Assert.Equal(1, provider.Calls);
        Assert.Equal(2, result.Documents.Count);
        Assert.All(result.Documents, document =>
            Assert.Equal(ReferenceSemanticSource.AiV2, Assert.Single(document.Candidates).SemanticMetadata!.Source));
    }

    [Fact]
    public async Task AiUnavailableFailsClosedAndCannotBeConfirmedAsLearnedSemantic()
    {
        var service = CreateService(new Provider(_ => throw new Xunit.Sdk.XunitException("provider must not run")), granted: false);

        var result = await service.GenerateSelectionRequirementsAsync(
            new ReferenceSemanticGenerationCommand(Document(), "C:\\reference", "页面"));

        Assert.False(result.IsSuccess);
        Assert.Equal("AI_UPLOAD_NOT_AUTHORIZED", result.Failure!.Code);
        Assert.Equal(ReferenceSemanticSource.AiUnavailable, result.Document.Candidates[0].SemanticMetadata!.Source);
        Assert.False(result.Document.Candidates[0].IsAutoConfirmable);
    }

    [Fact]
    public async Task UserOverrideInvalidatesAiCacheAndSurvivesServiceRecreation()
    {
        var cache = new MemoryCache();
        var sessions = new MemorySessions();
        var firstProvider = new Provider(request => SemanticResult(request, "人物正面站立图"));
        var firstService = CreateService(firstProvider, cache: cache, sessions: sessions);
        var generated = await firstService.GenerateSelectionRequirementsAsync(
            new ReferenceSemanticGenerationCommand(Document(), "C:\\reference", "页面"));

        var overridden = await firstService.ApplyUserOverrideAsync(
            generated.Document,
            generated.Document.Candidates[0].FrameId,
            "人物正面端坐图",
            CancellationToken.None);
        var secondProvider = new Provider(request => SemanticResult(request, "人物背面全身图"));
        var secondService = CreateService(secondProvider, cache: cache, sessions: sessions);
        var resumed = await secondService.GenerateSelectionRequirementsAsync(
            new ReferenceSemanticGenerationCommand(overridden, "C:\\reference", "页面"));

        Assert.Equal(1, cache.Invalidations);
        Assert.Equal(1, secondProvider.Calls);
        Assert.Equal("人物正面端坐图", resumed.Document.Candidates[0].SemanticLabel);
        Assert.True(resumed.Document.Candidates[0].SemanticMetadata!.UserOverridden);
        Assert.NotNull(sessions.Document);
    }

    [Fact]
    public async Task MismatchedContractVersionIsRejectedInsteadOfReused()
    {
        var provider = new Provider(request => SemanticResult(request, "人物正面端坐图") with
        {
            SchemaVersion = AiAnalysisProtocol.ResultSchemaVersion
        });
        var service = CreateService(provider);

        var result = await service.GenerateSelectionRequirementsAsync(
            new ReferenceSemanticGenerationCommand(Document(), "C:\\reference", "页面"));

        Assert.False(result.IsSuccess);
        Assert.Equal("TPL_LEARN_AI_RESULT_INVALID", result.Failure!.Code);
    }

    [Fact]
    public async Task CachedEntryWithOpenFrameSetIsNotReused()
    {
        var cache = new MemoryCache();
        var firstProvider = new Provider(request => SemanticResult(request, "人物正面站立图"));
        var command = new ReferenceSemanticGenerationCommand(Document(), "C:\\reference", "页面");
        await CreateService(firstProvider, cache: cache).GenerateSelectionRequirementsAsync(command);
        cache.Corrupt(entry => entry with
        {
            Result = entry.Result with
            {
                FrameResults = entry.Result.FrameResults.Select(frame => frame with { FrameId = "unknown_frame" }).ToArray()
            }
        });
        var secondProvider = new Provider(request => SemanticResult(request, "人物正面端坐图"));

        var result = await CreateService(secondProvider, cache: cache).GenerateSelectionRequirementsAsync(command);

        Assert.True(result.IsSuccess);
        Assert.False(result.CacheHit);
        Assert.Equal(1, secondProvider.Calls);
        Assert.Equal("人物正面端坐图", result.Document.Candidates[0].SemanticLabel);
    }

    [Fact]
    public async Task CachedEntryFromOldPromptVersionIsNotReused()
    {
        var cache = new MemoryCache();
        var command = new ReferenceSemanticGenerationCommand(Document(), "C:\\reference", "页面");
        await CreateService(new Provider(request => SemanticResult(request, "人物正面站立图")), cache: cache)
            .GenerateSelectionRequirementsAsync(command);
        cache.Corrupt(entry => entry with { PromptContractVersion = "reference-learning-semantic-prompt/v4" });
        var provider = new Provider(request => SemanticResult(request, "人物正面端坐图"));

        var result = await CreateService(provider, cache: cache).GenerateSelectionRequirementsAsync(command);

        Assert.True(result.IsSuccess);
        Assert.False(result.CacheHit);
        Assert.Equal(1, provider.Calls);
    }

    [Fact]
    public async Task UploadsOnlyDistinctPhotosActuallyReferencedByEffectiveFrames()
    {
        var document = Document();
        var unused = new ReferencePhotoFingerprint(
            "reference_unused", "unused-pocket.jpg", "unused-pocket.jpg", 120, new string('d', 64), "image/jpeg", 1000, 1500);
        document = document with
        {
            ReferencePhotos = [.. document.ReferencePhotos, unused]
        };
        var provider = new Provider(request => SemanticResult(request, "人物正面端坐图"));

        var result = await CreateService(provider).GenerateSelectionRequirementsAsync(
            new ReferenceSemanticGenerationCommand(document, "C:\\reference", "页面"));

        Assert.True(result.IsSuccess);
        var asset = Assert.Single(provider.LastRequest!.Assets);
        Assert.Equal("reference_01", asset.AssetId);
        Assert.DoesNotContain(provider.LastRequest.Assets, item => item.AssetId == unused.AssetId);
    }

    [Theory]
    [InlineData("竖版构图，中近景景别，人物坐在木质长凳上，双手捧着书卷，背景为浅米色墙面", "人物端坐图")]
    [InlineData("木质长凳人物正面端坐图", "人物正面端坐图")]
    public async Task ProjectsReferencePhotoNarrativeToReusableCategory(string narrative, string expected)
    {
        var provider = new Provider(request => SemanticResult(request, narrative));

        var result = await CreateService(provider).GenerateSelectionRequirementsAsync(
            new ReferenceSemanticGenerationCommand(Document(), "C:\\reference", "页面"));

        Assert.True(result.IsSuccess);
        Assert.Equal(expected, result.Document.Candidates[0].SemanticLabel);
        Assert.Equal(ReferenceSemanticSource.AiV2, result.Document.Candidates[0].SemanticMetadata!.Source);
    }

    [Fact]
    public async Task RejectsExactFrameWhenEmbeddedPhotoHashDoesNotMatchBeforeProvider()
    {
        var provider = new Provider(request => SemanticResult(request, "人物正面端坐图"));
        var document = Document() with
        {
            Candidates = [Document().Candidates[0] with { EmbeddedContentSha256 = Hash('c') }]
        };

        var result = await CreateService(provider).GenerateSelectionRequirementsAsync(
            new ReferenceSemanticGenerationCommand(document, "C:\\reference", "页面"));

        Assert.False(result.IsSuccess);
        Assert.Equal("TPL_LEARN_AI_REFERENCE_BINDING_INVALID", result.Failure!.Code);
        Assert.Equal(0, provider.Calls);
    }

    [Fact]
    public async Task RejectsNarrativeUserOverride()
    {
        var service = CreateService(new Provider(request => SemanticResult(request, "人物正面端坐图")));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => service.ApplyUserOverrideAsync(
            Document(),
            "frame_01",
            "人物坐在长凳上，背景为米色墙面",
            CancellationToken.None));

        Assert.Equal("TPL_LEARN_SELECTION_REQUIREMENT_INVALID", error.Message);
    }

    private static ReferenceLearningAiService CreateService(
        Provider provider,
        bool granted = true,
        MemoryCache? cache = null,
        MemorySessions? sessions = null) => new(
            provider,
            new Admission(granted),
            new Copies(),
            new Copies(),
            cache ?? new MemoryCache(),
            sessions ?? new MemorySessions(),
            new Usage());

    private static ReferenceLearningDocument Document(
        string learningId = "learn_01",
        string pageId = "page_01",
        string frameId = "frame_01")
    {
        var photo = new ReferencePhotoFingerprint(
            "reference_01", "sample.jpg", "sample.jpg", 100, new string('a', 64), "image/jpeg", 1000, 1500);
        var layout = LayoutIntent.Create("主体完整并按参考构图", 1, 0, 0, TemplateRect.Create(0, 0, 100, 100));
        var candidate = new ReferenceFrameCandidate(
            frameId, pageId, [0], 1, "审计层", Hash('b'), true, true, false, false, false,
            255, "normal", [0, 0, 100, 0, 100, 100, 0, 100], ReferenceMatchState.Exact,
            photo.AssetId, photo.RelativePath, photo.Sha256, "本地占位提示", 0.72, layout,
            PixelEvidence: new PsdLayerPixelEvidence(true, 100, 100, new PsdLayerBounds(0, 0, 100, 100)));
        return new ReferenceLearningDocument(
            learningId, pageId, "templates/reference.psd", Hash('p'), 100, Hash('s'),
            new PsdCanvasInspection(100, 100, 8, "RGB"), Hash('r'), [photo], [candidate],
            ReferenceLearningState.AwaitingReview,
            ReferenceLearningDigest.Compute(Hash('p'), Hash('s'), Hash('r'), [photo], [candidate]),
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
    }

    private static ReferenceLearningAiResultV2 SemanticResult(ReferenceLearningAiRequestV2 request, string requirement) => new(
        ReferenceLearningAiProtocol.ResultSchemaVersion, "result", request.Operation, request.RequestId, request.ModelConfigVersion,
        request.Frames.Select(frame => new ReferenceLearningFrameResultV2(
            frame.FrameId, requirement, ["参考组中该图的姿态和构图具有唯一性"], ["排除不同姿态与景别"], 0.97m)).ToArray(),
        [], new AiUsageSummaryV1("usage_01", 100, 20, 1, null, null));

    private static string Hash(char value) => "sha256:" + new string(value is >= 'a' and <= 'f' ? value : 'a', 64);
    private sealed class Provider(Func<ReferenceLearningAiRequestV2, ReferenceLearningAiResultV2> create) : IAiServicePort
    {
        public int Calls { get; private set; }
        public ReferenceLearningAiRequestV2? LastRequest { get; private set; }
        public Task<AiAnalysisExecutionResult> AnalyzeAsync(AiAnalysisRequestV1 request, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public Task<ReferenceLearningAiExecutionResult> AnalyzeReferenceLearningAsync(ReferenceLearningAiRequestV2 request, CancellationToken cancellationToken)
        {
            Calls++;
            LastRequest = request;
            return Task.FromResult(ReferenceLearningAiExecutionResult.Completed(create(request), new AiProviderUsage(100, 20, 120, false), TimeSpan.FromMilliseconds(1)));
        }
    }

    private sealed class Admission(bool granted) : IAiUploadAdmissionPort
    {
        public Task<AiUploadConsentRecord?> TryAcquireAsync(string scope, CancellationToken cancellationToken) => Task.FromResult<AiUploadConsentRecord?>(
            granted ? new AiUploadConsentRecord(scope, "consent_01", AiUploadConsentState.Granted, AiConsentPolicy.Version, DateTimeOffset.UtcNow) : null);
    }

    private sealed class Copies : IAiAnalysisCopyPreparer, IAiAnalysisCopyLeaseManager
    {
        public Task<AiAnalysisCopyMetadata> PrepareAsync(AiAnalysisCopyPreparationRequest request, CancellationToken cancellationToken) => Task.FromResult(
            new AiAnalysisCopyMetadata(request.AnalysisCopyReference, Hash('c'), 80, "image/jpeg", 800, 1200,
                request.CompressionProfileVersion, "lease_01", ["lease_01"]));
        public Task AcquireAsync(string analysisCopyReference, string leaseId, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ReleaseAsync(string analysisCopyReference, string leaseId, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class MemoryCache : IReferenceLearningAiCacheStore
    {
        private readonly Dictionary<string, ReferenceLearningAiCacheEntry> entries = new(StringComparer.Ordinal);
        private readonly Dictionary<string, ReferenceSemanticOverride> overrides = new(StringComparer.Ordinal);
        public int Invalidations { get; private set; }
        public void Corrupt(Func<ReferenceLearningAiCacheEntry, ReferenceLearningAiCacheEntry> mutate)
        {
            var key = Assert.Single(entries.Keys);
            entries[key] = mutate(entries[key]);
        }
        public Task<ReferenceLearningAiCacheEntry?> FindAsync(string cacheKey, CancellationToken cancellationToken) =>
            Task.FromResult(entries.TryGetValue(cacheKey, out var value) ? value : null);
        public Task SaveAsync(ReferenceLearningAiCacheEntry entry, CancellationToken cancellationToken) { entries[entry.CacheKey] = entry; return Task.CompletedTask; }
        public Task<bool> InvalidateAsync(string cacheKey, CancellationToken cancellationToken)
        {
            if (!entries.TryGetValue(cacheKey, out var value) || !value.IsReusable) return Task.FromResult(false);
            entries[cacheKey] = value with { IsReusable = false }; Invalidations++; return Task.FromResult(true);
        }
        public Task<ReferenceSemanticOverride?> FindOverrideAsync(string learningId, string frameId, CancellationToken cancellationToken) =>
            Task.FromResult(overrides.TryGetValue(learningId + "|" + frameId, out var value) ? value : null);
        public Task SaveOverrideAsync(ReferenceSemanticOverride value, CancellationToken cancellationToken) { overrides[value.LearningId + "|" + value.FrameId] = value; return Task.CompletedTask; }
    }

    private sealed class MemorySessions : IReferenceLearningSessionStore
    {
        public ReferenceLearningDocument? Document { get; private set; }
        public Task SaveAsync(ReferenceLearningDocument document, CancellationToken cancellationToken) { Document = document; return Task.CompletedTask; }
        public Task<ReferenceLearningDocument?> GetAsync(string learningId, CancellationToken cancellationToken) => Task.FromResult(Document);
    }

    private sealed class Usage : IAiUsageRecorder
    {
        public Task RecordAsync(AiUsageEvent usageEvent, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
