using System.Windows.Input;
using Desktop.Application.Inputs;
using Desktop.Application.Tasks;
using Desktop.Domain.Templates;
using Desktop.Infrastructure;
using Desktop.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace Desktop.App.UiTests;

public sealed class BatchInputViewModelTests
{
    [Theory]
    [InlineData(null)]
    [InlineData(BatchOutputMode.UnifiedDirectory)]
    [InlineData(BatchOutputMode.StyleSourceDirectories)]
    public async Task RealAdapterPersistsAndRestoresBothPublicModesWithoutExposingPrivateEvidence(BatchOutputMode? mode)
    {
        var root = Path.Combine(Path.GetTempPath(), "d7-ui-adapter-" + Guid.NewGuid().ToString("N"));
        var source = Directory.CreateDirectory(Path.Combine(root, "source")).FullName;
        var output = Directory.CreateDirectory(Path.Combine(root, "public")).FullName;
        try
        {
            // A local scanner fixture, not Photoshop/image-generation acceptance.
            var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAQAAAADCAIAAAA7ljmRAAAAEUlEQVR4nGP8z4AATEhsVA4AJnYBBZNezToAAAAASUVORK5CYII=");
            await File.WriteAllBytesAsync(Path.Combine(source, "front.png"), png);
            var services = new ServiceCollection().AddDesktopFoundation(new DesktopDataOptions(Path.Combine(root, "app")));
            await using var provider = services.BuildServiceProvider();
            var store = provider.GetRequiredService<SqliteDesktopStore>();
            await store.InitializeAsync();
            var group = TemplateGroup.Create("group_ui", "UI输出验证", DateTimeOffset.UtcNow);
            var page = TemplatePage.Create("page_ui", "revision_ui", 0, "主图", TemplatePageContentType.NormalComposition,
                TemplateGenerationMode.OncePerProduct, null, true, "resource_ui");
            var revision = TemplateRevision.CreateDraft("revision_ui", group.GroupId, 1, DateTimeOffset.UtcNow, [page])
                .AdvanceTo(TemplateRevisionState.ImportingCopy, new(SourceReadable: true))
                .AdvanceTo(TemplateRevisionState.StructureInspecting, new(CopyMatchesSource: true))
                .AdvanceTo(TemplateRevisionState.FrameBinding, new(StructureReadable: true))
                .AdvanceTo(TemplateRevisionState.Calibrating, new(RegisteredFramesHaveEvidence: true))
                .AdvanceTo(TemplateRevisionState.AwaitingReview, new(CalibrationComplete: true))
                .AdvanceTo(TemplateRevisionState.Committing, new(ConfigurationValid: true))
                .Commit("templates/ui/manifest.json", "sha256:" + new string('a', 64), new(CommitArtifactsConsistent: true));
            await store.SaveGroupAsync(group, default);
            await store.CommitRevisionAsync(revision, null, default);
            var adapter = new BatchInputWorkflowAdapter(provider.GetRequiredService<BatchInputWorkflowFacade>(),
                provider.GetRequiredService<TaskCoordinator>());
            var command = new BatchInputSubmission(null, group.GroupId, source,
                mode == BatchOutputMode.StyleSourceDirectories ? "" : output, [page.PageId], BatchSelectionMode.Automatic, false, mode);
            var saved = await adapter.SaveDraftAsync(command, default);
            var ready = await adapter.ScanAsync(command with { BatchId = saved.BatchId }, default);
            Assert.True(ready.Status == BatchInputUiStatus.ScanReady, ready.StatusMessage);
            Assert.Equal(mode, ready.OutputMode);
            Assert.Equal(command.OutputDirectory, ready.OutputDirectory);
            var persisted = (await store.GetAsync(ready.BatchId, default))!;
            Assert.Equal(mode is null, persisted.OutputSelection is null);
            if (mode is not null)
            {
                Assert.StartsWith(Path.Combine(root, "app") + Path.DirectorySeparatorChar, persisted.OutputRoot,
                    StringComparison.OrdinalIgnoreCase);
                Assert.NotEqual(persisted.OutputRoot, ready.OutputDirectory);
            }
            var reopened = new BatchInputWorkflowAdapter(provider.GetRequiredService<BatchInputWorkflowFacade>(),
                provider.GetRequiredService<TaskCoordinator>());
            var restored = await reopened.RestoreBatchAsync(ready.BatchId, default);
            Assert.Equal(System.Text.Json.JsonSerializer.Serialize(ready), System.Text.Json.JsonSerializer.Serialize(restored));
            using var model = new MainWindowViewModel(new AppLaunchOptions(true, null));
            model.BatchInputPageViewModel.State.ApplyProjection(restored!);
            Assert.Equal(mode is null, ((IResultReviewHost)model).CanEditRenderedBatch(ready.BatchId));
            Assert.Equal(mode is null, ((ISizeTableEditorHost)model).CanEditRenderedBatch(ready.BatchId));
            Assert.False(((IResultReviewHost)model).CanEditRenderedBatch("another-batch"));
            Assert.Empty(Directory.EnumerateFileSystemEntries(output));
            Assert.Single(Directory.EnumerateFileSystemEntries(source));
            Assert.Equal(png, await File.ReadAllBytesAsync(Path.Combine(source, "front.png")));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void SourceModeNeedsNoSharedOutputPickerAndIsFrozenAfterScan()
    {
        var workflow = new BatchWorkflow();
        var host = new BatchHost();
        using var page = CreateReadyPage(workflow, host);
        page.State.OutputDirectory = "";
        Assert.False(page.CanScanBatch);
        page.IsBatchStyleSourceOutput = true;
        Assert.True(page.CanScanBatch);
        Assert.True(page.IsBatchStyleSourceOutput);
        Assert.False(page.ChooseBatchOutputDirectoryCommand.CanExecute(null));
        Assert.Contains("各款实际源目录", page.BatchOutputDirectoryDisplay, StringComparison.Ordinal);
        page.ScanBatchInputCommand.Execute(null);
        Assert.Equal(BatchOutputMode.StyleSourceDirectories, Assert.Single(workflow.Scans).OutputMode);
        Assert.True(page.IsBatchScanReady);
        page.IsBatchUnifiedOutput = true;
        Assert.True(page.IsBatchStyleSourceOutput);
        Assert.False(page.CanScanBatch);
        Assert.Equal("scanned_batch", host.StartedBatch);
    }

    [Theory]
    [InlineData(BatchOutputMode.UnifiedDirectory)]
    [InlineData(BatchOutputMode.StyleSourceDirectories)]
    [InlineData(null)]
    public void RestorePreservesExplicitOrLegacyOutputModeWithoutRescanning(BatchOutputMode? mode)
    {
        var workflow = new BatchWorkflow { Restored = Projection("restored", BatchInputUiStatus.ScanReady) with
            { OutputMode = mode, OutputDirectory = mode == BatchOutputMode.StyleSourceDirectories ? "" : @"C:\confirmed" } };
        var host = new BatchHost();
        using var view = new BatchInputViewModel(null, workflow, null, host);
        view.RestoreBatchInputCommand.Execute(null);
        Assert.Equal(mode, view.State.OutputMode);
        Assert.False(view.CanChooseBatchOutputDirectory);
        Assert.Empty(workflow.Scans);
        Assert.Equal(0, workflow.Saves);
        Assert.Equal("restored", host.RestoredBatch);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NewDraftOpenFailuresReportFeedbackAndKeepCurrentIdentity(bool unexpected)
    {
        var workflow = new BatchWorkflow();
        var host = new BatchHost { LibraryError = unexpected ? new UnauthorizedAccessException("test") : new IOException("test") };
        using var page = CreateReadyPage(workflow, host);
        page.CurrentBatchId = "old_batch";

        page.OpenBatchInputCommand.Execute(null);

        Assert.Contains("暂时无法打开", page.BatchSwitchStatusText, StringComparison.Ordinal);
        Assert.Equal("old_batch", page.CurrentBatchId);
        Assert.Equal(@"C:\test-input", page.BatchInputDirectory);
        Assert.Null(host.Screen);
        Assert.False(page.BatchBusy);
        Assert.True(page.OpenBatchInputCommand.CanExecute(null));
        Assert.Empty(workflow.Scans);
        Assert.Equal(0, workflow.Saves);
        Assert.Null(host.StartedBatch);

        host.LibraryError = null;
        page.OpenBatchInputCommand.Execute(null);
        Assert.Empty(page.BatchSwitchStatusText);
        Assert.Equal("batch", host.Screen);
        Assert.Null(page.CurrentBatchId);
        Assert.Empty(workflow.Scans);
        Assert.Equal(0, workflow.Saves);
    }

    [Fact]
    public async Task NewDraftRechecksSwitchGateAfterAwaitAndReportsTheRefusal()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var workflow = new BatchWorkflow();
        var host = new BatchHost { LibraryGate = gate.Task };
        using var page = CreateReadyPage(workflow, host);
        page.CurrentBatchId = "old_batch";
        var settled = WhenCommandSettles(page.OpenBatchInputCommand, () => host.LibraryLoads > 0 && !page.BatchBusy);
        page.OpenBatchInputCommand.Execute(null);
        Assert.True(page.BatchBusy);
        host.CanSwitchBatch = false;
        gate.SetResult();
        await settled;

        Assert.Contains("尚未收口", page.BatchSwitchStatusText, StringComparison.Ordinal);
        Assert.Equal("old_batch", page.CurrentBatchId);
        Assert.Null(host.Screen);
        Assert.False(page.OpenBatchInputCommand.CanExecute(null));
        Assert.Empty(workflow.Scans);
        Assert.Equal(0, workflow.Saves);
        Assert.Null(host.StartedBatch);
    }

    [Fact]
    public void MissingConsentDoesNotScanOrStartAi()
    {
        var workflow = new BatchWorkflow();
        var host = new BatchHost { ConsentGranted = false };
        using var page = CreateReadyPage(workflow, host);

        page.ScanBatchInputCommand.Execute(null);

        Assert.Equal(1, host.ConsentChecks);
        Assert.Empty(workflow.Scans);
        Assert.Null(host.StartedBatch);
        Assert.False(page.BatchBusy);
        Assert.Equal(BatchInputUiStatus.Draft, page.State.Status);
    }

    [Fact]
    public async Task CancelledScanReleasesBusyAndRetryUsesNewBatchIdentity()
    {
        var workflow = new BatchWorkflow { WaitForCancellation = true };
        var host = new BatchHost();
        using var page = CreateReadyPage(workflow, host);
        page.CurrentBatchId = "old_batch";
        var finished = WhenCommandSettles(page.ScanBatchInputCommand,
            () => workflow.Scans.Count > 0 && !page.BatchBusy);

        page.ScanBatchInputCommand.Execute(null);
        Assert.True(page.IsBatchScanning);
        Assert.True(page.CancelBatchScanCommand.CanExecute(null));
        Assert.False(page.ChooseBatchInputDirectoryCommand.CanExecute(null));
        page.CancelBatchScanCommand.Execute(null);
        await finished;

        Assert.True(workflow.ScanToken.IsCancellationRequested);
        Assert.True(page.IsBatchScanBlocked);
        Assert.Contains("扫描已取消", page.BatchStatusMessage, StringComparison.Ordinal);
        Assert.Null(host.StartedBatch);
        workflow.WaitForCancellation = false;
        page.RetryBatchScanCommand.Execute(null);

        Assert.Null(workflow.Scans[1].BatchId);
        Assert.Equal("scanned_batch", page.CurrentBatchId);
        Assert.Equal("scanned_batch", host.StartedBatch);
        Assert.True(page.IsBatchScanReady);
        Assert.False(page.BatchBusy);
    }

    [Fact]
    public async Task PendingOldDraftCannotOverwriteNewBatchBeforeSerializedScan()
    {
        var saveGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var workflow = new BatchWorkflow { SaveGate = saveGate.Task };
        var host = new BatchHost();
        using var page = CreateReadyPage(workflow, host);
        page.ChooseBatchInputDirectoryCommand.Execute(null);
        Assert.Equal(1, workflow.Saves);
        page.CancelBatchInputCommand.Execute(null);
        Assert.Equal("workbench", host.Screen);
        Assert.Null(page.CurrentBatchId);
        var finished = WhenCommandSettles(page.ScanBatchInputCommand,
            () => workflow.Scans.Count > 0 && !page.BatchBusy);

        page.ScanBatchInputCommand.Execute(null);
        Assert.Empty(workflow.Scans);
        saveGate.SetResult();
        await finished;

        Assert.Null(Assert.Single(workflow.Scans).BatchId);
        Assert.Equal("scanned_batch", page.CurrentBatchId);
        Assert.Equal("scanned_batch", host.StartedBatch);
        Assert.False(page.BatchBusy);
    }

    [Fact]
    public void RestoreAppliesSavedSettingsBeforeAiHandoffAndDoesNotRescan()
    {
        var workflow = new BatchWorkflow
        {
            Restored = Projection("restored_batch", BatchInputUiStatus.ScanReady) with
            {
                SelectionMode = BatchSelectionMode.Manual,
                IncludeEditablePsd = true,
                InputDirectory = @"C:\restored-input"
            }
        };
        var host = new BatchHost();
        using var page = new BatchInputViewModel(null, workflow, null, host);
        host.OnRestore = batchId =>
        {
            Assert.Equal("restored_batch", page.CurrentBatchId);
            Assert.Equal(@"C:\restored-input", page.BatchInputDirectory);
            Assert.True(page.IsBatchManualSelection);
            Assert.True(page.BatchIncludeEditablePsd);
            Assert.Equal(page.CurrentBatchId, batchId);
        };

        page.RestoreBatchInputCommand.Execute(null);

        Assert.Equal("restored_batch", host.RestoredBatch);
        Assert.Equal(1, host.LibraryLoads);
        Assert.Empty(workflow.Scans);
        Assert.Equal(0, workflow.Saves);
        Assert.Null(host.StartedBatch);
    }

    [Fact]
    public async Task ListingBatchesDoesNotLoadTemplatesScanSaveOrHandOffToAi()
    {
        var workflow = new BatchWorkflow();
        foreach (var status in Enum.GetValues<BatchInputUiStatus>())
            workflow.Stored.Add(status.ToString(), Projection(status.ToString(), status));
        var host = new BatchHost();
        using var page = CreateReadyPage(workflow, host);
        page.CurrentBatchId = "current_batch";

        var batches = await page.ListRecoverableAsync(CancellationToken.None);

        Assert.Equal(4, batches.Count);
        Assert.Equal("current_batch", page.CurrentBatchId);
        Assert.Equal(0, host.LibraryLoads);
        Assert.Equal(0, host.ConsentChecks);
        Assert.Equal(0, workflow.LatestRestores);
        Assert.Empty(workflow.RestoreRequests);
        Assert.Empty(workflow.Scans);
        Assert.Equal(0, workflow.Saves);
        Assert.Null(host.RestoredBatch);
        Assert.Null(host.StartedBatch);
        Assert.Null(host.Screen);
    }

    [Fact]
    public async Task ExplicitSelectionRestoresOlderBatchSettingsAndNeverUsesLatest()
    {
        var workflow = new BatchWorkflow { Restored = Projection("batch_B", BatchInputUiStatus.ScanReady) };
        workflow.Stored.Add("batch_A", Projection("batch_A", BatchInputUiStatus.ScanReady) with
        {
            InputDirectory = @"C:\older-input",
            OutputDirectory = @"C:\older-output",
            SelectionMode = BatchSelectionMode.Manual,
            IncludeEditablePsd = true
        });
        var host = new BatchHost();
        using var page = CreateReadyPage(workflow, host);
        page.CurrentBatchId = "batch_B";
        host.OnRestore = batchId =>
        {
            Assert.Equal("batch_A", batchId);
            Assert.Equal(batchId, page.CurrentBatchId);
            Assert.Equal(@"C:\older-input", page.BatchInputDirectory);
            Assert.Equal(@"C:\older-output", page.BatchOutputDirectory);
            Assert.True(page.IsBatchManualSelection);
            Assert.True(page.BatchIncludeEditablePsd);
        };

        await page.RestoreBatchAsync("batch_A");

        Assert.Equal("batch_A", Assert.Single(workflow.RestoreRequests));
        Assert.Equal("batch_A", host.RestoredBatch);
        Assert.Equal(0, workflow.LatestRestores);
        Assert.Equal(0, host.ConsentChecks);
        Assert.Empty(workflow.Scans);
        Assert.Equal(0, workflow.Saves);
        Assert.Null(host.StartedBatch);
        Assert.False(page.BatchBusy);
    }

    [Theory]
    [InlineData(BatchInputUiStatus.Draft)]
    [InlineData(BatchInputUiStatus.Scanning)]
    [InlineData(BatchInputUiStatus.ScanBlocked)]
    public async Task UnscannedSelectionOnlyRestoresInputWithoutStartingWork(BatchInputUiStatus status)
    {
        var workflow = new BatchWorkflow();
        workflow.Stored.Add("batch_A", Projection("batch_A", status));
        var host = new BatchHost();
        using var page = new BatchInputViewModel(null, workflow, null, host);

        await page.RestoreBatchAsync("batch_A");

        Assert.Equal("batch_A", page.CurrentBatchId);
        Assert.Equal(status, page.State.Status);
        Assert.Equal("batch", host.Screen);
        Assert.Null(host.RestoredBatch);
        Assert.Null(host.StartedBatch);
        Assert.Empty(workflow.Scans);
        Assert.Equal(0, workflow.Saves);
    }

    [Fact]
    public async Task MissingSelectionDoesNotFallBackOrClearTheCurrentBatch()
    {
        var workflow = new BatchWorkflow { Restored = Projection("latest_B", BatchInputUiStatus.ScanReady) };
        var host = new BatchHost();
        using var page = CreateReadyPage(workflow, host);
        page.CurrentBatchId = "current_B";

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => page.RestoreBatchAsync("missing_A"));

        Assert.Equal("INPUT_BATCH_NOT_FOUND", error.Message);
        Assert.Equal("current_B", page.CurrentBatchId);
        Assert.Equal(0, workflow.LatestRestores);
        Assert.Equal(0, host.LibraryLoads);
        Assert.Null(host.RestoredBatch);
        Assert.Null(host.Screen);
        Assert.False(page.BatchBusy);
    }

    [Fact]
    public async Task PendingOldAutosaveCannotReplaceExplicitlySelectedBatch()
    {
        var saveGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var workflow = new BatchWorkflow { SaveGate = saveGate.Task };
        workflow.Stored.Add("batch_A", Projection("batch_A", BatchInputUiStatus.ScanReady));
        var host = new BatchHost();
        using var page = CreateReadyPage(workflow, host);
        page.CurrentBatchId = "batch_B";
        page.ChooseBatchInputDirectoryCommand.Execute(null);
        Assert.Equal(1, workflow.Saves);

        var restoring = page.RestoreBatchAsync("batch_A");
        Assert.True(page.BatchBusy);
        Assert.False(page.OpenBatchInputCommand.CanExecute(null));
        Assert.False(page.RestoreBatchInputCommand.CanExecute(null));
        Assert.False(page.ScanBatchInputCommand.CanExecute(null));
        Assert.False(page.CancelBatchInputCommand.CanExecute(null));
        Assert.Empty(workflow.RestoreRequests);
        await Assert.ThrowsAsync<InvalidOperationException>(() => page.RestoreBatchAsync("batch_B"));
        saveGate.SetResult();
        await restoring.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("batch_A", page.CurrentBatchId);
        Assert.Equal("batch_A", host.RestoredBatch);
        Assert.Equal(1, workflow.Saves);
        Assert.Empty(workflow.Scans);
        Assert.Null(host.StartedBatch);
        Assert.False(page.BatchBusy);
    }

    [Fact]
    public async Task ExplicitRestoreRejectsMismatchedIdentityWithoutApplyingIt()
    {
        var workflow = new BatchWorkflow();
        workflow.Stored.Add("batch_A", Projection("batch_B", BatchInputUiStatus.ScanReady));
        var host = new BatchHost();
        using var page = new BatchInputViewModel(null, workflow, null, host);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => page.RestoreBatchAsync("batch_A"));

        Assert.Equal("INPUT_BATCH_ID_MISMATCH", error.Message);
        Assert.Null(page.CurrentBatchId);
        Assert.Null(host.RestoredBatch);
        Assert.False(page.BatchBusy);
    }

