using Desktop.Application.Ai;
using Desktop.Application.Templates;

namespace Desktop.App.UiTests;

public sealed class AiAnalysisViewModelTests
{
    private static readonly string[] ControlCalls = ["start:batch_1", "pause:batch_1", "resume:batch_1", "refresh:batch_1"];

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NavigationInvalidatesLateConsentReads(bool referenceLearning)
    {
        var deferred = new TaskCompletionSource<AiUploadConsentRecord?>();
        var workflow = new Workflow { ConsentReadOverride = () => deferred.Task };
        var host = new Host();
        using var page = new AiAnalysisViewModel(workflow, host);
        var pending = referenceLearning ? page.EnsureAiUploadConsentForReferenceLearningAsync() : page.EnsureAiUploadConsentForScanAsync();
        page.ClosePageDialogs();
        deferred.SetResult(null);
        Assert.False(await pending);
        Assert.False(page.IsAiUploadConsentDialogVisible);
        Assert.False(page.IsBatchScanConsentPending);
        Assert.False(page.IsReferenceLearningConsentPending);
        Assert.Equal(0, workflow.Grants + host.Scans + host.ReferenceStarts);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NavigationDuringExplicitConsentGrantDoesNotResumeOldPage(bool referenceLearning)
    {
        var deferred = new TaskCompletionSource<AiUploadConsentRecord>();
        var workflow = new Workflow { ConsentGrantOverride = () => deferred.Task };
        var host = new Host();
        using var page = new AiAnalysisViewModel(workflow, host);
        if (referenceLearning) await page.EnsureAiUploadConsentForReferenceLearningAsync();
        else await page.EnsureAiUploadConsentForScanAsync();
        page.AiUploadConsentAcknowledged = true;
        var pending = page.ConfirmAiUploadConsentAsync();
        page.ClosePageDialogs();
        deferred.SetResult(new(AiConsentScopes.AnalysisCopy, "consent", AiUploadConsentState.Granted,
            AiConsentPolicy.Version, DateTimeOffset.UnixEpoch));
        await pending;
        Assert.Equal(1, workflow.Grants);
        Assert.True(page.IsAiUploadConsentGranted);
        Assert.False(page.IsAiUploadConsentDialogVisible);
        Assert.False(page.AiUploadConsentAcknowledged);
        Assert.Equal(0, host.Scans + host.ReferenceStarts);
    }

    [Fact]
    public async Task NavigationInvalidatesLateInstanceReviewWithoutFallingBackToLegacyLock()
    {
        var deferred = new TaskCompletionSource<TemplateInstanceSelectionReview?>();
        var workflow = new Workflow { InstanceReviewOverride = () => deferred.Task };
        using var page = new AiAnalysisViewModel(workflow, new Host());
        var candidate = new AiCandidateViewModel(new("batch_1", "style_1", "frame", "asset_1", "fixture.png", 1, 0.9m, [], false, "frame"));
        var pending = page.TryLockAiSelectionAsync(candidate);
        page.ClosePageDialogs();
        deferred.SetResult(new("batch_1", "style_1", "frame", "asset_1", 1, "digest", [new("instance", "实例", "asset_1", 0)]));
        await pending;
        Assert.False(page.IsInstanceSelectionVisible);
        Assert.Null(page.SelectedInstanceTarget);
        Assert.Null(workflow.InstanceCommand);
        Assert.Empty(workflow.Calls);
    }

    [Fact]
    public async Task NavigationClearsOpenInstanceSelectionWithoutConfirmingIt()
    {
        var workflow = new Workflow { InstanceReview = new("batch_1", "style_1", "frame", "asset_1", 1,
            "digest", [new("instance", "实例", "asset_1", 0)]) };
        using var page = new AiAnalysisViewModel(workflow, new Host());
        await page.TryLockAiSelectionAsync(new(new("batch_1", "style_1", "frame", "asset_1", "fixture.png", 1, 0.9m, [], false, "frame")));
        Assert.True(page.IsInstanceSelectionVisible);
        page.ClosePageDialogs();
        Assert.False(page.IsInstanceSelectionVisible);
        Assert.Empty(page.InstanceSelectionTargets);
        Assert.Null(page.SelectedInstanceTarget);
        page.ConfirmInstanceSelectionCommand.Execute(null);
        Assert.Null(workflow.InstanceCommand);
        Assert.Empty(workflow.Calls);
    }

    [Fact]
    public async Task MissingCredentialStopsBothConsentFlowsBeforeWorkflowCalls()
    {
        var workflow = new Workflow();
        var host = new Host { CredentialConfigured = false, BatchStatusMessage = "configure credential" };
        using var page = new AiAnalysisViewModel(workflow, host);

        Assert.False(await page.EnsureAiUploadConsentForScanAsync());
        Assert.False(await page.EnsureAiUploadConsentForReferenceLearningAsync());

        Assert.Equal(0, workflow.ConsentReads);
        Assert.False(page.IsAiUploadConsentDialogVisible);
        Assert.Equal(host.BatchStatusMessage, host.ReferenceLearningStatusMessage);
        Assert.Equal(0, host.Scans + host.ReferenceStarts);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExplicitConsentContinuesOnlyTheRequestingPage(bool referenceLearning)
    {
        var workflow = new Workflow();
        var host = new Host();
        using var page = new AiAnalysisViewModel(workflow, host);

        Assert.False(referenceLearning
            ? await page.EnsureAiUploadConsentForReferenceLearningAsync()
            : await page.EnsureAiUploadConsentForScanAsync());
        Assert.True(page.IsAiUploadConsentDialogVisible);
        Assert.False(page.AiUploadConsentAcknowledged);
        Assert.False(page.ConfirmAiUploadConsentCommand.CanExecute(null));
        page.ConfirmAiUploadConsentCommand.Execute(null);
        Assert.Equal(0, workflow.Grants);

        page.AiUploadConsentAcknowledged = true;
        page.ConfirmAiUploadConsentCommand.Execute(null);

        Assert.Equal(1, workflow.Grants);
        Assert.Equal(referenceLearning ? 1 : 0, host.ReferenceStarts);
        Assert.Equal(referenceLearning ? 0 : 1, host.Scans);
        Assert.True(page.IsAiUploadConsentGranted);
        Assert.False(page.IsAiUploadConsentDialogVisible);
        Assert.False(page.AiUploadConsentAcknowledged);
        Assert.False(page.IsAiBusy);
    }

    [Fact]
    public async Task CancelledDialogDoesNotResumeOldScanWhenSettingsGrantConsent()
    {
        var workflow = new Workflow();
        var host = new Host();
        using var page = new AiAnalysisViewModel(workflow, host);
        await page.EnsureAiUploadConsentForScanAsync();
        page.AiUploadConsentAcknowledged = true;

        page.CancelAiUploadConsentCommand.Execute(null);
        page.GrantAiUploadConsentCommand.Execute(null);

        Assert.False(page.IsAiUploadConsentDialogVisible);
        Assert.False(page.AiUploadConsentAcknowledged);
        Assert.Equal(0, host.Scans + host.ReferenceStarts);
        Assert.True(page.IsAiUploadConsentGranted);
        page.RevokeAiUploadConsentCommand.Execute(null);
        Assert.True(page.IsAiUploadConsentRevoked);
        Assert.False(await page.EnsureAiUploadConsentForScanAsync());
        Assert.True(page.IsAiUploadConsentDialogVisible);
        Assert.Contains("已撤回", host.BatchStatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StartAndControlsUseHostBatchIdentityAndUpdatePageProjection()
    {
        var workflow = new Workflow();
        var host = new Host();
        using var page = new AiAnalysisViewModel(workflow, host);

        await page.StartAiAnalysisAsync(host.CurrentBatchId);
        Assert.Equal("analysis", host.Screen);
        Assert.True(page.PauseAiAnalysisCommand.CanExecute(null));
        page.PauseAiAnalysisCommand.Execute(null);
        Assert.Equal(AiAnalysisSchedulingControlState.Paused, page.AiProjection!.ControlState);
        Assert.True(page.ResumeAiAnalysisCommand.CanExecute(null));
        page.ResumeAiAnalysisCommand.Execute(null);
        Assert.Equal(AiAnalysisSchedulingControlState.Running, page.AiProjection.ControlState);
        page.RefreshAiAnalysisCommand.Execute(null);

        Assert.Equal(ControlCalls, workflow.Calls);
        Assert.False(page.IsAiBusy);
    }

    [Fact]
    public async Task StartFailureKeepsInputBlockedAndClearsPageBusyState()
    {
        var workflow = new Workflow { StartError = new InvalidOperationException("AI_CHECKPOINT_TEST") };
        var host = new Host();
        using var page = new AiAnalysisViewModel(workflow, host);

        await page.StartAiAnalysisAsync(host.CurrentBatchId);

        Assert.Equal(BatchInputUiStatus.ScanBlocked, host.BatchStatus);
        Assert.Contains("本地分析状态未保存或不一致", host.BatchStatusMessage, StringComparison.Ordinal);
        Assert.False(page.IsAiBusy);
        Assert.Null(page.AiProjection);
        Assert.Null(host.Screen);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SafeRetryHandsOffNewBatchCreationWithoutReusingOldRequest(bool validInput)
    {
        var workflow = new Workflow
        {
            Projection = Projection() with { Status = AiBatchUiStatus.Failed, ActionText = "重试分析" }
        };
        var host = new Host { CanRestartBatchAnalysis = validInput };
        using var page = new AiAnalysisViewModel(workflow, host);
        await page.StartAiAnalysisAsync(host.CurrentBatchId);
        host.OnRestart = () => Assert.True(page.IsAiBusy);

        page.RetryAiAnalysisCommand.Execute(null);

        Assert.Equal(validInput ? 1 : 0, host.Restarts);
        Assert.Single(workflow.Calls);
        Assert.False(page.IsAiBusy);
        if (!validInput) Assert.Contains("配置已失效", page.AiStatusMessage, StringComparison.Ordinal);
    }

    private static AiBatchAnalysisProjection Projection() => new(
        "batch_1", AiBatchUiStatus.Analysing, "analysing", "wait", 0, 0, 0, 0, [],
        AiAnalysisSchedulingControlState.Running, null);

    [Theory]
    [InlineData(AiRequirementSelectionUiStatus.AiSelected)]
    [InlineData(AiRequirementSelectionUiStatus.ReviewRequired)]
    [InlineData(AiRequirementSelectionUiStatus.Unsafe)]
    [InlineData(AiRequirementSelectionUiStatus.ManualLocked)]
    public async Task CurrentAiPhotoCanBeExplicitlyLockedWithoutReplacementOrAnotherAnalysis(AiRequirementSelectionUiStatus status)
    {
        var workflow = new Workflow();
        using var page = new AiAnalysisViewModel(workflow, new Host());
        var target = new AiRequirementUsageViewModel(new AiRequirementSelectionProjection(
            "batch_1", "style_1", "page_1", "四格细节", "composition", "成分信息图", "成分信息图",
            status, status.ToString(), "asset_1", "current.png", "照片 01"));
        var current = new AiStyleAssetViewModel(new AiStyleAssetProjection("batch_1", "style_1", "asset_1", "current.png", 1));
        page.BeginAiReplacementCommand.Execute(target);
        Assert.False(page.CanConfirmAiReplacement);
        Assert.False(page.SelectAiReplacementAssetCommand.CanExecute(new AiStyleAssetViewModel(
            new AiStyleAssetProjection("batch_1", "other_style", "asset_1", "current.png", 1))));
        page.SelectAiReplacementAssetCommand.Execute(current);

        var shouldLock = status != AiRequirementSelectionUiStatus.ManualLocked;
        Assert.Equal(shouldLock, page.ConfirmAiReplacementCommand.CanExecute(null));
        Assert.Contains(shouldLock ? "人工确认并锁定" : "已人工锁定", page.AiReplacementInstructionText, StringComparison.Ordinal);
        await page.ConfirmAiReplacementAsync();

        if (shouldLock) Assert.Equal("lock:batch_1:style_1:composition:asset_1", Assert.Single(workflow.Calls));
        else Assert.Empty(workflow.Calls);
        if (shouldLock)
        {
            Assert.False(page.IsAiReplacementActive);
            Assert.Contains("已确认并锁定当前照片", page.AiStatusMessage, StringComparison.Ordinal);
            Assert.DoesNotContain("已替换", page.AiStatusMessage, StringComparison.Ordinal);
        }
    }

    private sealed class Host : IAiAnalysisHost
    {
        public string CurrentBatchId => "batch_1";
        public bool IsBatchNarrowLayout => false;
        public bool IsAiAnalysisVisible => Screen == "analysis";
        public bool IsAiStyleReviewVisible => Screen == "style";
        public bool IsAiStyleReviewScreen => Screen == "style";
        public bool CanRestartBatchAnalysis { get; init; } = true;
        public bool CredentialConfigured { get; init; } = true;
        public string BatchStatusMessage { get; set; } = string.Empty;
        public string ReferenceLearningStatusMessage { get; set; } = string.Empty;
        public BatchInputUiStatus BatchStatus { get; private set; }
        public string? Screen { get; private set; }
        public int Scans { get; private set; }
        public int ReferenceStarts { get; private set; }
        public int Restarts { get; private set; }
        public Action? OnRestart { get; set; }
        public void NotifyMainVisibilityChanged() { }
        public void ShowAiAnalysis() => Screen = "analysis";
        public void ShowAiStyleReview() => Screen = "style";
        public void ShowResultOverview() => Screen = "results";
        public void RaiseResultCommandsChanged() { }
        public void ApplyRenderPreparation(AiRenderPlanPreparation preparation) { }
        public void SetBatchStatus(BatchInputUiStatus status, string message)
        { BatchStatus = status; BatchStatusMessage = message; }
        public Task<bool> EnsureCredentialConfiguredAsync() => Task.FromResult(CredentialConfigured);
        public Task StartReferenceLearningAsync() { ReferenceStarts++; return Task.CompletedTask; }
        public Task ScanBatchInputAsync() { Scans++; return Task.CompletedTask; }
        public Task RestartBatchAnalysisAsync() { OnRestart?.Invoke(); Restarts++; return Task.CompletedTask; }
    }

    [Fact]
    public async Task E3FormalReplacementCommandRequiresExplicitInstanceConfirmation()
    {
        var workflow = new Workflow { InstanceReview = new("batch_1", "style_1", "composition", "asset_1", 5,
            "sha256:" + new string('a', 64), [new("instance_a", "页面 · 实例1", "asset_1", 0), new("instance_b", "页面 · 实例2", "asset_1", 3)]) };
        using var page = new AiAnalysisViewModel(workflow, new Host());
        page.BeginAiReplacementCommand.Execute(new AiRequirementUsageViewModel(new AiRequirementSelectionProjection(
            "batch_1", "style_1", "page_1", "四格细节", "composition", "成分信息图", "成分信息图",
            AiRequirementSelectionUiStatus.AiSelected, "AI用图", "asset_1", "current.png", "照片 01")));
        page.SelectAiReplacementAssetCommand.Execute(new AiStyleAssetViewModel(new AiStyleAssetProjection("batch_1", "style_1", "asset_1", "current.png", 1)));
        await page.ConfirmAiReplacementAsync();
        Assert.True(page.IsInstanceSelectionVisible); Assert.Null(page.SelectedInstanceTarget);
        Assert.Empty(workflow.Calls); Assert.Null(workflow.InstanceCommand);
        Assert.False(page.ConfirmInstanceSelectionCommand.CanExecute(null));
        page.SelectedInstanceTarget = page.InstanceSelectionTargets[1];
        Assert.True(page.ConfirmInstanceSelectionCommand.CanExecute(null));
        page.ConfirmInstanceSelectionCommand.Execute(null);
        Assert.Equal("instance_b", workflow.InstanceCommand!.PageInstanceId);
        Assert.Equal(5, workflow.InstanceCommand.ExpectedRevision);
        Assert.False(page.IsInstanceSelectionVisible); Assert.Empty(workflow.Calls);
        Assert.Contains("其他实例未更改", page.AiStatusMessage, StringComparison.Ordinal);
    }

    private sealed class Workflow : IAiBatchAnalysisWorkflow, ITemplateInstanceSelectionWorkflow
    {
        public Func<Task<AiUploadConsentRecord?>>? ConsentReadOverride { get; init; }
        public Func<Task<AiUploadConsentRecord>>? ConsentGrantOverride { get; init; }
        public Func<Task<TemplateInstanceSelectionReview?>>? InstanceReviewOverride { get; init; }
        public TemplateInstanceSelectionReview? InstanceReview { get; init; }
        public TemplateInstanceSelectionCommand? InstanceCommand { get; private set; }
        public Task<TemplateInstanceSelectionReview?> ReviewInstanceSelectionAsync(string batchId, string styleId, string frameId, string assetId, CancellationToken token)
            => InstanceReviewOverride?.Invoke() ?? Task.FromResult(InstanceReview);
        public Task LockInstanceSelectionAsync(TemplateInstanceSelectionCommand command, CancellationToken token)
        { InstanceCommand = command; return Task.CompletedTask; }
        private AiUploadConsentRecord? _consent;
        public AiBatchAnalysisProjection Projection { get; set; } = AiAnalysisViewModelTests.Projection();
        public Exception? StartError { get; init; }
        public List<string> Calls { get; } = [];
        public int ConsentReads { get; private set; }
        public int Grants { get; private set; }
        public Task<AiBatchAnalysisProjection> StartAsync(string batchId, CancellationToken token)
        { Calls.Add("start:" + batchId); if (StartError is not null) throw StartError; return Task.FromResult(Projection); }
        public Task<AiBatchAnalysisProjection?> RefreshAsync(string batchId, CancellationToken token)
        { Calls.Add("refresh:" + batchId); return Task.FromResult<AiBatchAnalysisProjection?>(Projection); }
        public Task<AiBatchAnalysisProjection> PauseAsync(string batchId, CancellationToken token)
        { Calls.Add("pause:" + batchId); Projection = Projection with { ControlState = AiAnalysisSchedulingControlState.Paused }; return Task.FromResult(Projection); }
        public Task<AiBatchAnalysisProjection> ResumeAsync(string batchId, CancellationToken token)
        { Calls.Add("resume:" + batchId); Projection = Projection with { ControlState = AiAnalysisSchedulingControlState.Running }; return Task.FromResult(Projection); }
        public Task<AiBatchAnalysisProjection> CancelAsync(string batchId, CancellationToken token) => throw new NotSupportedException();
        public Task<AiBatchAnalysisProjection> WaitForNetworkAsync(string batchId, CancellationToken token) => throw new NotSupportedException();
        public Task<AiBatchAnalysisProjection> LockSelectionAsync(string batchId, string styleId, string requirementId, string assetId, CancellationToken token)
        { Calls.Add($"lock:{batchId}:{styleId}:{requirementId}:{assetId}"); return Task.FromResult(Projection); }
        public Task<AiBatchAnalysisProjection> SelectSizeTableSourceAsync(string batchId, string styleId, string assetId, CancellationToken token) => throw new NotSupportedException();
        public Task<AiBatchAnalysisProjection> AnalyzeSelectedAssetAsync(string batchId, string styleId, string assetId, bool includeSizeTableSource, CancellationToken token) => throw new NotSupportedException();
        public Task<AiRenderPlanPreparation> PrepareRenderingAsync(string batchId, CancellationToken token) => throw new NotSupportedException();
        public Task<AiUploadConsentRecord?> GetUploadConsentAsync(CancellationToken token)
        { ConsentReads++; return ConsentReadOverride?.Invoke() ?? Task.FromResult(_consent); }
        public Task<AiUploadConsentRecord> GrantUploadConsentAsync(CancellationToken token)
        { Grants++; return ConsentGrantOverride?.Invoke() ?? SetConsent(AiUploadConsentState.Granted); }
        public Task<AiUploadConsentRecord> RevokeUploadConsentAsync(CancellationToken token) => SetConsent(AiUploadConsentState.Revoked);
        private Task<AiUploadConsentRecord> SetConsent(AiUploadConsentState state)
        {
            _consent = new(AiConsentScopes.AnalysisCopy, "consent_test", state, AiConsentPolicy.Version, DateTimeOffset.UnixEpoch);
            return Task.FromResult(_consent);
        }
    }
}
