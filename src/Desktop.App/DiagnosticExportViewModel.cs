using System.ComponentModel;
using System.Windows.Input;
using Desktop.Application.Diagnostics;
using Microsoft.Win32;

namespace Desktop.App;

internal interface IDiagnosticSavePicker
{
    string? ChoosePath();
}

internal sealed class WindowsDiagnosticSavePicker : IDiagnosticSavePicker
{
    public string? ChoosePath()
    {
        var dialog = new SaveFileDialog
        {
            Title = "导出脱敏诊断包（仅保存在本机，不会上传）",
            Filter = "诊断包 (*.zip)|*.zip",
            DefaultExt = ".zip",
            AddExtension = true,
            FileName = $"desktop-diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}.zip",
            OverwritePrompt = false,
            CheckPathExists = true,
            RestoreDirectory = true
        };
        return dialog.ShowDialog(System.Windows.Application.Current.MainWindow) == true ? dialog.FileName : null;
    }
}

/// <summary>Explicit export only; opening settings never writes a diagnostic package.</summary>
public sealed class DiagnosticExportViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly IDiagnosticPackageExporter? _exporter;
    private readonly IDiagnosticSavePicker _picker;
    private CancellationTokenSource? _pending;
    private bool _disposed;
    private string _status = "仅在点击导出并选择本地新文件后生成；不会自动上传。";

    public DiagnosticExportViewModel(IDiagnosticPackageExporter? exporter)
        : this(exporter, new WindowsDiagnosticSavePicker()) { }

    internal DiagnosticExportViewModel(IDiagnosticPackageExporter? exporter, IDiagnosticSavePicker picker)
    {
        _exporter = exporter;
        _picker = picker;
        ExportCommand = new AsyncRelayCommand(ExportAsync, () => exporter is not null && !IsBusy && !_disposed);
        CancelCommand = new RelayCommand(Cancel, () => IsBusy);
        if (exporter is null) _status = "当前运行入口未接入诊断导出。";
    }

    public ICommand ExportCommand { get; }
    public ICommand CancelCommand { get; }
    public bool IsBusy => _pending is not null;
    public string Status => _status;
    public event PropertyChangedEventHandler? PropertyChanged;

    public async Task ExportAsync()
    {
        if (_exporter is null || IsBusy || _disposed) return;
        using var pending = new CancellationTokenSource();
        _pending = pending;
        SetStatus("请选择本地新的 .zip 文件；已有文件不会被覆盖。", refreshCommands: true);
        try
        {
            var path = _picker.ChoosePath();
            if (path is null) { SetStatus("已取消选择；没有导出诊断包。"); return; }
            pending.Token.ThrowIfCancellationRequested();
            SetStatus("正在本机生成脱敏诊断包…不会读取图片、API Key 或内部数据。");
            var result = await _exporter.ExportAsync(path, pending.Token).ConfigureAwait(true);
            if (_disposed) return;
            var limits = result.LogStatus switch
            {
                "missing" => "本机尚无技术日志；已导出清单与可用的诊断元数据。",
                "unavailable" => "技术日志不可读取；已导出清单与可用的诊断元数据。",
                _ => $"含 {result.ExportedRecords} 条白名单技术记录，省略 {result.OmittedRecords} 条。"
            };
            var photoshopEvidence = result.PhotoshopEnvironmentStatus switch
            {
                DiagnosticPhotoshopEnvironmentStatus.HistoricalExecutableMetadata =>
                    "已附上次 Photoshop 环境检查结果；不代表当前连接或可直接出图。",
                _ => "尚无Photoshop环境检测快照。"
            };
            var photoshopConnectionEvidence = result.PhotoshopConnectionStatus switch
            {
                DiagnosticPhotoshopConnectionStatus.HistoricalAuthenticatedBuildMatch =>
                    "已附上次连接检查结果；只代表当时的检查，不代表当前连接或正式出图。",
                DiagnosticPhotoshopConnectionStatus.HistoricalCheck =>
                    "已附连接检查历史，但当时未完成检查；请重新检查后再继续。",
                _ => "尚无连接检查历史；完成设备配对后可在这里检查。"
            };
            SetStatus($"诊断包已保存到所选位置，未上传。{limits}"
                + (result.Truncated ? "日志已按安全上限截断。" : string.Empty)
                + photoshopEvidence
                + photoshopConnectionEvidence);
        }
        catch (OperationCanceledException) when (pending.IsCancellationRequested)
        { SetStatus("导出已取消；未发布新诊断包。"); }
        catch (Exception)
        { SetStatus("诊断包未能导出。请选择可写的本地新 .zip 文件，避开程序数据目录、网络位置和已有文件后重试。原文件未覆盖。"); }
        finally
        {
            _pending = null;
            if (!_disposed) Notify();
        }
    }

    public void Cancel() => _pending?.Cancel();

    private void SetStatus(string value, bool refreshCommands = false)
    {
        if (_disposed) return;
        _status = value;
        PropertyChanged?.Invoke(this, new(nameof(Status)));
        if (refreshCommands) Notify();
    }

    private void Notify()
    {
        PropertyChanged?.Invoke(this, new(nameof(IsBusy)));
        ((AsyncRelayCommand)ExportCommand).RaiseCanExecuteChanged();
        ((RelayCommand)CancelCommand).RaiseCanExecuteChanged();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Cancel();
        GC.SuppressFinalize(this);
    }
}
