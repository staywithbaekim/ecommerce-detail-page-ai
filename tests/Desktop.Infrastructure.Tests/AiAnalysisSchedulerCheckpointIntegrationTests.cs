using Desktop.Application.Ai;
using Desktop.Contracts.Ai;
using Desktop.Contracts.Validation;
using Desktop.Infrastructure.Ai;
using Desktop.Infrastructure.Files;
using Desktop.Infrastructure.Persistence;

namespace Desktop.Infrastructure.Tests;

public sealed class AiAnalysisSchedulerCheckpointIntegrationTests
{
    [Fact]
    public async Task SchedulerCommitsLedgerResultUsageCacheAndEmptyPendingSnapshotAsOneRecoverableCheckpoint()
    {
        var root = Path.Combine(Path.GetTempPath(), "ai01-scheduler-checkpoint-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var options = new DesktopDataOptions(root);
        try
        {
            await using (var store = new SqliteDesktopStore(options))
            {
                await store.InitializeAsync();
                var policy = new LocalAppPathPolicy(root);
                var files = new AtomicFileStore(policy);
                var manifests = new AiAnalysisStateManifestStore(files);
                using var coordinator = new AiAnalysisCheckpointCoordinator(store, manifests, TimeProvider.System);
                var results = new FileAiAnalysisResultStore(files, new JsonSchemaContractValidator());
                using var queue = new BoundedAiAnalysisWorkBuffer(new AiAnalysisQueueOptions(2, 1));
                using var scheduler = new AiAnalysisScheduler(
                    new SuccessfulProvider(),
                    store,
                    queue,
                    new AiAnalysisBudgetPolicy(2, 1000, 1000, 10m, 1m, new AiAnalysisPricing(0.01m, 0.02m)),
                    stateStore: store,
                    stateKey: "batch-batch_01",
                    resultStore: results,
                    checkpointPort: coordinator);

                Assert.Equal(AiAnalysisWorkDisposition.Enqueued,
                    (await scheduler.EnqueueAsync(Request(), AiAnalysisWorkStage.ProductJointUnderstanding, CancellationToken.None)).Disposition);
                Assert.Equal(AiAnalysisWorkDisposition.Completed,
                    (await scheduler.ProcessNextAsync(CancellationToken.None)).Disposition);
                Assert.Equal("Validated", (await store.FindRequestByFingerprintAsync(
                    AiAnalysisCacheKeyFactory.Create(Request()), CancellationToken.None))!.Status);
                Assert.NotNull(await results.LoadAsync("results/request_01", CancellationToken.None));
                Assert.Empty((await store.LoadSchedulingStateAsync("batch-batch_01", CancellationToken.None))!.PendingItems);
                Assert.True((await manifests.InspectAsync("batch_01", CancellationToken.None))!.IsCommitted);
            }

            await using var reopened = new SqliteDesktopStore(options);
            await reopened.InitializeAsync();
            var reopenedFiles = new AtomicFileStore(new LocalAppPathPolicy(root));
            using var reopenedCoordinator = new AiAnalysisCheckpointCoordinator(
                reopened,
                new AiAnalysisStateManifestStore(reopenedFiles),
                TimeProvider.System);
            using var reopenedQueue = new BoundedAiAnalysisWorkBuffer(new AiAnalysisQueueOptions(2, 1));
            using var reopenedScheduler = new AiAnalysisScheduler(
                new SuccessfulProvider(),
                reopened,
                reopenedQueue,
                new AiAnalysisBudgetPolicy(2, 1000, 1000, 10m, 1m, new AiAnalysisPricing(0.01m, 0.02m)),
                stateStore: reopened,
                stateKey: "batch-batch_01",
                resultStore: new FileAiAnalysisResultStore(reopenedFiles, new JsonSchemaContractValidator()),
                checkpointPort: reopenedCoordinator);
            Assert.Equal(0, await reopenedScheduler.RestoreAsync(CancellationToken.None));
            Assert.Equal(0, reopenedQueue.Count);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static AiAnalysisRequestV1 Request() => new(
        AiAnalysisProtocol.RequestSchemaVersion,
        "request",
        "request_01",
        "correlation_01",
        "product_01",
        "batch_01",
        "style_01",
        "snapshot_01",
        "revision_01",
        AiAnalysisKind.ProductJointUnderstanding,
        "model/proposed",
        "prompt/v1",
        AiAnalysisProtocol.ResultSchemaVersion,
        AiAnalysisCopyProfiles.BytePreservingProvisionalV1,
        "consent_01",
        "sha256:" + new string('a', 64),
        [new AiAssetInputV1("asset_01", "sha256:" + new string('b', 64), "image/jpeg", 10, 10, "copy_01")],
        [new AiRequirementInputV1("frame_01", "商品图", "居中", true)]);

    private sealed class SuccessfulProvider : IAiServicePort
    {
        public Task<AiAnalysisExecutionResult> AnalyzeAsync(AiAnalysisRequestV1 request, CancellationToken cancellationToken) =>
            Task.FromResult(AiAnalysisExecutionResult.Completed(
                new AiAnalysisResultV1(
                    AiAnalysisProtocol.ResultSchemaVersion,
                    "result",
                    request.RequestId,
                    null,
                    request.ModelConfigVersion,
                    [], [], [], null, [],
                    new AiUsageSummaryV1("usage_" + request.RequestId, 1, 1, 1, null, null)),
                new AiProviderUsage(1, 1, 2, false),
                TimeSpan.Zero));
    }
}