    [Theory]
    [InlineData(false, "目录暂时不可访问")]
    [InlineData(true, "清单与数据库记录不一致")]
    public void ScanFailuresRemainBlockedWithoutAiHandoff(bool inconsistent, string message)
    {
        var workflow = new BatchWorkflow
        {
            ScanError = inconsistent
                ? new InvalidOperationException("INPUT_MANIFEST_DATABASE_MISMATCH")
                : new IOException("test")
        };
        var host = new BatchHost();
        using var page = CreateReadyPage(workflow, host);

        page.ScanBatchInputCommand.Execute(null);

        Assert.True(page.IsBatchScanBlocked);
        Assert.Contains(message, page.BatchStatusMessage, StringComparison.Ordinal);
        Assert.False(page.BatchBusy);
        Assert.True(page.RetryBatchScanCommand.CanExecute(null));
        Assert.Null(host.StartedBatch);
    }

    private static Task WhenCommandSettles(ICommand command, Func<bool> finished)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler? handler = null;
        handler = (_, _) =>
        {
            if (!finished()) return;
            command.CanExecuteChanged -= handler;
            completion.TrySetResult();
        };
        command.CanExecuteChanged += handler;
        return completion.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static BatchInputViewModel CreateReadyPage(BatchWorkflow workflow, BatchHost host)
    {
        var page = new BatchInputViewModel(new FolderPicker(), workflow, null, host);
        page.State.SelectedTemplate = host.TemplateGroups[0];
        page.State.InputDirectory = @"C:\test-input";
        page.State.OutputDirectory = @"C:\test-output";
        page.BatchPages.Add(new("page_1", "page", "test", true));
        Assert.True(page.CanScanBatch);
        return page;
    }

