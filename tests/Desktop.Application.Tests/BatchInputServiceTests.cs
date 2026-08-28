using Desktop.Application.Inputs;
using Desktop.Application.Templates;
using Desktop.Domain.Inputs;
using Desktop.Domain.Templates;

namespace Desktop.Application.Tests;

public sealed class BatchInputServiceTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 28, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task CreatesDraftFromCommittedTemplateWithoutSeparateSnapshotWrite()
    {
        var templates = new TemplateRepository();
        var repository = new BatchRepository();
        var service = CreateService(
            FileInventoryReadResult.Blocked(ScanBlockReason.EmptyLayout()),
            repository,
            templates: templates);

        var draft = await service.CreateDraftFromTemplateAsync(new CreateBatchFromTemplateCommand(
            "batch_01",
            "group_01",
            @"C:\输入",
            @"D:\输出",
            ["page_01"],
            BatchSelectionMode.Automatic,
            false));

        Assert.Equal(BatchScanStatus.Draft, draft.ScanStatus);
        Assert.Equal("revision_01", draft.TemplateSnapshot.RevisionId);
        Assert.Equal("group_01", await service.ResolveTemplateGroupIdAsync(draft));
        Assert.Same(draft, repository.Items["batch_01"]);
    }

    [Fact]
    public async Task RepeatedAutosaveReusesBatchSnapshotAndCreationTime()
    {
        var templates = new TemplateRepository();
        var repository = new BatchRepository();
        var clock = new MutableTimeProvider(Now);
        var service = CreateService(
            FileInventoryReadResult.Blocked(ScanBlockReason.EmptyLayout()),
            repository,
            templates: templates,
            timeProvider: clock);
        var command = CreateFromTemplateCommand(["page_01"]);

        var first = await service.CreateDraftFromTemplateAsync(command);
        clock.UtcNow = Now.AddMinutes(1);
        var second = await service.CreateDraftFromTemplateAsync(command with
        {
            OutputRoot = @"D:\新输出"
        });

        Assert.Equal(first.BatchId, second.BatchId);
        Assert.Equal(first.CreatedAt, second.CreatedAt);
        Assert.Equal(first.TemplateSnapshot.SnapshotId, second.TemplateSnapshot.SnapshotId);
        Assert.Equal(first.TemplateSnapshot.CreatedAt, second.TemplateSnapshot.CreatedAt);
        Assert.Equal(@"D:\新输出", second.OutputRoot);
        Assert.Equal(Now.AddMinutes(1), second.UpdatedAt);
    }

    [Fact]
    public async Task PageSelectionCanChangeBeforeScanningWithoutReplacingSnapshotIdentity()
    {
        var templates = new TemplateRepository();
        var repository = new BatchRepository();
        var service = CreateService(
            FileInventoryReadResult.Blocked(ScanBlockReason.EmptyLayout()),
            repository,
            templates: templates);
        var first = await service.CreateDraftFromTemplateAsync(CreateFromTemplateCommand(["page_01"]));

        var updated = await service.CreateDraftFromTemplateAsync(
            CreateFromTemplateCommand(["page_02"]));

        Assert.Equal(first.TemplateSnapshot.SnapshotId, updated.TemplateSnapshot.SnapshotId);
        Assert.Equal("page_02", Assert.Single(updated.TemplateSnapshot.Pages).PageId);
    }

    [Fact]
    public async Task ScanningAndReadyDraftsFreezeConfiguration()
    {
        var templates = new TemplateRepository();
        var repository = new BatchRepository();
        var service = CreateService(
            FileInventoryReadResult.Success(InputDirectoryInventory.Create(
                "单款",
                [File("正面.jpg")],
                [])),
            repository,
            templates: templates);
        var command = CreateFromTemplateCommand(["page_01"]);
        var draft = await service.CreateDraftFromTemplateAsync(command);
        await repository.SaveAsync(draft.BeginScanning(Now), CancellationToken.None);

        var scanningError = await Assert.ThrowsAsync<InputRuleViolationException>(
            () => service.CreateDraftFromTemplateAsync(command));
        Assert.Equal("INPUT_DRAFT_CONFIGURATION_FROZEN", scanningError.Message);

        await repository.SaveAsync(draft, CancellationToken.None);
        var ready = await service.ScanAsync(draft.BatchId);
        await repository.SaveAsync(ready, CancellationToken.None);
        Assert.Same(ready, await service.CreateDraftFromTemplateAsync(command));
        var readyError = await Assert.ThrowsAsync<InputRuleViolationException>(
            () => service.CreateDraftFromTemplateAsync(command with { IncludeEditablePsd = true }));
        Assert.Equal("INPUT_DRAFT_CONFIGURATION_FROZEN", readyError.Message);
    }

    [Fact]
    public async Task SingleStyleScanBuildsManifestAndReadyGraphForAiConsumer()
    {
        var repository = new BatchRepository();
        var committer = new Committer();
        var service = CreateService(FileInventoryReadResult.Success(InputDirectoryInventory.Create(
            "中文 单款 01",
            [File("正面 01.jpg")],
            [])), repository, committer);
        await service.CreateDraftAsync(CreateDraftCommand());

        var result = await service.ScanAsync("batch_01");

        Assert.Equal(BatchScanStatus.ScanReady, result.ScanStatus);
        Assert.Equal(BatchInputType.SingleStyle, result.InputType);
        Assert.Single(result.Styles);
        var asset = Assert.Single(result.Assets);
        Assert.Equal("正面 01.jpg", asset.SourceRelativePath);
        Assert.Equal(new string('a', 64), asset.Sha256);
        Assert.NotNull(committer.LastRequest);
        Assert.Equal(AssetManifestProtocol.Version, committer.LastRequest!.Manifest.SchemaVersion);
        Assert.DoesNotContain(@"C:\输入", committer.LastRequest.Manifest.Assets[0].SourceRelativePath);
    }

    [Fact]
    public async Task MixedLayoutPersistsBlockedStateAndNeverCallsCommitter()
    {
        var repository = new BatchRepository();
        var committer = new Committer();
        var service = CreateService(FileInventoryReadResult.Success(InputDirectoryInventory.Create(
            "混合",
            [File("root.jpg")],
            [FirstLevelDirectoryInventory.Create("A", "A", [File("A/child.jpg")])])), repository, committer);
        await service.CreateDraftAsync(CreateDraftCommand());

        var result = await service.ScanAsync("batch_01");

        Assert.Equal(BatchScanStatus.ScanBlocked, result.ScanStatus);
        Assert.Equal("INPUT_MIXED_LAYOUT", result.BlockReason?.Code);
        Assert.Empty(result.Styles);
        Assert.Empty(result.Assets);
        Assert.Null(committer.LastRequest);
    }

    [Fact]
    public async Task MetadataReadsStayWithinConfiguredConcurrency()
    {
        var files = Enumerable.Range(0, 40).Select(index => File($"图 {index:D2}.png")).ToArray();
        var metadata = new TrackingMetadataReader(delayMilliseconds: 5);
        var repository = new BatchRepository();
        var service = CreateService(
            FileInventoryReadResult.Success(InputDirectoryInventory.Create("批次", files, [])),
            repository,
            new Committer(),
            metadata,
            options: new BatchScanOptions(3));
        await service.CreateDraftAsync(CreateDraftCommand());

        var result = await service.ScanAsync("batch_01");

        Assert.Equal(BatchScanStatus.ScanReady, result.ScanStatus);
        Assert.InRange(metadata.MaximumConcurrency, 1, 3);
        Assert.Equal(40, metadata.ReadCount);
    }

    [Fact]
    public async Task CancellationPersistsBlockedWithoutManifestOrPartialAssets()
    {
        var metadata = new TrackingMetadataReader(delayMilliseconds: 10_000);
        var repository = new BatchRepository();
        var committer = new Committer();
        var service = CreateService(
            FileInventoryReadResult.Success(InputDirectoryInventory.Create(
                "批次",
                [File("图.jpg")],
                [])),
            repository,
            committer,
            metadata);
        await service.CreateDraftAsync(CreateDraftCommand());
        using var cancellation = new CancellationTokenSource();
        cancellation.CancelAfter(TimeSpan.FromMilliseconds(25));

        var result = await service.ScanAsync("batch_01", cancellation.Token);

        Assert.Equal(BatchScanStatus.ScanBlocked, result.ScanStatus);
        Assert.Equal("INPUT_SCAN_CANCELLED", result.BlockReason?.Code);
        Assert.Empty(result.Styles);
        Assert.Empty(result.Assets);
        Assert.Null(result.ManifestReference);
        Assert.Null(committer.LastRequest);
    }

    private static BatchInputService CreateService(
        FileInventoryReadResult inventory,
        BatchRepository repository,
        Committer? committer = null,
        IAssetMetadataReader? metadata = null,
        BatchScanOptions? options = null,
        ITemplateRepository? templates = null,
        TimeProvider? timeProvider = null) => new(
            new Inventory(inventory),
            metadata ?? new TrackingMetadataReader(0),
            repository,
            committer ?? new Committer(),
            timeProvider ?? new FixedTimeProvider(Now),
            options,
            templates);

    private static CreateBatchFromTemplateCommand CreateFromTemplateCommand(
        IReadOnlyList<string> selectedPageIds) => new(
            "batch_01",
            "group_01",
            @"C:\输入",
            @"D:\输出",
            selectedPageIds,
            BatchSelectionMode.Automatic,
            false);

    private static CreateBatchDraftCommand CreateDraftCommand()
    {
        var snapshot = BatchTemplateSnapshot.Create(
            "snapshot_01",
            "batch_01",
            TemplateRepository.Revision,
            ["page_01"],
            Now);
        return new CreateBatchDraftCommand(
            "batch_01",
            @"C:\输入",
            @"D:\输出",
            snapshot,
            BatchSelectionMode.Automatic,
            false);
    }

    private static InputFileReference File(string relativePath) =>
        InputFileReference.Create(relativePath, Path.GetFileName(relativePath));

    private sealed class Inventory(FileInventoryReadResult result) : IFileInventory
    {
        public Task<FileInventoryReadResult> ReadApprovedLevelsAsync(
            string inputRoot,
            CancellationToken cancellationToken) => Task.FromResult(result);
    }

    private sealed class TrackingMetadataReader(int delayMilliseconds) : IAssetMetadataReader
    {
        private int _current;
        private int _maximum;
        private int _count;

        public int MaximumConcurrency => _maximum;
        public int ReadCount => _count;

        public async Task<AssetMetadataReadResult> ReadAsync(
            string inputRoot,
            string sourceRelativePath,
            CancellationToken cancellationToken)
        {
            var current = Interlocked.Increment(ref _current);
            Interlocked.Increment(ref _count);
            UpdateMaximum(current);
            try
            {
                if (delayMilliseconds > 0)
                {
                    await Task.Delay(delayMilliseconds, cancellationToken);
                }
                return AssetMetadataReadResult.Success(new AssetFileMetadata(
                    1024,
                    new string('a', 64),
                    1200,
                    1600,
                    SourceImageFormat.Jpeg,
                    "image/jpeg"));
            }
            finally
            {
                Interlocked.Decrement(ref _current);
            }
        }

        private void UpdateMaximum(int value)
        {
            int observed;
            do
            {
                observed = _maximum;
                if (value <= observed)
                {
                    return;
                }
            }
            while (Interlocked.CompareExchange(ref _maximum, value, observed) != observed);
        }
    }

    private sealed class BatchRepository : IBatchRepository
    {
        public Dictionary<string, BatchDraft> Items { get; } = new(StringComparer.Ordinal);

        public Task SaveAsync(BatchDraft draft, CancellationToken cancellationToken)
        {
            Items[draft.BatchId] = draft;
            return Task.CompletedTask;
        }

        public Task<BatchDraft?> GetAsync(string batchId, CancellationToken cancellationToken) =>
            Task.FromResult(Items.GetValueOrDefault(batchId));

        public Task<IReadOnlyList<BatchDraft>> ListRecoverableAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<BatchDraft>>(Items.Values.ToArray());

        public Task DeleteDraftAsync(string batchId, CancellationToken cancellationToken)
        {
            Items.Remove(batchId);
            return Task.CompletedTask;
        }
    }

    private sealed class Committer : IBatchScanCommitter
    {
        public BatchScanCommitRequest? LastRequest { get; private set; }

        public Task<BatchScanCommitResult> CommitAsync(
            BatchScanCommitRequest request,
            CancellationToken cancellationToken)
        {
            LastRequest = request;
            var ready = request.ScanningDraft.MarkScanReady(
                request.InputType,
                request.Styles,
                request.Assets,
                $"batches/{request.ScanningDraft.BatchId}/asset-manifest/manifest.json",
                "sha256:" + new string('b', 64),
                Now.AddMinutes(1));
            return Task.FromResult(BatchScanCommitResult.Success(ready));
        }
    }

    private sealed class TemplateRepository : ITemplateRepository
    {
        public static TemplateRevision Revision { get; } = TemplateRevision.Restore(
            "revision_01",
            "group_01",
            1,
            TemplateRevisionState.Committed,
            "templates/revision_01/manifest.json",
            "sha256:" + new string('c', 64),
            Now,
            [TemplatePage.Create(
                "page_01",
                "revision_01",
                0,
                "详情页",
                TemplatePageContentType.NormalComposition,
                TemplateGenerationMode.OncePerProduct,
                null,
                true,
                "resource_01"),
             TemplatePage.Create(
                "page_02",
                "revision_01",
                1,
                "尺码页",
                TemplatePageContentType.NormalComposition,
                TemplateGenerationMode.OncePerProduct,
                null,
                true,
                "resource_02")],
            []);

        private static readonly TemplateGroup Group = TemplateGroup.Restore(
            "group_01", "模板", Revision.RevisionId, TemplateGroupStatus.Active, Now, Now);

        public Task<TemplateGroup?> GetGroupAsync(string groupId, CancellationToken cancellationToken) =>
            Task.FromResult<TemplateGroup?>(groupId == Group.GroupId ? Group : null);

        public Task<TemplateRevision?> GetRevisionAsync(string revisionId, CancellationToken cancellationToken) =>
            Task.FromResult<TemplateRevision?>(revisionId == Revision.RevisionId ? Revision : null);

        public Task<IReadOnlyList<TemplateGroup>> ListGroupsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<TemplateGroup>>([Group]);

        public Task SaveGroupAsync(TemplateGroup group, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task SaveDraftRevisionAsync(TemplateRevision revision, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<TemplateRevision?> GetLatestRevisionForGroupAsync(string groupId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task CommitRevisionAsync(TemplateRevision committedRevision, string? expectedCurrentRevisionId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task SaveCalibrationDraftAsync(CalibrationDraft draft, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<CalibrationDraft?> GetCalibrationDraftAsync(string draftId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<string>> GetBlockingBatchIdsAsync(string groupId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task DeleteGroupAsync(string groupId, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class MutableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = utcNow;

        public override DateTimeOffset GetUtcNow() => UtcNow;
    }
}
