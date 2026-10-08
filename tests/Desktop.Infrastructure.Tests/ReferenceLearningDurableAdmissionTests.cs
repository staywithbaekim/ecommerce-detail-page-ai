using System.Security.Cryptography;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Desktop.Application.Ai;
using Desktop.Application.Psd;
using Desktop.Application.Templates;
using Desktop.Contracts.Ai;
using Desktop.Contracts.Validation;
using Desktop.Domain.Templates;
using Desktop.Infrastructure.Ai;
using Desktop.Infrastructure.Files;
using Desktop.Infrastructure.Persistence;
using Desktop.Infrastructure.Templates;
using Microsoft.Data.Sqlite;

namespace Desktop.Infrastructure.Tests;

// Offline evidence: real SQLite, files, WIC copies and learning service;
// only the paid provider boundary is replaced. No real-image claim.
public sealed class ReferenceLearningDurableAdmissionTests
{
    [Fact]
    public async Task CompletedResultAndUsageSurviveReopenWithoutSecondCall()
    {
        using var root = new TestDirectory();
        var command = Command(root.Path);
        string cacheKey;
        string requestId;
        await using (var store = await Store(root.Path))
        using (var copies = Copies(root.Path))
        {
            var provider = new Provider(Success);
            var result = await Service(root.Path, store, copies, provider).GenerateSelectionRequirementsAsync(command);
            Assert.True(result.IsSuccess, result.Failure?.Code);
            cacheKey = result.CacheKey!;
            requestId = provider.Request!.RequestId;
            Assert.Equal(123, Assert.Single(await store.LoadUsageAsync(requestId, default)).InputUnits);
        }
        await using var reopened = await Store(root.Path);
        using var copies2 = Copies(root.Path);
        var unused = new Provider(_ => throw new InvalidOperationException("must not run"));
        var cached = await Service(root.Path, reopened, copies2, unused).GenerateSelectionRequirementsAsync(command);
        Assert.True(cached.IsSuccess);
        Assert.True(cached.CacheHit);
        Assert.Equal(cacheKey, cached.CacheKey);
        Assert.Equal(0, unused.Calls);
        Assert.Single(await reopened.LoadUsageAsync(requestId, default));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnknownSurvivesReopenAndBlocksChangedPromptAndLearningId(bool throws)
    {
        using var root = new TestDirectory();
        var command = Command(root.Path);
        await using (var store = await Store(root.Path))
        using (var copies = Copies(root.Path))
        {
            var provider = new Provider(_ => throws ? throw new OperationCanceledException() :
                ReferenceLearningAiExecutionResult.Failure(AiProviderOutcome.AiResultUnknown,
                    new("unknown", false, "provider", "query first"), TimeSpan.Zero, new(123, 20, 143, false)));
            var result = await Service(root.Path, store, copies, provider).GenerateSelectionRequirementsAsync(command);
            Assert.Equal(AiProviderOutcome.AiResultUnknown, result.Failure!.Outcome);
            Assert.Equal(1, provider.Calls);
        }
        await using var reopened = await Store(root.Path);
        using var copies2 = Copies(root.Path);
        var unused = new Provider(Success);
        var changed = command with { PageContext = "另一个说明", Document = command.Document with { LearningId = "learn_again" } };
        var blocked = await Service(root.Path, reopened, copies2, unused).GenerateSelectionRequirementsAsync(changed);
        Assert.Equal("TPL_LEARN_AI_RESULT_UNKNOWN", blocked.Failure!.Code);
        Assert.Equal(0, unused.Calls);
    }

    [Fact]
    public async Task InvalidSemanticStillRecordsUsageAndCannotBeResubmitted()
    {
        using var root = new TestDirectory();
        await using var store = await Store(root.Path);
        using var copies = Copies(root.Path);
        var command = Command(root.Path);
        var provider = new Provider(request => Success(request) with
        { Result = Success(request).Result! with { RequestId = "foreign_request" } });
        var service = Service(root.Path, store, copies, provider);
        Assert.Equal("TPL_LEARN_AI_RESULT_INVALID", (await service.GenerateSelectionRequirementsAsync(command)).Failure!.Code);
        Assert.Equal("TPL_LEARN_AI_REQUEST_ALREADY_FINISHED", (await service.GenerateSelectionRequirementsAsync(command)).Failure!.Code);
        Assert.Equal(1, provider.Calls);
        Assert.Single(await store.LoadUsageAsync(provider.Request!.RequestId, default));
    }

    [Fact]
    public async Task PersistedBudgetBlocksProviderBeforeCall()
    {
        using var root = new TestDirectory();
        await using var store = await Store(root.Path);
        using var copies = Copies(root.Path);
        var command = Command(root.Path);
        var first = new Provider(Success);
        await Service(root.Path, store, copies, first).GenerateSelectionRequirementsAsync(command);
        var scope = await Scalar(root.Path, "SELECT batch_id FROM ai_request_ledger WHERE style_id='reference_learning';");
        var now = DateTimeOffset.UtcNow;
        await store.RecordRequestAsync(new("budget_seed", Hash('f'), scope!, "reference_learning", "Queued", null, now, now), default);
        await store.RecordUsageAsync(new("usage_budget_seed", "budget_seed", ReferenceLearningAiService.ModelConfigVersion,
            10_000_000, 0, false, 0, null, null, now), default);
        var provider = new Provider(Success);
        var result = await Service(root.Path, store, copies, provider).GenerateSelectionRequirementsAsync(command with { PageContext = "改变说明" });
        Assert.Equal("AI_BUDGET_HARD_STOP", result.Failure!.Code);
        Assert.Equal(0, provider.Calls);
    }

    [Fact]
    public async Task CacheWrittenBeforeDatabaseFailureClosesSameRequestOnRestart()
    {
        using var root = new TestDirectory();
        var command = Command(root.Path);
        await using (var store = await Store(root.Path))
        using (var copies = Copies(root.Path))
        {
            await Sql(root.Path, "CREATE TRIGGER fail_validation BEFORE UPDATE OF status ON ai_request_ledger WHEN NEW.status='Validated' BEGIN SELECT RAISE(ABORT,'injected commit window'); END;");
            var provider = new Provider(Success);
            await Assert.ThrowsAsync<SqliteException>(() => Service(root.Path, store, copies, provider).GenerateSelectionRequirementsAsync(command));
            Assert.Equal("Analysing", await Scalar(root.Path, "SELECT status FROM ai_request_ledger;"));
            await Sql(root.Path, "DROP TRIGGER fail_validation;");
        }
        await using var reopened = await Store(root.Path);
        using var copies2 = Copies(root.Path);
        var unused = new Provider(Success);
        Assert.True((await Service(root.Path, reopened, copies2, unused).GenerateSelectionRequirementsAsync(command)).CacheHit);
        Assert.Equal(0, unused.Calls);
        Assert.Equal("Validated", await Scalar(root.Path, "SELECT status FROM ai_request_ledger;"));
    }

    [Fact]
    public async Task ConcurrentSameRequestCallsProviderOnceAndOtherPageOverrideWins()
    {
        using var root = new TestDirectory();
        await using var store = await Store(root.Path);
        using var copies = Copies(root.Path);
        var first = Command(root.Path);
        var secondDocument = first.Document with { LearningId = "learn_second", PageId = "page_second",
            Candidates = [first.Document.Candidates[0] with { FrameId = "frame_second", PageId = "page_second" }] };
        var second = first with { Document = secondDocument };
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new Provider(Success) { Before = async () => { entered.SetResult(); await release.Task; } };
        var service = Service(root.Path, store, copies, provider);
        var pending = service.GenerateSelectionRequirementsAsync([first, second]);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            var contender = await service.GenerateSelectionRequirementsAsync([first, second]);
            Assert.Equal("TPL_LEARN_AI_RESULT_UNKNOWN", contender.Failure!.Code);
            await service.ApplyUserOverrideAsync(secondDocument, "frame_second", "假模特背面全身图");
        }
        finally { release.TrySetResult(); }
        var result = await pending;
        Assert.True(result.IsSuccess, result.Failure?.Code);
        Assert.Equal(1, provider.Calls);
        Assert.Equal("假模特背面全身图", result.Documents[1].Candidates[0].SemanticLabel);
        var persisted = await new JsonReferenceLearningSessionStore(Files(root.Path)).GetAsync("learn_second", default);
        Assert.Equal(ReferenceSemanticSource.UserOverride, persisted!.Candidates[0].SemanticMetadata!.Source);
    }