    private static BatchInputProjection Projection(string batchId, BatchInputUiStatus status) =>
        new(batchId, "group_1", status, @"C:\test-input", @"C:\test-output",
            ["page_1"], BatchSelectionMode.Automatic, false, "recognized", "status");

    private sealed class FolderPicker : IFolderPicker
    {
        public Task<string?> PickFolderAsync(string title, string? initialDirectory,
            CancellationToken cancellationToken) => Task.FromResult<string?>(@"C:\new-input");
    }

    private sealed class BatchHost : IBatchInputHost
    {
        public bool CanSwitchBatch { get; set; } = true;
        public Exception? LibraryError { get; set; }
        public Task? LibraryGate { get; init; }
        public IReadOnlyList<TemplateLibraryCardViewModel> TemplateGroups { get; } =
            [new("group_1", "template", "1 page", "today", "Committed", HasCommittedRevision: true)];
        public bool ConsentGranted { get; init; } = true;
        public int ConsentChecks { get; private set; }
        public int LibraryLoads { get; private set; }
        public string? StartedBatch { get; private set; }
        public string? RestoredBatch { get; private set; }
        public string? Screen { get; private set; }
        public Action<string>? OnRestore { get; set; }
        public Task LoadTemplateLibraryAsync()
        {
            LibraryLoads++;
            if (LibraryError is not null) throw LibraryError;
            return LibraryGate ?? Task.CompletedTask;
        }
        public void ShowBatchInput() => Screen = "batch";
        public void ShowWorkbench() => Screen = "workbench";
        public void CreateTemplate() => Screen = "new-template";
        public void ImportTemplate() => Screen = "import-template";
        public Task<bool> EnsureUploadConsentAsync() { ConsentChecks++; return Task.FromResult(ConsentGranted); }
        public Task StartAiAnalysisAsync(string batchId) { StartedBatch = batchId; return Task.CompletedTask; }
        public Task RestoreAiAnalysisAsync(string batchId)
        {
            OnRestore?.Invoke(batchId);
            RestoredBatch = batchId;
            return Task.CompletedTask;
        }
    }

