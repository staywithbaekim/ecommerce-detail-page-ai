using Desktop.Application.Diagnostics;
using Desktop.Application.Rendering;

namespace Desktop.App.UiTests;

public sealed class PhotoshopEnvironmentViewModelTests
{
    [Fact]
    public async Task OpeningAndCancellingPickerDoNotDetectOrSelectRenderingHost()
    {
        var probe = new Probe();
        var picker = new Picker(null);
        using var view = new PhotoshopEnvironmentViewModel(probe, picker);
        Assert.Equal(0, probe.Calls);
        Assert.Empty(view.Items);
        Assert.Contains("尚未检测", view.Status, StringComparison.Ordinal);
        await view.DetectAsync(true);
        Assert.Equal(1, picker.Calls);
        Assert.Equal(0, probe.Calls);
        Assert.Contains("已取消选择", view.Status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RealMetadataAndHistoricalTimeAreSeparatedFromReadinessAndManualPathIsPassed()
    {
        var probe = new Probe { Result = Snapshot() };
        using var view = new PhotoshopEnvironmentViewModel(probe, new Picker(@"G:\Adobe Photoshop 2025\Photoshop.exe"));
        await view.DetectAsync(true);
        Assert.Equal(@"G:\Adobe Photoshop 2025\Photoshop.exe", probe.Path);
        Assert.Contains("只有明确点击", view.Status, StringComparison.Ordinal);
        Assert.Contains("26.8 (20250624.r.8 5e0d05d)", view.SelectedItem!.VersionDetail, StringComparison.Ordinal);
        Assert.Contains("26.8.1.8", view.SelectedItem.VersionDetail, StringComparison.Ordinal);
        Assert.Contains("不代表可生产", view.SelectedItem.VersionScope, StringComparison.Ordinal);
        Assert.Contains("仅代表该时刻", view.ObservationText, StringComparison.Ordinal);
        Assert.Contains("仍需安装与能力验收", view.OperatingSystemText, StringComparison.Ordinal);
        Assert.Contains("运行进程", view.SelectedItem.SourceText, StringComparison.Ordinal);
        Assert.Contains("手动选择文件", view.SelectedItem.SourceText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EmptyPartialAndUnsupportedAreNotFakeConnectionFailures()
    {
        var probe = new Probe { Result = Snapshot() with { Installations = [], UnavailableSources = 2, Truncated = true, OsVersion = "10.0.19045.1" } };
        using var view = new PhotoshopEnvironmentViewModel(probe);
        await view.DetectAsync();
        Assert.False(view.HasItems);
        Assert.Contains("不代表已证明未安装", view.Status, StringComparison.Ordinal);
        Assert.Contains("2 个", view.Status, StringComparison.Ordinal);
        Assert.Contains("扫描上限", view.Status, StringComparison.Ordinal);
        Assert.Contains("不在当前首发支持", view.OperatingSystemText, StringComparison.Ordinal);
        Assert.Contains("不是普通连接失败", new PhotoshopEnvironmentItem(Snapshot().Installations[0] with { NumericFileVersion = "26.9.0.1" }).VersionScope, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FailedReadPreservesEarlierSnapshotWithoutLeakingException()
    {
        var probe = new Probe();
        using var view = new PhotoshopEnvironmentViewModel(probe);
        await view.DetectAsync();
        var items = view.Items;
        var observed = view.ObservationText;
        probe.Fail = true;
        await view.DetectAsync();
        Assert.Same(items, view.Items);
        Assert.Equal(observed, view.ObservationText);
        Assert.Contains("上次快照", view.Status, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", view.Status, StringComparison.Ordinal);
        Assert.True(view.RefreshCommand.CanExecute(null));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BusyIsSingleFlightAndCancelledOrDisposedLateResultIsNotPublished(bool dispose)
    {
        var probe = new Probe { Wait = true };
        var picker = new Picker(@"G:\Photoshop.exe");
        using var view = new PhotoshopEnvironmentViewModel(probe, picker);
        var pending = view.DetectAsync();
        await probe.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True(view.IsBusy);
        Assert.False(view.BrowseCommand.CanExecute(null));
        await view.DetectAsync(true);
        Assert.Equal(0, picker.Calls);
        Assert.Equal(1, probe.Calls);
        if (dispose) view.Dispose(); else view.Cancel();
        var status = view.Status;
        var notifications = 0;
        view.PropertyChanged += (_, _) => notifications++;
        probe.Release.TrySetResult();
        await pending;
        Assert.Empty(view.Items);
        if (dispose) { Assert.Equal(status, view.Status); Assert.Equal(0, notifications); }
        else Assert.Contains("已取消", view.Status, StringComparison.Ordinal);
        Assert.Equal(!dispose, view.RefreshCommand.CanExecute(null));
    }

    [Fact]
    public async Task OnlyKnownItemCanBeSelectedAndRefreshPreservesPath()
    {
        var first = Snapshot().Installations[0];
        var second = first with { ExecutablePath = @"D:\Photoshop.exe", NumericFileVersion = "27.0.0.1" };
        var probe = new Probe { Result = Snapshot() with { Installations = [first, second] } };
        using var view = new PhotoshopEnvironmentViewModel(probe);
        await view.DetectAsync();
        view.SelectedItem = view.Items[1];
        view.SelectedItem = new PhotoshopEnvironmentItem(first);
        Assert.Equal(second.ExecutablePath, view.SelectedItem!.ExecutablePath);
        await view.DetectAsync();
        Assert.Equal(second.ExecutablePath, view.SelectedItem!.ExecutablePath);
        Assert.Equal(2, probe.Calls); // Merely selecting a row does not detect or call any renderer.
    }

    [Fact]
    public async Task ViewedCandidateDoesNotSaveUntilExplicitAction()
    {
        var first = Snapshot().Installations[0];
        var second = first with { ExecutablePath = @"D:\Adobe\Photoshop.exe" };
        var probe = new Probe { Result = Snapshot() with { Installations = [first, second] } };
        var selection = new HostSelectionPort
        {
            SelectResult = new(PhotoshopHostSelectionStatus.Valid, second.ExecutablePath,
                second.NumericFileVersion, new DateTimeOffset(2026, 9, 25, 8, 0, 0, TimeSpan.Zero))
        };
        using var view = new PhotoshopEnvironmentViewModel(probe, new Picker(null), selection);

        await view.DetectAsync();
        view.SelectedItem = view.Items[1];

        Assert.Equal(0, selection.SelectCalls);
        Assert.Null(view.SavedHostSelection);
        Assert.True(view.SelectHostCommand.CanExecute(null));

        await view.SelectHostAsync();

        Assert.Equal(1, selection.SelectCalls);
        Assert.Equal(second.ExecutablePath, selection.SelectedPath);
        Assert.Equal(1, probe.Calls);
        Assert.Equal(PhotoshopHostSelectionStatus.Valid, view.SavedHostSelection!.Status);
        Assert.Contains(second.ExecutablePath, view.SavedHostPathText, StringComparison.Ordinal);
        Assert.Contains(second.NumericFileVersion, view.SavedHostVersionText, StringComparison.Ordinal);
        Assert.Contains("不代表已连接", view.HostSelectionStatus, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(PhotoshopHostSelectionStatus.NotSelected, "尚未选择正式宿主")]
    [InlineData(PhotoshopHostSelectionStatus.Valid, "程序文件与记录匹配")]
    [InlineData(PhotoshopHostSelectionStatus.Missing, "程序文件已缺失")]
    [InlineData(PhotoshopHostSelectionStatus.Changed, "已更新或被替换")]
    [InlineData(PhotoshopHostSelectionStatus.InvalidRecord, "记录损坏")]
    [InlineData(PhotoshopHostSelectionStatus.Unavailable, "暂时不可安全读取")]
    public async Task ReadShowsDistinctPersistedSelectionStatesWithoutDiscoveryOrSaving(
        PhotoshopHostSelectionStatus status,
        string expected)
    {
        var probe = new Probe();
        var selection = new HostSelectionPort
        {
            ReadResult = new(status,
                status is PhotoshopHostSelectionStatus.Valid or PhotoshopHostSelectionStatus.Missing or PhotoshopHostSelectionStatus.Changed
                    ? @"G:\Adobe Photoshop 2025\Photoshop.exe" : null,
                status is PhotoshopHostSelectionStatus.Valid or PhotoshopHostSelectionStatus.Missing or PhotoshopHostSelectionStatus.Changed
                    ? "26.8.1.8" : null,
                status is PhotoshopHostSelectionStatus.Valid or PhotoshopHostSelectionStatus.Missing or PhotoshopHostSelectionStatus.Changed
                    ? new DateTimeOffset(2026, 9, 25, 7, 30, 0, TimeSpan.Zero) : null)
        };
        using var view = new PhotoshopEnvironmentViewModel(probe, new Picker(null), selection);

        await view.RefreshHostSelectionAsync();

        Assert.Equal(1, selection.ReadCalls);
        Assert.Equal(0, selection.SelectCalls);
        Assert.Equal(0, probe.Calls);
        Assert.Equal(status, view.SavedHostSelection!.Status);
        Assert.Contains(expected, view.HostSelectionStatus, StringComparison.Ordinal);
        if (status == PhotoshopHostSelectionStatus.Valid)
            Assert.Contains("不代表已连接", view.HostSelectionStatus, StringComparison.Ordinal);
        else
            Assert.DoesNotContain("已连接", view.HostSelectionStatus, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ValidReadShowsPathVersionTimeAndSnapshotOnlyUsageBoundary()
    {
        var selectedAt = new DateTimeOffset(2026, 9, 25, 7, 30, 0, TimeSpan.Zero);
        var selection = new HostSelectionPort
        {
            ReadResult = new(PhotoshopHostSelectionStatus.Valid,
                @"G:\Adobe Photoshop 2025\Photoshop.exe", "26.8.1.8", selectedAt, IsInUse: true)
        };
        using var view = new PhotoshopEnvironmentViewModel(null, new Picker(null), selection);

        await view.RefreshHostSelectionAsync();

        Assert.Contains(@"G:\Adobe Photoshop 2025\Photoshop.exe", view.SavedHostPathText, StringComparison.Ordinal);
        Assert.Contains("26.8.1.8", view.SavedHostVersionText, StringComparison.Ordinal);
        Assert.Contains(selectedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture), view.SavedHostSelectedAtText, StringComparison.Ordinal);
        Assert.Contains("正在使用", view.SavedHostUsageText, StringComparison.Ordinal);
        Assert.Contains("强制拒绝换选", view.SavedHostUsageText, StringComparison.Ordinal);
        Assert.Contains("不代表已连接", view.HostSelectionStatus, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("PHOTOSHOP_HOST_UNSUPPORTED", "不在当前支持")]
    [InlineData("PHOTOSHOP_HOST_PATH_INVALID", "本地 Photoshop.exe")]
    [InlineData("PHOTOSHOP_HOST_SELECTION_BUSY", "正在使用正式宿主")]
    [InlineData("PHOTOSHOP_HOST_SELECTION_UNAVAILABLE", "选择服务暂不可用")]
    public async Task SaveFailuresUseFixedCopyAndKeepPriorVisibleSelection(string code, string expected)
    {
        var original = new PhotoshopHostSelectionSnapshot(PhotoshopHostSelectionStatus.Valid,
            @"C:\Adobe\Photoshop.exe", "26.8.1.8", new DateTimeOffset(2026, 9, 25, 6, 0, 0, TimeSpan.Zero));
        var selection = new HostSelectionPort { ReadResult = original };
        using var view = new PhotoshopEnvironmentViewModel(
            new Probe(), new Picker(null), selection);
        await view.RefreshHostSelectionAsync();
        await view.DetectAsync();
        selection.SelectFailure = new InvalidOperationException(code);

        await view.SelectHostAsync();

        Assert.Equal(1, selection.SelectCalls);
        Assert.Same(original, view.SavedHostSelection);
        Assert.Contains(expected, view.HostSelectionStatus, StringComparison.Ordinal);
        Assert.Contains("保持不变", view.HostSelectionStatus, StringComparison.Ordinal);
        Assert.DoesNotContain(code, view.HostSelectionStatus, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SelectionIsSingleFlightAndCancelledOrDisposedLateSuccessIsNotPublished(bool dispose)
    {
        var original = new PhotoshopHostSelectionSnapshot(PhotoshopHostSelectionStatus.Valid,
            @"C:\Adobe\Photoshop.exe", "26.8.1.8");
        var selection = new HostSelectionPort
        {
            ReadResult = original,
            SelectResult = new(PhotoshopHostSelectionStatus.Valid, @"G:\late\Photoshop.exe", "26.8.1.8"),
            WaitForSelectRelease = true,
            IgnoreCancellation = true
        };
        var probe = new Probe();
        using var view = new PhotoshopEnvironmentViewModel(probe, new Picker(null), selection);
        await view.RefreshHostSelectionAsync();
        await view.DetectAsync();

        var running = view.SelectHostAsync();
        await selection.SelectStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True(view.IsSelectingHost);
        Assert.False(view.SelectHostCommand.CanExecute(null));
        Assert.False(view.RefreshHostSelectionCommand.CanExecute(null));

        await view.SelectHostAsync();
        Assert.Equal(1, selection.SelectCalls);
        if (dispose) view.Dispose(); else view.Cancel();
        var status = view.HostSelectionStatus;
        selection.SelectRelease.TrySetResult();
        await running;

        Assert.Same(original, view.SavedHostSelection);
        Assert.DoesNotContain("late", view.SavedHostPathText, StringComparison.OrdinalIgnoreCase);
        if (dispose) Assert.Equal(status, view.HostSelectionStatus);
        else Assert.Contains("已取消", view.HostSelectionStatus, StringComparison.Ordinal);
        Assert.Equal(!dispose, view.RefreshHostSelectionCommand.CanExecute(null));
    }

    [Fact]
    public async Task UnwiredPortIsDisabled()
    {
        var picker = new Picker(@"G:\Photoshop.exe");
        using var view = new PhotoshopEnvironmentViewModel(null, picker);
        Assert.False(view.RefreshCommand.CanExecute(null));
        Assert.False(view.BrowseCommand.CanExecute(null));
        Assert.False(view.RefreshHostSelectionCommand.CanExecute(null));
        Assert.False(view.SelectHostCommand.CanExecute(null));
        await view.DetectAsync(true);
        Assert.Equal(0, picker.Calls);
    }

    internal static PhotoshopEnvironmentSnapshot Snapshot() => new(
        new DateTimeOffset(2026, 9, 24, 12, 0, 0, TimeSpan.Zero), "10.0.26100.1", "X64", true,
        [new(@"G:\Adobe Photoshop 2025\Photoshop.exe", "26.8.1.8", "26.8 (20250624.r.8 5e0d05d)",
            PhotoshopDiscoverySource.RunningProcess | PhotoshopDiscoverySource.UserSelectedFile, true)], 0, false);

    private sealed class Picker(string? path) : IPhotoshopExecutablePicker
    {
        public int Calls { get; private set; }
        public string? ChoosePath() { Calls++; return path; }
    }

    internal sealed class Probe : IPhotoshopEnvironmentProbe
    {
        public PhotoshopEnvironmentSnapshot Result { get; set; } = Snapshot();
        public PhotoshopEnvironmentSnapshot? Latest => null;
        public int Calls { get; private set; }
        public string? Path { get; private set; }
        public bool Fail { get; set; }
        public bool Wait { get; init; }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<PhotoshopEnvironmentSnapshot> DetectAsync(string? additionalExecutablePath, CancellationToken cancellationToken)
        {
            Calls++;
            Path = additionalExecutablePath;
            Started.TrySetResult();
            if (Wait) await Release.Task; // Deliberately ignores cancellation to test late results.
            if (Fail) throw new IOException("secret_path");
            return Result;
        }
    }

    internal sealed class HostSelectionPort : IPhotoshopHostSelectionPort
    {
        public PhotoshopHostSelectionSnapshot ReadResult { get; set; } =
            new(PhotoshopHostSelectionStatus.NotSelected);
        public PhotoshopHostSelectionSnapshot SelectResult { get; set; } =
            new(PhotoshopHostSelectionStatus.Valid, @"G:\Adobe Photoshop 2025\Photoshop.exe", "26.8.1.8");
        public Exception? ReadFailure { get; set; }
        public Exception? SelectFailure { get; set; }
        public bool WaitForReadRelease { get; set; }
        public bool WaitForSelectRelease { get; set; }
        public bool IgnoreCancellation { get; set; }
        public int ReadCalls { get; private set; }
        public int SelectCalls { get; private set; }
        public string? SelectedPath { get; private set; }
        public CancellationToken ReadToken { get; private set; }
        public CancellationToken SelectToken { get; private set; }
        public TaskCompletionSource ReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReadRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource SelectStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource SelectRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<PhotoshopHostSelectionSnapshot> ReadAsync(CancellationToken cancellationToken)
        {
            ReadCalls++;
            ReadToken = cancellationToken;
            ReadStarted.TrySetResult();
            if (ReadFailure is not null) throw ReadFailure;
            if (WaitForReadRelease)
            {
                if (IgnoreCancellation) await ReadRelease.Task;
                else await ReadRelease.Task.WaitAsync(cancellationToken);
            }
            return ReadResult;
        }

        public async Task<PhotoshopHostSelectionSnapshot> SelectAsync(
            string executablePath,
            CancellationToken cancellationToken)
        {
            SelectCalls++;
            SelectedPath = executablePath;
            SelectToken = cancellationToken;
            SelectStarted.TrySetResult();
            if (SelectFailure is not null) throw SelectFailure;
            if (WaitForSelectRelease)
            {
                if (IgnoreCancellation) await SelectRelease.Task;
                else await SelectRelease.Task.WaitAsync(cancellationToken);
            }
            return SelectResult;
        }
    }
}
