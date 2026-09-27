using Desktop.Application.Diagnostics;

namespace Desktop.App.UiTests;

public sealed class DiagnosticExportViewModelTests
{
    [Fact]
    public async Task NoAutomaticExportAndCancelledPickerDoesNotCallPort()
    {
        var port = new ExportPort();
        var picker = new Picker(null);
        using var view = new DiagnosticExportViewModel(port, picker);
        Assert.Equal(0, port.Calls);
        Assert.Equal(0, picker.Calls);
        await view.ExportAsync();
        Assert.Equal(1, picker.Calls);
        Assert.Equal(0, port.Calls);
        Assert.Contains("没有导出", view.Status, StringComparison.Ordinal);
        Assert.True(view.ExportCommand.CanExecute(null));
    }

    [Fact]
    public async Task SingleFlightAndCancellationAreExplicit()
    {
        var port = new ExportPort { Wait = true };
        using var view = new DiagnosticExportViewModel(port, new Picker(@"C:\local\support.zip"));
        var operation = view.ExportAsync();
        await port.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True(view.IsBusy);
        Assert.False(view.ExportCommand.CanExecute(null));
        await view.ExportAsync();
        Assert.Equal(1, port.Calls);
        view.CancelCommand.Execute(null);
        await operation;
        Assert.False(view.IsBusy);
        Assert.False(view.CancelCommand.CanExecute(null));
        Assert.Contains("已取消", view.Status, StringComparison.Ordinal);
        Assert.DoesNotContain("已保存", view.Status, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("read")]
    [InlineData("missing")]
    [InlineData("unavailable")]
    public async Task SuccessShowsEvidenceLimitsAndNoUpload(string logStatus)
    {
        var port = new ExportPort { Result = new(4, 2, true, logStatus) };
        using var view = new DiagnosticExportViewModel(port, new Picker(@"C:\local\support.zip"));
        await view.ExportAsync();
        Assert.Equal(@"C:\local\support.zip", port.Path);
        Assert.Contains("已保存", view.Status, StringComparison.Ordinal);
        Assert.Contains("未上传", view.Status, StringComparison.Ordinal);
        Assert.Contains("截断", view.Status, StringComparison.Ordinal);
        Assert.Contains("尚无Photoshop环境检测快照", view.Status, StringComparison.Ordinal);
        Assert.Contains("尚无认证连接检查历史", view.Status, StringComparison.Ordinal);
        Assert.Contains("认证宿主版本仍未采集", view.Status, StringComparison.Ordinal);
        Assert.Contains("逐机渲染/ICC能力仍未验收", view.Status, StringComparison.Ordinal);
        Assert.DoesNotContain("Photoshop实际版本尚未采集", view.Status, StringComparison.Ordinal);
        if (logStatus == "unavailable") Assert.Contains("不可读取", view.Status, StringComparison.Ordinal);
        if (logStatus == "missing") Assert.Contains("尚无技术日志", view.Status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SuccessWithEnvironmentSnapshotDescribesHistoricalMetadataWithoutClaimingReadiness()
    {
        var port = new ExportPort
        {
            Result = new(1, 0, false, "read", DiagnosticPhotoshopEnvironmentStatus.HistoricalExecutableMetadata)
        };
        using var view = new DiagnosticExportViewModel(port, new Picker(@"C:\local\support.zip"));

        await view.ExportAsync();

        Assert.Contains("历史Photoshop可执行文件元数据", view.Status, StringComparison.Ordinal);
        Assert.Contains("不代表认证宿主完整构建、能力或连接", view.Status, StringComparison.Ordinal);
        Assert.DoesNotContain("Photoshop实际版本尚未采集", view.Status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AuthenticatedConnectionHistoryExplainsPointInTimeEvidenceAndCapabilityGap()
    {
        var port = new ExportPort
        {
            Result = new(1, 0, false, "read",
                DiagnosticPhotoshopEnvironmentStatus.NotCollected,
                DiagnosticPhotoshopConnectionStatus.HistoricalAuthenticatedBuildMatch)
        };
        using var view = new DiagnosticExportViewModel(port, new Picker(@"C:\local\support.zip"));

        await view.ExportAsync();

        Assert.Contains("认证连接检查时间点的批准完整构建匹配证据", view.Status, StringComparison.Ordinal);
        Assert.Contains("仅代表该检查时间点", view.Status, StringComparison.Ordinal);
        Assert.Contains("不代表持续连接", view.Status, StringComparison.Ordinal);
        Assert.Contains("逐机渲染/ICC能力仍未验收", view.Status, StringComparison.Ordinal);
        Assert.DoesNotContain("认证宿主版本仍未采集", view.Status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FailedConnectionHistoryDoesNotClaimAuthenticatedBuildOrCapabilityAcceptance()
    {
        var port = new ExportPort
        {
            Result = new(1, 0, false, "read",
                DiagnosticPhotoshopEnvironmentStatus.NotCollected,
                DiagnosticPhotoshopConnectionStatus.HistoricalCheck)
        };
        using var view = new DiagnosticExportViewModel(port, new Picker(@"C:\local\support.zip"));

        await view.ExportAsync();

        Assert.Contains("已附认证连接检查历史", view.Status, StringComparison.Ordinal);
        Assert.Contains("没有可信的批准完整构建匹配证据", view.Status, StringComparison.Ordinal);
        Assert.Contains("逐机渲染/ICC能力仍未验收", view.Status, StringComparison.Ordinal);
        Assert.DoesNotContain("批准完整构建匹配证据；不代表持续连接", view.Status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FailureIsActionableAndDoesNotExposeRawException()
    {
        var port = new ExportPort { Fail = true };
        using var view = new DiagnosticExportViewModel(port, new Picker(@"C:\local\support.zip"));
        await view.ExportAsync();
        Assert.Contains("未能导出", view.Status, StringComparison.Ordinal);
        Assert.DoesNotContain("raw_secret", view.Status, StringComparison.Ordinal);
        Assert.True(view.ExportCommand.CanExecute(null));
    }

    [Fact]
    public async Task DisposalCancelsAndCannotPublishLateState()
    {
        var port = new ExportPort { Wait = true };
        using var view = new DiagnosticExportViewModel(port, new Picker(@"C:\local\support.zip"));
        var operation = view.ExportAsync();
        await port.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        view.Dispose();
        var status = view.Status;
        var notifications = 0;
        view.PropertyChanged += (_, _) => notifications++;
        await operation;
        Assert.Equal(status, view.Status);
        Assert.Equal(0, notifications);
        Assert.False(view.ExportCommand.CanExecute(null));
    }

    [Fact]
    public async Task MissingPortDisablesExport()
    {
        var picker = new Picker(@"C:\local\support.zip");
        using var view = new DiagnosticExportViewModel(null, picker);
        Assert.False(view.ExportCommand.CanExecute(null));
        await view.ExportAsync();
        Assert.Equal(0, picker.Calls);
    }

    private sealed class Picker(string? path) : IDiagnosticSavePicker
    {
        public int Calls { get; private set; }
        public string? ChoosePath() { Calls++; return path; }
    }

    private sealed class ExportPort : IDiagnosticPackageExporter
    {
        public int Calls { get; private set; }
        public string? Path { get; private set; }
        public bool Wait { get; init; }
        public bool Fail { get; init; }
        public DiagnosticPackageResult Result { get; init; } = new(2, 0, false, "read");
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<DiagnosticPackageResult> ExportAsync(string destinationPath, CancellationToken cancellationToken)
        {
            Calls++;
            Path = destinationPath;
            Started.TrySetResult();
            if (Wait) await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            if (Fail) throw new IOException("raw_secret_failure");
            return Result;
        }
    }
}
