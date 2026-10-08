using System.ComponentModel;
using System.Windows.Input;
using Desktop.Application.Diagnostics;
using Desktop.Application.Rendering;
using Microsoft.Win32;

namespace Desktop.App;

internal interface IPhotoshopExecutablePicker { string? ChoosePath(); }

internal sealed class WindowsPhotoshopExecutablePicker : IPhotoshopExecutablePicker
{
    public string? ChoosePath()
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择 Photoshop.exe；选取本身只检查版本，保存需返回页面明确点击“设为正式宿主”",
            Filter = "Photoshop 程序 (Photoshop.exe)|Photoshop.exe",
            CheckFileExists = true,
            Multiselect = false,
            RestoreDirectory = true
        };
        return dialog.ShowDialog(System.Windows.Application.Current.MainWindow) == true ? dialog.FileName : null;
    }
}

public sealed class PhotoshopEnvironmentItem(PhotoshopInstallation installation)
{
    public PhotoshopInstallation Installation { get; } = installation;
    public string Summary => $"Photoshop {Installation.NumericFileVersion} · "
        + (Installation.IsRunning ? "检测时有运行进程" : "检测时未发现对应进程");
    public string ExecutablePath => Installation.ExecutablePath;
    public string VersionDetail => $"版本：{Installation.NumericFileVersion}";
    public string SourceText => "检测来源：" + string.Join("、", new[]
    {
        (PhotoshopDiscoverySource.RunningProcess, "运行进程"),
        (PhotoshopDiscoverySource.Registry, "安装登记"),
        (PhotoshopDiscoverySource.StandardDirectory, "Adobe安装目录"),
        (PhotoshopDiscoverySource.UserSelectedFile, "手动选择文件")
    }.Where(item => Installation.Source.HasFlag(item.Item1)).Select(item => item.Item2));
    public string VersionScope => Installation.IsVersionCandidate
        ? "支持版本候选；保存后仍需完成设备配对和连接检查。"
        : "不在当前首发支持版本范围，请更换受支持版本。";
}

