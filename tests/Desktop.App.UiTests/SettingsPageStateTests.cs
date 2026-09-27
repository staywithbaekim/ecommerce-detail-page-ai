using Desktop.Application.Ai;
using Desktop.Application.Rendering;

namespace Desktop.App.UiTests;

public sealed class SettingsPageStateTests
{
    [Fact]
    public void CredentialStateOwnsAvailabilityAndBusyTransitions()
    {
        var state = new SettingsPageState();

        state.ConfigureCredentialManagement(available: true);
        state.SetCredentialInput("secret-value");
        Assert.True(state.CanSaveAiCredential);
        Assert.True(state.CanRefreshAiCredential);
        Assert.False(state.CanRemoveAiCredential);

        state.SetCredentialBusy(true);
        Assert.False(state.CanSaveAiCredential);
        Assert.False(state.CanRefreshAiCredential);

        state.SetCredentialBusy(false);
        state.SetCredentialStatus(new(AiCredentialState.Configured, "1234"));
        Assert.True(state.CanRemoveAiCredential);
        Assert.Equal("已配置 Key · 1234", state.AiCredentialStatusText);

        Assert.True(state.ClearCredentialInput());
        Assert.False(state.CanSaveAiCredential);
    }

    [Fact]
    public async Task LeavingPhotoshopSectionCancelsTheConnectionCheck()
    {
        var check = new BlockingConnectionCheckPort();
        using var model = new MainWindowViewModel(
            AppLaunchOptions.Default,
            templateWorkspace: null,
            photoshopConnectionCheck: check);
        var page = model.SettingsPageViewModel;
        page.SelectedSection = "Photoshop";

        var running = page.Photoshop.CheckConnectionAsync();
        await check.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        page.SelectedSection = "隐私与诊断";
        await running;

        Assert.True(check.ObservedToken.IsCancellationRequested);
        Assert.Contains("取消", page.Photoshop.ConnectionStatus, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LeavingSettingsPageCancelsTheConnectionCheckWithoutUiLifetimeHooks()
    {
        var check = new BlockingConnectionCheckPort();
        using var model = new MainWindowViewModel(
            AppLaunchOptions.Default,
            templateWorkspace: null,
            photoshopConnectionCheck: check);
        model.SelectedNavigationItem = model.NavigationItems.Single(item => item.Key == "settings");
        model.SettingsPageViewModel.SelectedSection = "Photoshop";

        var running = model.SettingsPageViewModel.Photoshop.CheckConnectionAsync();
        await check.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        model.SelectedNavigationItem = model.NavigationItems.Single(item => item.Key == "workbench");
        await running;

        Assert.True(check.ObservedToken.IsCancellationRequested);
        Assert.Contains("取消", model.SettingsPageViewModel.Photoshop.ConnectionStatus, StringComparison.Ordinal);
    }

    private sealed class BlockingConnectionCheckPort : IPhotoshopConnectionCheckPort
    {
        public CancellationToken ObservedToken { get; private set; }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public PhotoshopConnectionCheckResult? Latest => null;

        public async Task<PhotoshopConnectionCheckResult> CheckAsync(CancellationToken cancellationToken)
        {
            ObservedToken = cancellationToken;
            Started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("unreachable");
        }
    }
}