    private static ReferenceLearningAiService Service(string root, SqliteDesktopStore store, FileAiAnalysisCopyPreparer copies, Provider provider) =>
        new(provider, store, copies, copies, new JsonReferenceLearningAiCacheStore(Files(root)),
            new JsonReferenceLearningSessionStore(Files(root)), new InMemoryAiUsageRecorder(), store);
    private static AtomicFileStore Files(string root) => new(new LocalAppPathPolicy(root));
    private static FileAiAnalysisCopyPreparer Copies(string root) => new(new LocalAppPathPolicy(root), Files(root), new AiAnalysisCopyEncodingOptions());
    private static async Task<SqliteDesktopStore> Store(string root)
    {
        var store = new SqliteDesktopStore(new(root));
        await store.InitializeAsync();
        await store.GrantAsync(AiConsentScopes.AnalysisCopy, AiConsentPolicy.Version, default);
        return store;
    }
    private static ReferenceSemanticGenerationCommand Command(string root)
    {
        var path = Path.Combine(root, "source"); Directory.CreateDirectory(path);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(8, 8, 96, 96, PixelFormats.Rgb24, null, new byte[8 * 8 * 3], 24)));
        using var stream = new MemoryStream(); encoder.Save(stream);
        var bytes = stream.ToArray(); File.WriteAllBytes(Path.Combine(path, "sample.png"), bytes);
        var photo = new ReferencePhotoFingerprint("asset", "sample.png", "sample.png", bytes.Length,
            Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), "image/png", 8, 8);
        var candidate = new ReferenceFrameCandidate("frame", "page", [0], 1, "target", Hash('b'), true, true, false, false, false,
            255, "normal", [0, 0, 100, 0, 100, 100, 0, 100], ReferenceMatchState.Exact,
            photo.AssetId, photo.RelativePath, photo.Sha256, "未确认", 0.8,
            LayoutIntent.Create("主体完整", 1, 0, 0, TemplateRect.Create(0, 0, 100, 100)),
            PixelEvidence: new PsdLayerPixelEvidence(true, 100, 100, new PsdLayerBounds(0, 0, 100, 100)));
        var document = new ReferenceLearningDocument("learn", "page", "templates/source.psd", Hash('a'), 100, Hash('b'),
            new PsdCanvasInspection(100, 100, 8, "RGB"), Hash('c'), [photo], [candidate], ReferenceLearningState.AwaitingReview,
            ReferenceLearningDigest.Compute(Hash('a'), Hash('b'), Hash('c'), [photo], [candidate]), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        return new(document, path, "商品展示页");
    }
    private static ReferenceLearningAiExecutionResult Success(ReferenceLearningAiRequestV2 request) =>
        ReferenceLearningAiExecutionResult.Completed(new(ReferenceLearningAiProtocol.ResultSchemaVersion, "result", request.Operation,
            request.RequestId, request.ModelConfigVersion, request.Frames.Select(frame => new ReferenceLearningFrameResultV2(
                frame.FrameId, "真人模特正面全身图", ["主体正面"], [], 0.97m)).ToArray(), [], new("usage", 123, 20, 1, null, null)),
            new(123, 20, 143, false), TimeSpan.Zero);
    private static string Hash(char c) => "sha256:" + new string(c, 64);
    private static async Task<string?> Scalar(string root, string sql)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = new DesktopDataOptions(root).DatabasePath, Pooling = false }.ToString());
        await connection.OpenAsync(); await using var command = connection.CreateCommand(); command.CommandText = sql;
        return Convert.ToString(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }
    private static Task<string?> Sql(string root, string sql) => Scalar(root, sql);
    private sealed class Provider(Func<ReferenceLearningAiRequestV2, ReferenceLearningAiExecutionResult> result) : IAiServicePort
    {
        public int Calls { get; private set; }
        public ReferenceLearningAiRequestV2? Request { get; private set; }
        public Func<Task>? Before { get; init; }
        public Task<AiAnalysisExecutionResult> AnalyzeAsync(AiAnalysisRequestV1 request, CancellationToken token) => throw new NotSupportedException();
        public async Task<ReferenceLearningAiExecutionResult> AnalyzeReferenceLearningAsync(ReferenceLearningAiRequestV2 request, CancellationToken token)
        {
            Calls++; Request = request;
            if (Before is not null) await Before();
            return result(request);
        }
    }
}
