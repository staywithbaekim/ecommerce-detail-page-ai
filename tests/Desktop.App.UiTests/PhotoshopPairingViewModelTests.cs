using Desktop.Application.Rendering;
using System.Globalization;
using System.Text.Json;

namespace Desktop.App.UiTests;

public sealed class PhotoshopPairingViewModelTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task InstanceCheckIsExplicitAndNeverTreatsABasicResultAsInstanceEvidence(bool verifiedScope)
    {
        var pairing = new PairingPort();
        var check = new ConnectionCheckPort
        {
            Result = new(PhotoshopConnectionCheckStatus.Ready, DateTimeOffset.UnixEpoch, 3,
                "26.8.1 (20250624.r.8 5e0d05d)", verifiedScope)
        };
        using var view = new PhotoshopPairingViewModel(pairing, check);
        await view.CheckInstanceConnectionAsync();
        Assert.True(check.RequestedInstance);
        Assert.Equal(1, check.Calls);
        Assert.Equal(0, pairing.PairCalls);
        Assert.Empty(view.Code);
        Assert.Contains(verifiedScope ? "实例能力检查已通过" : "不能用基础连接结果替代",
            view.ConnectionStatus, StringComparison.Ordinal);
        if (verifiedScope) Assert.Contains("实例能力", view.ConnectionHistory, StringComparison.Ordinal);
        else Assert.Contains("尚无已完成", view.ConnectionHistory, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompletionOrFailureAlwaysClearsTransientCode(bool fail)
    {
        var port = new PairingPort { Fail = fail };
        using var view = new PhotoshopPairingViewModel(port);
        var sawCode = false;
        view.PropertyChanged += (_, _) =>
        {
            if (view.Code.Length == 43)
            {
                sawCode = true;
                Assert.DoesNotContain(view.Code, JsonSerializer.Serialize(view), StringComparison.Ordinal);
            }
        };
        await view.PairAsync();
        Assert.True(sawCode);
        Assert.Empty(view.Code);
        Assert.False(view.IsPairing);
        Assert.DoesNotContain("sensitive", view.Status, StringComparison.Ordinal);
        Assert.Equal(!fail, view.Status.Contains("已保存", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CancelImmediatelyClearsCodeAndPreservesPairingActionForRetry()
    {
        var port = new PairingPort { Wait = true };
        using var view = new PhotoshopPairingViewModel(port);
        var running = view.PairAsync();
        await port.Shown.Task.WaitAsync(TimeSpan.FromSeconds(3));
        view.Cancel();
        Assert.Empty(view.Code);
        await running;
        Assert.Empty(view.Code);
        Assert.True(view.PairCommand.CanExecute(null));
        Assert.Contains("取消", view.Status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExplicitConnectionCheckShowsAuthenticatedHistoricalEvidenceWithoutPairingAgain()
    {
        var observedAt = new DateTimeOffset(2026, 9, 24, 8, 30, 0, TimeSpan.Zero);
        var pairing = new PairingPort();
        var check = new ConnectionCheckPort
        {
            Result = new(PhotoshopConnectionCheckStatus.Ready, observedAt, 3,
                "26.8.1 (20250624.r.8 5e0d05d)")
        };
        using var view = new PhotoshopPairingViewModel(pairing, check);

        Assert.Equal(0, check.Calls);
        await view.CheckConnectionAsync();

        Assert.Equal(1, check.Calls);
        Assert.Equal(0, pairing.PairCalls);
        Assert.Contains("通过", view.ConnectionStatus, StringComparison.Ordinal);
        Assert.Contains("不代表当前连接", view.ConnectionStatus, StringComparison.Ordinal);
        Assert.Contains("这是本次检查的历史结果", view.ConnectionStatus, StringComparison.Ordinal);
        Assert.Contains("更换宿主或组件后请重新检查", view.ConnectionStatus, StringComparison.Ordinal);
        Assert.Contains(observedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture),
            view.ConnectionHistory, StringComparison.Ordinal);
        Assert.Contains("基础连接", view.ConnectionHistory, StringComparison.Ordinal);
        Assert.Contains("已通过", view.ConnectionHistory, StringComparison.Ordinal);
        Assert.DoesNotContain("26.8.1 (20250624.r.8 5e0d05d)", view.ConnectionHistory, StringComparison.Ordinal);
        Assert.False(view.IsBusy);
        Assert.False(view.IsPairing);
    }

    [Fact]
    public void LatestIsDisplayedOnlyAsHistoryAndDoesNotStartAConnection()
    {
        var observedAt = new DateTimeOffset(2026, 9, 24, 9, 0, 0, TimeSpan.Zero);
        var check = new ConnectionCheckPort
        {
            LatestValue = new(PhotoshopConnectionCheckStatus.Ready, observedAt, 4,
                "26.8.1 (20250624.r.8 5e0d05d)")
        };
        using var view = new PhotoshopPairingViewModel(null, check);

        Assert.Equal(0, check.Calls);
        Assert.Contains("历史结果", view.ConnectionStatus, StringComparison.Ordinal);
        Assert.Contains("基础连接", view.ConnectionHistory, StringComparison.Ordinal);
        Assert.Contains("已通过", view.ConnectionHistory, StringComparison.Ordinal);
        Assert.Contains("更换宿主或组件后请重新检查", view.ConnectionHistory, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(PhotoshopConnectionCheckStatus.PairingRequired, "尚未保存设备配对")]
    [InlineData(PhotoshopConnectionCheckStatus.Busy, "当前任务正在使用连接通道")]
    [InlineData(PhotoshopConnectionCheckStatus.TimedOut, "认证连接检查超时")]
    [InlineData(PhotoshopConnectionCheckStatus.HostNotReady, "Photoshop 当前无法开始检查")]
    [InlineData(PhotoshopConnectionCheckStatus.Failed, "认证连接检查未完成")]
    [InlineData(PhotoshopConnectionCheckStatus.HostSelectionRequired, "尚未选择正式 Photoshop")]
    [InlineData(PhotoshopConnectionCheckStatus.HostSelectionInvalid, "已保存的 Photoshop 程序已失效")]
    public async Task ConnectionFailuresRemainActionableAndDistinct(
        PhotoshopConnectionCheckStatus status,
        string expected)
    {
        var check = new ConnectionCheckPort
        {
            Result = new(status, DateTimeOffset.UnixEpoch, status == PhotoshopConnectionCheckStatus.HostNotReady ? 3 : 0)
        };
        using var view = new PhotoshopPairingViewModel(null, check);

        await view.CheckConnectionAsync();

        Assert.Contains(expected, view.ConnectionStatus, StringComparison.Ordinal);
        Assert.DoesNotContain("可生产", view.ConnectionStatus, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CancelledLateReadyResultCannotReplacePriorHistoryOrShowSuccess()
    {
        var prior = new PhotoshopConnectionCheckResult(
            PhotoshopConnectionCheckStatus.Failed,
            new DateTimeOffset(2026, 9, 24, 7, 0, 0, TimeSpan.Zero));
        var check = new ConnectionCheckPort
        {
            LatestValue = prior,
            WaitForRelease = true,
            IgnoreCancellation = true,
            Result = new(PhotoshopConnectionCheckStatus.Ready, prior.ObservedAt.AddHours(1), 3, "late-sensitive-build")
        };
        using var view = new PhotoshopPairingViewModel(null, check);

        var running = view.CheckConnectionAsync();
        await check.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True(view.IsCheckingConnection);
        Assert.False(view.IsPairing);

        view.Cancel();
        Assert.True(check.ObservedToken.IsCancellationRequested);
        check.Release.TrySetResult();
        await running;

        Assert.Contains("取消", view.ConnectionStatus, StringComparison.Ordinal);
        Assert.DoesNotContain("通过", view.ConnectionStatus, StringComparison.Ordinal);
        Assert.DoesNotContain("late-sensitive-build", view.ConnectionHistory, StringComparison.Ordinal);
        Assert.Contains(prior.ObservedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture),
            view.ConnectionHistory, StringComparison.Ordinal);
        Assert.True(view.CheckConnectionCommand.CanExecute(null));
    }

    [Fact]
    public async Task DisposeCancelsAndIgnoresLateConnectionSuccess()
    {
        var check = new ConnectionCheckPort
        {
            WaitForRelease = true,
            IgnoreCancellation = true,
            Result = new(PhotoshopConnectionCheckStatus.Ready, DateTimeOffset.UtcNow, 3, "late-build")
        };
        var view = new PhotoshopPairingViewModel(null, check);

        var running = view.CheckConnectionAsync();
        await check.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        view.Dispose();
        Assert.True(check.ObservedToken.IsCancellationRequested);
        check.Release.TrySetResult();
        await running;

        Assert.DoesNotContain("通过", view.ConnectionStatus, StringComparison.Ordinal);
        Assert.DoesNotContain("late-build", view.ConnectionHistory, StringComparison.Ordinal);
        Assert.False(view.CheckConnectionCommand.CanExecute(null));
    }

    [Fact]
    public async Task PairingOwnsTheSharedBusyGateAndConnectionCheckCannotStart()
    {
        var pairing = new PairingPort { Wait = true };
        var check = new ConnectionCheckPort();
        using var view = new PhotoshopPairingViewModel(pairing, check);

        var running = view.PairAsync();
        await pairing.Shown.Task.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.True(view.IsPairing);
        Assert.False(view.CheckConnectionCommand.CanExecute(null));
        Assert.False(view.RefreshCommand.CanExecute(null));
        await view.CheckConnectionAsync();
        await view.RefreshAsync();
        Assert.Equal(0, check.Calls);
        Assert.Equal(0, pairing.RefreshCalls);

        view.Cancel();
        await running;
    }

    [Fact]
    public async Task PairingRefreshOwnsTheSharedBusyGateWithoutShowingAPairingCode()
    {
        var pairing = new PairingPort { WaitRefresh = true };
        var check = new ConnectionCheckPort();
        using var view = new PhotoshopPairingViewModel(pairing, check);

        var running = view.RefreshAsync();
        await pairing.RefreshStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.True(view.IsBusy);
        Assert.False(view.IsPairing);
        Assert.Empty(view.Code);
        Assert.False(view.PairCommand.CanExecute(null));
        Assert.False(view.CheckConnectionCommand.CanExecute(null));
        await view.PairAsync();
        await view.CheckConnectionAsync();
        Assert.Equal(0, pairing.PairCalls);
        Assert.Equal(0, check.Calls);

        view.Cancel();
        await running;
    }

    [Fact]
    public async Task ConnectionCheckOwnsTheSharedBusyGateWithoutShowingAPairingCode()
    {
        var pairing = new PairingPort();
        var check = new ConnectionCheckPort { WaitForRelease = true };
        using var view = new PhotoshopPairingViewModel(pairing, check);

        var running = view.CheckConnectionAsync();
        await check.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.True(view.IsCheckingConnection);
        Assert.False(view.IsPairing);
        Assert.Empty(view.Code);
        Assert.Contains("60 秒", view.ConnectionStatus, StringComparison.Ordinal);
        Assert.Contains("配套组件已启动", view.ConnectionStatus, StringComparison.Ordinal);
        Assert.Contains("Photoshop 和配套组件", view.ConnectionStatus, StringComparison.Ordinal);
        Assert.Contains("不会重新配对", view.ConnectionStatus, StringComparison.Ordinal);
        Assert.False(view.PairCommand.CanExecute(null));
        Assert.False(view.RefreshCommand.CanExecute(null));
        Assert.False(view.CheckInstanceConnectionCommand.CanExecute(null));
        await view.PairAsync();
        await view.RefreshAsync();
        Assert.Equal(0, pairing.PairCalls);
        Assert.Equal(0, pairing.RefreshCalls);

        view.Cancel();
        await running;
    }

    [Fact]
    public async Task UnknownConnectionExceptionDoesNotLeakItsMessage()
    {
        var check = new ConnectionCheckPort { Failure = new IOException("sensitive raw socket failure") };
        using var view = new PhotoshopPairingViewModel(null, check);

        await view.CheckConnectionAsync();

        Assert.Contains("失败", view.ConnectionStatus, StringComparison.Ordinal);
        Assert.DoesNotContain("sensitive", view.ConnectionStatus, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("socket", view.ConnectionStatus, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class PairingPort : IPhotoshopPairingPort
    {
        public bool Fail { get; init; }
        public bool Wait { get; init; }
        public bool WaitRefresh { get; init; }
        public int PairCalls { get; private set; }
        public int RefreshCalls { get; private set; }
        public TaskCompletionSource Shown { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource RefreshStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<bool> IsConfiguredAsync(CancellationToken cancellationToken)
        {
            RefreshCalls++;
            RefreshStarted.TrySetResult();
            if (WaitRefresh) await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return false;
        }
        public async Task PairAsync(Func<ReadOnlyMemory<byte>, CancellationToken, Task> presentCode, CancellationToken cancellationToken)
        {
            PairCalls++;
            await presentCode(new byte[32], cancellationToken);
            Shown.TrySetResult();
            try
            {
                if (Wait) await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                if (Fail) throw new IOException("sensitive raw failure");
            }
            finally { await presentCode(ReadOnlyMemory<byte>.Empty, CancellationToken.None); }
        }
    }

    private sealed class ConnectionCheckPort : IPhotoshopConnectionCheckPort
    {
        public PhotoshopConnectionCheckResult? LatestValue { get; init; }
        public PhotoshopConnectionCheckResult Result { get; init; } =
            new(PhotoshopConnectionCheckStatus.Failed, DateTimeOffset.UnixEpoch);
        public Exception? Failure { get; init; }
        public bool WaitForRelease { get; init; }
        public bool IgnoreCancellation { get; init; }
        public int Calls { get; private set; }
        public bool RequestedInstance { get; private set; }
        public CancellationToken ObservedToken { get; private set; }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public PhotoshopConnectionCheckResult? Latest => LatestValue;

        public Task<PhotoshopConnectionCheckResult> CheckInstanceIdentityAsync(CancellationToken cancellationToken)
        {
            RequestedInstance = true;
            return CheckAsync(cancellationToken);
        }

        public async Task<PhotoshopConnectionCheckResult> CheckAsync(CancellationToken cancellationToken)
        {
            Calls++;
            ObservedToken = cancellationToken;
            Started.TrySetResult();
            if (Failure is not null) throw Failure;
            if (WaitForRelease)
            {
                if (IgnoreCancellation) await Release.Task;
                else await Release.Task.WaitAsync(cancellationToken);
            }
            return Result;
        }
    }
}
