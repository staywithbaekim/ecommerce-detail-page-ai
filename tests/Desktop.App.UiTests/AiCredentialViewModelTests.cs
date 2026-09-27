using Desktop.Application.Ai;

namespace Desktop.App.UiTests;

public sealed class AiCredentialViewModelTests
{
    [Fact]
    public async Task SaveClearsInputAndRechecksMaskedStatusBeforeScan()
    {
        var port = new CredentialPort();
        using var page = new AiCredentialViewModel(port);
        page.Initialize();
        Assert.False(await page.EnsureConfiguredForScanAsync());
        var cleared = 0;
        page.AiCredentialInputCleared += (_, _) => cleared++;
        page.SetAiCredentialInput("fake-test-only-1234");

        page.SaveAiCredentialCommand.Execute(null);

        Assert.Equal(1, port.Saves);
        Assert.Equal(1, cleared);
        Assert.Empty(page.State.CredentialInput);
        Assert.Equal("••••1234", page.AiCredentialMaskedSuffix);
        Assert.DoesNotContain("fake-test-only", page.AiCredentialStatusText, StringComparison.Ordinal);
        Assert.Contains("没有调用 AI", page.AiCredentialActionMessage, StringComparison.Ordinal);
        Assert.False(page.CanSaveAiCredential);
        Assert.True(page.RemoveAiCredentialCommand.CanExecute(null));
        Assert.True(await page.EnsureConfiguredForScanAsync());

        page.RemoveAiCredentialCommand.Execute(null);

        Assert.Equal(1, port.Removes);
        Assert.Equal(AiCredentialState.NotConfigured, page.AiCredentialState);
        Assert.False(await page.EnsureConfiguredForScanAsync());
    }

    [Fact]
    public void FailedSaveKeepsInputAndClearsBusyWithoutExposingErrorDetails()
    {
        var port = new CredentialPort { SaveError = new IOException("fake-sensitive-error") };
        using var page = new AiCredentialViewModel(port);
        page.Initialize();
        page.SetAiCredentialInput("fake-test-only-1234");
        var cleared = false;
        page.AiCredentialInputCleared += (_, _) => cleared = true;

        page.SaveAiCredentialCommand.Execute(null);

        Assert.False(cleared);
        Assert.Equal("fake-test-only-1234", page.State.CredentialInput);
        Assert.False(page.IsAiCredentialBusy);
        Assert.True(page.SaveAiCredentialCommand.CanExecute(null));
        Assert.Equal(AiCredentialState.NotConfigured, page.AiCredentialState);
        Assert.DoesNotContain("fake-sensitive-error", page.AiCredentialActionMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void FailedRemoveRetainsConfiguredStatusAndPendingInput()
    {
        var port = new CredentialPort { Configured = true, RemoveError = new IOException() };
        using var page = new AiCredentialViewModel(port);
        page.Initialize();
        page.SetAiCredentialInput("fake-replacement-5678");

        page.RemoveAiCredentialCommand.Execute(null);

        Assert.Equal(AiCredentialState.Configured, page.AiCredentialState);
        Assert.Equal("fake-replacement-5678", page.State.CredentialInput);
        Assert.False(page.IsAiCredentialBusy);
        Assert.True(page.RemoveAiCredentialCommand.CanExecute(null));
        Assert.Contains("移除失败", page.AiCredentialActionMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FailedReadBlocksScanAndHidesPreviousConfiguredStatus()
    {
        var port = new CredentialPort { Configured = true };
        using var page = new AiCredentialViewModel(port);
        page.Initialize();
        Assert.Equal(AiCredentialState.Configured, page.AiCredentialState);
        port.ReadError = new IOException("fake-sensitive-error");

        Assert.False(await page.EnsureConfiguredForScanAsync());

        Assert.Equal(AiCredentialState.Unavailable, page.AiCredentialState);
        Assert.Equal("未显示", page.AiCredentialMaskedSuffix);
        Assert.False(page.IsAiCredentialBusy);
        Assert.True(page.RefreshAiCredentialCommand.CanExecute(null));
        Assert.DoesNotContain("fake-sensitive-error", page.AiCredentialActionMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InFlightSaveDisablesAllCommandsAndCannotSaveTwice()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var port = new CredentialPort { SaveGate = gate.Task };
        using var page = new AiCredentialViewModel(port);
        page.Initialize();
        page.SetAiCredentialInput("fake-test-only-1234");
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        page.SaveAiCredentialCommand.CanExecuteChanged += (_, _) =>
        {
            if (port.Configured && !page.IsAiCredentialBusy) completed.TrySetResult();
        };

        page.SaveAiCredentialCommand.Execute(null);
        Assert.True(page.IsAiCredentialBusy);
        Assert.False(page.SaveAiCredentialCommand.CanExecute(null));
        Assert.False(page.RefreshAiCredentialCommand.CanExecute(null));
        Assert.False(page.RemoveAiCredentialCommand.CanExecute(null));
        page.SaveAiCredentialCommand.Execute(null);
        page.RefreshAiCredentialCommand.Execute(null);
        Assert.Equal(1, port.Saves);
        Assert.Equal(1, port.Reads);

        gate.SetResult();
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, port.Reads);
        Assert.Empty(page.State.CredentialInput);
        Assert.True(page.RefreshAiCredentialCommand.CanExecute(null));
    }

    [Fact]
    public async Task OfflineFixtureKeepsScanBehaviorAndDisposeClearsBufferOnce()
    {
        var page = new AiCredentialViewModel(null);
        page.Initialize();
        Assert.True(await page.EnsureConfiguredForScanAsync());
        Assert.False(page.RefreshAiCredentialCommand.CanExecute(null));
        page.SetAiCredentialInput("fake-test-only-1234");
        var cleared = 0;
        var notifications = 0;
        page.AiCredentialInputCleared += (_, _) => cleared++;
        page.PropertyChanged += (_, _) => notifications++;

        page.Dispose();
        page.Dispose();

        Assert.Empty(page.State.CredentialInput);
        Assert.Equal(1, cleared);
        var before = notifications;
        page.State.SetCredentialActionMessage("after disposal");
        Assert.Equal(before, notifications);
    }

    private sealed class CredentialPort : IAiCredentialManagementPort
    {
        public bool Configured { get; set; }
        public Exception? SaveError { get; init; }
        public Exception? RemoveError { get; init; }
        public Exception? ReadError { get; set; }
        public Task? SaveGate { get; init; }
        public int Saves { get; private set; }
        public int Removes { get; private set; }
        public int Reads { get; private set; }

        public Task<AiCredentialStatusSnapshot> GetStatusAsync(CancellationToken cancellationToken)
        {
            Reads++;
            if (ReadError is not null) throw ReadError;
            return Task.FromResult(new AiCredentialStatusSnapshot(
                Configured ? AiCredentialState.Configured : AiCredentialState.NotConfigured,
                Configured ? "••••1234" : null));
        }

        public async Task SaveAsync(string apiKey, CancellationToken cancellationToken)
        {
            Saves++;
            Assert.Equal("fake-test-only-1234", apiKey);
            if (SaveError is not null) throw SaveError;
            if (SaveGate is not null) await SaveGate;
            Configured = true;
        }

        public Task RemoveAsync(CancellationToken cancellationToken)
        {
            Removes++;
            if (RemoveError is not null) throw RemoveError;
            Configured = false;
            return Task.CompletedTask;
        }
    }
}