    private sealed class BatchWorkflow : IBatchInputWorkflow
    {
        public BatchInputProjection? Restored { get; init; }
        public Dictionary<string, BatchInputProjection> Stored { get; } = new(StringComparer.Ordinal);
        public List<string> RestoreRequests { get; } = [];
        public int LatestRestores { get; private set; }
        public Task? SaveGate { get; init; }
        public Exception? ScanError { get; init; }
        public bool WaitForCancellation { get; set; }
        public CancellationToken ScanToken { get; private set; }
        public List<BatchInputSubmission> Scans { get; } = [];
        public int Saves { get; private set; }

        public Task<BatchInputProjection?> RestoreLatestDraftAsync(CancellationToken cancellationToken)
        {
            LatestRestores++;
            return Task.FromResult(Restored);
        }

        public Task<IReadOnlyList<BatchRecoverySummary>> ListRecoverableAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<BatchRecoverySummary>>(Stored.Values.Select(item =>
                new BatchRecoverySummary(item.BatchId, item.TemplateGroupId, item.InputDirectory,
                    item.Status, DateTimeOffset.UnixEpoch)).ToArray());

        public Task<BatchInputProjection?> RestoreBatchAsync(string batchId, CancellationToken cancellationToken)
        {
            RestoreRequests.Add(batchId);
            return Task.FromResult(Stored.GetValueOrDefault(batchId));
        }

        public async Task<BatchInputProjection> ScanAsync(BatchInputSubmission submission, CancellationToken cancellationToken)
        {
            Scans.Add(submission);
            ScanToken = cancellationToken;
            if (ScanError is not null) throw ScanError;
            if (WaitForCancellation) await Task.Delay(Timeout.Infinite, cancellationToken);
            return Projection("scanned_batch", BatchInputUiStatus.ScanReady) with { OutputMode = submission.OutputMode };
        }

        public async Task<BatchInputProjection> SaveDraftAsync(BatchInputSubmission submission, CancellationToken cancellationToken)
        {
            Saves++;
            if (SaveGate is not null) await SaveGate;
            return Projection("old_saved_batch", BatchInputUiStatus.Draft) with { OutputMode = submission.OutputMode };
        }
    }
}