/// <summary>
/// Settings-owned read-only inventory plus an explicit persisted host choice.
/// Merely detecting or changing the viewed item never saves a rendering host.
/// </summary>
public sealed class PhotoshopEnvironmentViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly IPhotoshopEnvironmentProbe? _probe;
    private readonly IPhotoshopHostSelectionPort? _hostSelection;
    private readonly IPhotoshopExecutablePicker _picker;
    private CancellationTokenSource? _pending;
    private PendingOperation _pendingOperation;
    private bool _disposed;
    private string _status;
    private string _hostSelectionStatus;
    private PhotoshopEnvironmentSnapshot? _snapshot;
    private PhotoshopHostSelectionSnapshot? _savedHostSelection;
    private PhotoshopEnvironmentItem[] _items = [];
    private PhotoshopEnvironmentItem? _selected;

    public PhotoshopEnvironmentViewModel(
        IPhotoshopEnvironmentProbe? probe,
        IPhotoshopHostSelectionPort? hostSelection = null)
        : this(probe, new WindowsPhotoshopExecutablePicker(), hostSelection)
    {
    }

    internal PhotoshopEnvironmentViewModel(
        IPhotoshopEnvironmentProbe? probe,
        IPhotoshopExecutablePicker picker,
        IPhotoshopHostSelectionPort? hostSelection = null)
    {
        _probe = probe;
        _picker = picker;
        _hostSelection = hostSelection;
        _status = probe is null
            ? "当前入口未接入环境检测。"
            : "尚未检测。点击重新检测仅读取本机程序元数据，不会启动或操作 Photoshop。";
        _hostSelectionStatus = hostSelection is null
            ? "当前入口未接入正式宿主选择；检测列表仍只用于查看候选。"
            : "尚未读取已保存的正式宿主。读取不会检测全机、启动 Photoshop、连接、配对或渲染。";

        RefreshCommand = new AsyncRelayCommand(() => DetectAsync(false),
            () => probe is not null && !IsBusy && !_disposed);
        BrowseCommand = new AsyncRelayCommand(() => DetectAsync(true),
            () => probe is not null && !IsBusy && !_disposed);
        RefreshHostSelectionCommand = new AsyncRelayCommand(RefreshHostSelectionAsync,
            () => hostSelection is not null && !IsBusy && !_disposed);
        SelectHostCommand = new AsyncRelayCommand(SelectHostAsync,
            () => hostSelection is not null && SelectedItem is not null && !IsBusy && !_disposed);
        CancelCommand = new RelayCommand(Cancel,
            () => IsBusy && _pending is { IsCancellationRequested: false } && !_disposed);
    }

    public ICommand RefreshCommand { get; }
    public ICommand BrowseCommand { get; }
    public ICommand RefreshHostSelectionCommand { get; }
    public ICommand SelectHostCommand { get; }
    public ICommand CancelCommand { get; }
    public bool IsBusy => _pending is not null;
    public bool IsDetecting => _pendingOperation == PendingOperation.Detection;
    public bool IsReadingHostSelection => _pendingOperation == PendingOperation.HostSelectionRead;
    public bool IsSelectingHost => _pendingOperation == PendingOperation.HostSelectionSave;
    public string CancelActionText => _pendingOperation switch
    {
        PendingOperation.Detection => "取消检测",
        PendingOperation.HostSelectionRead => "取消读取宿主",
        PendingOperation.HostSelectionSave => "取消设置宿主",
        _ => "取消当前环境操作"
    };
    public string Status => _status;
    public string HostSelectionStatus => _hostSelectionStatus;
    public PhotoshopHostSelectionSnapshot? SavedHostSelection => _savedHostSelection;
    public IReadOnlyList<PhotoshopEnvironmentItem> Items => _items;
    public bool HasItems => _items.Length > 0;
    public PhotoshopEnvironmentItem? SelectedItem
    {
        get => _selected;
        set
        {
            if (_disposed || value == _selected || value is not null && !_items.Contains(value)) return;
            _selected = value;
            PropertyChanged?.Invoke(this, new(nameof(SelectedItem)));
            ((AsyncRelayCommand)SelectHostCommand).RaiseCanExecuteChanged();
        }
    }

    public string ObservationText => _snapshot is null ? "尚未检查本机环境。"
        : $"上次检查：{_snapshot.ObservedAt.ToLocalTime():yyyy-MM-dd HH:mm}。结果只代表当时的环境。";
    public string OperatingSystemText => _snapshot is null ? "尚未检查系统环境。"
        : _snapshot.IsOsCandidate
            ? "本机系统符合首发支持范围，仍需完成安装和连接检查。"
            : "本机系统不在当前首发支持范围，或无法确认客户端类型。";

    public string SavedHostPathText => _savedHostSelection switch
    {
        null => "已保存程序路径：尚未读取。",
        { Status: PhotoshopHostSelectionStatus.NotSelected } => "已保存程序路径：无。",
        { Status: PhotoshopHostSelectionStatus.InvalidRecord } => "已保存程序路径：记录损坏，未采用其中内容。",
        { Status: PhotoshopHostSelectionStatus.Unavailable } => "已保存程序路径：暂时不可读。",
        { ExecutablePath: { Length: > 0 } } => "已保存 Photoshop 程序。",
        _ => "已保存的 Photoshop 程序记录未提供。"
    };

    public string SavedHostVersionText => _savedHostSelection switch
    {
        null => "已保存数字版本：尚未读取。",
        { Status: PhotoshopHostSelectionStatus.NotSelected } => "已保存数字版本：无。",
        { Status: PhotoshopHostSelectionStatus.InvalidRecord } => "已保存数字版本：记录损坏，未采用其中内容。",
        { Status: PhotoshopHostSelectionStatus.Unavailable } => "已保存数字版本：暂时不可读。",
        { NumericFileVersion: { Length: > 0 } version } => $"已保存数字版本：{version}",
        _ => "已保存数字版本：记录未提供。"
    };

    public string SavedHostSelectedAtText => _savedHostSelection?.SelectedAt is { } selectedAt
        ? $"选择时间：{selectedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss}"
        : "选择时间：无可用记录。";

    public string SavedHostUsageText => _savedHostSelection switch
    {
        null => "读取时占用快照：尚未读取；最终能否换选由保存服务在操作时重新判定。",
        { IsInUse: true } => "读取时占用快照：正在使用。检查、配对、生成或启动中的租约释放前，服务会强制拒绝换选。",
        _ => "读取时占用快照：未显示正在使用。这不是换选许可；保存时服务仍会重新检查，并在活动期间强制拒绝。"
    };

    public event PropertyChangedEventHandler? PropertyChanged;

    public async Task DetectAsync(bool chooseFile = false)
    {
        if (_probe is null || IsBusy || _disposed) return;
        using var pending = Begin(PendingOperation.Detection);
        try
        {
            string? path = null;
            if (chooseFile)
            {
                path = _picker.ChoosePath();
                if (path is null)
                {
                    SetDetectionStatus("已取消选择文件；未启动检测或更改正式宿主。");
                    return;
                }
            }

            pending.Token.ThrowIfCancellationRequested();
            SetDetectionStatus("正在只读检测本机 Photoshop 程序与版本…不会连接、保存宿主或接管 Photoshop。");
            var snapshot = await _probe.DetectAsync(path, pending.Token).ConfigureAwait(true);
            pending.Token.ThrowIfCancellationRequested();
            if (!CanPublish(pending)) return;

            var priorPath = _selected?.ExecutablePath;
            _snapshot = snapshot;
            _items = snapshot.Installations.Select(item => new PhotoshopEnvironmentItem(item)).ToArray();
            _selected = _items.FirstOrDefault(item => string.Equals(item.ExecutablePath, priorPath, StringComparison.OrdinalIgnoreCase))
                ?? (_items.Length > 0 ? _items[0] : null);
            SetDetectionStatus((_items.Length == 0
                    ? "未发现可识别的本机 Photoshop；可选择实际 Photoshop.exe 补充检测（不代表已证明未安装）。"
                    : $"检测到 {_items.Length} 个程序。下拉只切换查看项；只有明确点击“设为正式宿主”并保存成功才会更新正式选择。")
                + (snapshot.UnavailableSources > 0 ? $"有 {snapshot.UnavailableSources} 个来源/路径不可读或无法识别，结果可能不完整。" : string.Empty)
                + (snapshot.Truncated ? "已达到安全扫描上限，结果可能不完整。" : string.Empty));
        }
        catch (OperationCanceledException) when (pending.IsCancellationRequested)
        {
            SetDetectionStatus("检测已取消；保留上次快照，不代表实时状态。");
        }
        catch (Exception) when (pending.IsCancellationRequested)
        {
            SetDetectionStatus("检测已取消；保留上次快照，不代表实时状态。");
        }
        catch
        {
            SetDetectionStatus("无法完成检测。请重新检测或选择可读的本地 Photoshop.exe；网络/链接路径及其他程序不支持。上次快照如有则保留，不能视作当前连接状态。");
        }
        finally
        {
            Finish(pending);
        }
    }

    public async Task RefreshHostSelectionAsync()
    {
        if (_hostSelection is null || IsBusy || _disposed) return;
        using var pending = Begin(PendingOperation.HostSelectionRead);
        _hostSelectionStatus = "正在只读刷新已保存的正式宿主…不会检测全机、启动 Photoshop、连接、配对或渲染。";
        Notify();
        try
        {
            var snapshot = await _hostSelection.ReadAsync(pending.Token).ConfigureAwait(true);
            pending.Token.ThrowIfCancellationRequested();
            if (!CanPublish(pending)) return;
            PublishHostSelection(snapshot, wasExplicitSave: false);
        }
        catch (OperationCanceledException) when (pending.IsCancellationRequested)
        {
            SetHostSelectionStatus("读取已取消；保留上次可见的正式宿主记录。");
        }
        catch (Exception) when (pending.IsCancellationRequested)
        {
            SetHostSelectionStatus("读取已取消；保留上次可见的正式宿主记录。");
        }
        catch
        {
            SetHostSelectionStatus("无法读取已保存的正式宿主；保留上次可见记录，不能据此断言当前有选择或选择有效。请稍后重试。");
        }
        finally
        {
            Finish(pending);
        }
    }

    public async Task SelectHostAsync()
    {
        if (_hostSelection is null || SelectedItem is null || IsBusy || _disposed) return;
        var candidatePath = SelectedItem.ExecutablePath;
        using var pending = Begin(PendingOperation.HostSelectionSave);
        _hostSelectionStatus = "正在验证并保存当前查看的程序为正式宿主…保存前不会改变原记录，也不会启动、连接或渲染。";
        Notify();
        try
        {
            var snapshot = await _hostSelection.SelectAsync(candidatePath, pending.Token).ConfigureAwait(true);
            pending.Token.ThrowIfCancellationRequested();
            if (!CanPublish(pending)) return;
            PublishHostSelection(snapshot, wasExplicitSave: true);
        }
        catch (OperationCanceledException) when (pending.IsCancellationRequested)
        {
            SetHostSelectionStatus("设置正式宿主已取消；保留原已保存记录，未显示迟到成功。");
        }
        catch (Exception) when (pending.IsCancellationRequested)
        {
            SetHostSelectionStatus("设置正式宿主已取消；保留原已保存记录，未显示迟到成功。");
        }
        catch (Exception error)
        {
            SetHostSelectionStatus(error.Message switch
            {
                "PHOTOSHOP_HOST_UNSUPPORTED" => "无法设为正式宿主：所选程序不在当前支持的 Photoshop 2025 26.8.1 范围。原已保存记录保持不变。",
                "PHOTOSHOP_HOST_PATH_INVALID" => "无法设为正式宿主：请选择受支持的本地 Photoshop.exe；网络、设备、附加数据流、链接或其他不安全路径不接受。原已保存记录保持不变。",
                "PHOTOSHOP_HOST_SELECTION_BUSY" => "当前检查、配对、生成或启动流程正在使用正式宿主，不能换选。请等待活动结束后重试；原已保存记录保持不变。",
                "PHOTOSHOP_HOST_SELECTION_UNAVAILABLE" => "正式宿主选择服务暂不可用，未能安全保存。请检查本机设置存储后重试；原已保存记录保持不变。",
                _ => "无法保存正式宿主；原已保存记录保持不变。请重新读取状态并重试。"
            });
        }
        finally
        {
            Finish(pending);
        }
    }

    public void Cancel()
    {
        var pending = _pending;
        if (pending is null || pending.IsCancellationRequested) return;
        pending.Cancel();
        if (!_disposed)
        {
            if (_pendingOperation == PendingOperation.Detection)
                _status = "正在取消环境检测…";
            else if (_pendingOperation == PendingOperation.HostSelectionRead)
                _hostSelectionStatus = "正在取消已保存宿主读取…";
            else if (_pendingOperation == PendingOperation.HostSelectionSave)
                _hostSelectionStatus = "正在取消正式宿主设置…原记录尚未改变。";
            Notify();
        }
    }

    private CancellationTokenSource Begin(PendingOperation operation)
    {
        var pending = new CancellationTokenSource();
        _pending = pending;
        _pendingOperation = operation;
        Notify();
        return pending;
    }

    private bool CanPublish(CancellationTokenSource pending) =>
        !_disposed && !pending.IsCancellationRequested && ReferenceEquals(_pending, pending);

    private void PublishHostSelection(PhotoshopHostSelectionSnapshot snapshot, bool wasExplicitSave)
    {
        _savedHostSelection = snapshot;
        _hostSelectionStatus = snapshot.Status switch
        {
            PhotoshopHostSelectionStatus.NotSelected => wasExplicitSave
                ? "服务未确认新的正式宿主，当前没有已保存选择。请重新查看候选并明确重试。"
                : "尚未选择正式宿主。请先只读检测或选择程序检查版本，再对确认的查看项点击“设为正式宿主”。",
            PhotoshopHostSelectionStatus.Valid => "正式宿主已保存；不代表已连接，请继续设备配对和连接检查。",
            PhotoshopHostSelectionStatus.Missing => "已保存的 Photoshop 程序已找不到，请重新选择并保存。",
            PhotoshopHostSelectionStatus.Changed => "已保存的 Photoshop 程序已发生变化，请重新选择并保存。",
            PhotoshopHostSelectionStatus.InvalidRecord => "已保存的 Photoshop 程序记录无效，请重新选择并保存。",
            PhotoshopHostSelectionStatus.Unavailable => "暂时无法读取已保存的 Photoshop 程序，请稍后重试。",
            _ => "正式宿主选择返回未知状态，未据此确认选择有效。"
        };
        Notify();
    }

    private void SetDetectionStatus(string value)
    {
        if (_disposed) return;
        _status = value;
        Notify();
    }

    private void SetHostSelectionStatus(string value)
    {
        if (_disposed) return;
        _hostSelectionStatus = value;
        Notify();
    }

    private void Finish(CancellationTokenSource pending)
    {
        if (ReferenceEquals(_pending, pending))
        {
            _pending = null;
            _pendingOperation = PendingOperation.None;
        }
        Notify();
    }

    private void Notify()
    {
        if (_disposed) return;
        foreach (var name in new[]
        {
            nameof(Status), nameof(HostSelectionStatus), nameof(SavedHostSelection),
            nameof(SavedHostPathText), nameof(SavedHostVersionText), nameof(SavedHostSelectedAtText), nameof(SavedHostUsageText),
            nameof(IsBusy), nameof(IsDetecting), nameof(IsReadingHostSelection), nameof(IsSelectingHost), nameof(CancelActionText),
            nameof(Items), nameof(HasItems), nameof(SelectedItem), nameof(ObservationText), nameof(OperatingSystemText)
        })
            PropertyChanged?.Invoke(this, new(name));
        ((AsyncRelayCommand)RefreshCommand).RaiseCanExecuteChanged();
        ((AsyncRelayCommand)BrowseCommand).RaiseCanExecuteChanged();
        ((AsyncRelayCommand)RefreshHostSelectionCommand).RaiseCanExecuteChanged();
        ((AsyncRelayCommand)SelectHostCommand).RaiseCanExecuteChanged();
        ((RelayCommand)CancelCommand).RaiseCanExecuteChanged();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _pending?.Cancel();
        GC.SuppressFinalize(this);
    }

    private enum PendingOperation
    {
        None,
        Detection,
        HostSelectionRead,
        HostSelectionSave
    }
}
