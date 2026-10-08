using System.ComponentModel;
using System.Text.Json.Serialization;
using System.Windows.Input;
using Desktop.Application.Rendering;

namespace Desktop.App;

/// <summary>Settings-owned device setup. No batch submission or automatic Photoshop editing.</summary>
public sealed class PhotoshopPairingViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly IPhotoshopPairingPort? _port;
    private readonly IPhotoshopConnectionCheckPort? _connectionCheck;
    private CancellationTokenSource? _pending;
    private PendingOperation _pendingOperation;
    private bool _disposed;
    private string _code = string.Empty;
    private string _status = "尚未检查 Photoshop 设备配对。";
    private string _connectionStatus;
    private PhotoshopConnectionCheckResult? _latestConnectionCheck;

    public PhotoshopPairingViewModel(
        IPhotoshopPairingPort? port,
        IPhotoshopConnectionCheckPort? connectionCheck = null)
    {
        _port = port;
        _connectionCheck = connectionCheck;
        try
        {
            _latestConnectionCheck = connectionCheck?.Latest;
            _connectionStatus = connectionCheck is null
                ? "当前入口未接入认证连接检查。"
                : _latestConnectionCheck is null
                    ? "尚未测试认证连接。服务会先重验上方已保存的正式宿主；已初始化、保存设备配对且处于等待状态的配套插件会自动接入，无需日常手点连接。检查不会启动 Photoshop、重新配对或执行渲染。"
                    : DescribeConnectionCheck(_latestConnectionCheck);
        }
        catch
        {
            _connectionStatus = "无法读取本次启动内的认证连接检查历史；未启动连接。";
        }
        PairCommand = new AsyncRelayCommand(PairAsync, () => _port is not null && _pending is null && !_disposed);
        RefreshCommand = new AsyncRelayCommand(RefreshAsync, () => _port is not null && _pending is null && !_disposed);
        CheckConnectionCommand = new AsyncRelayCommand(CheckConnectionAsync,
            () => _connectionCheck is not null && _pending is null && !_disposed);
        CheckInstanceConnectionCommand = new AsyncRelayCommand(CheckInstanceConnectionAsync,
            () => _connectionCheck is not null && _pending is null && !_disposed);
        CancelCommand = new RelayCommand(Cancel,
            () => _pending is not null && !_pending.IsCancellationRequested && !_disposed);
    }

    public ICommand PairCommand { get; }
    public ICommand RefreshCommand { get; }
    public ICommand CheckConnectionCommand { get; }
    public ICommand CheckInstanceConnectionCommand { get; }
    public ICommand CancelCommand { get; }
    public string Status => _status;
    public string ConnectionStatus => _connectionStatus;
    public string ConnectionHistory => _latestConnectionCheck is null
        ? "尚无已完成的认证连接检查历史；当前未保留认证连接。"
        : DescribeConnectionHistory(_latestConnectionCheck);
    public bool IsBusy => _pending is not null;
    public bool IsPairing => _pendingOperation == PendingOperation.Pairing;
    public bool IsCheckingConnection => _pendingOperation == PendingOperation.ConnectionCheck;
    public string CancelActionText => _pendingOperation switch
    {
        PendingOperation.Pairing => "取消配对",
        PendingOperation.PairingRefresh => "取消配对检查",
        PendingOperation.ConnectionCheck => "取消认证检查",
        _ => "取消当前操作"
    };
    [JsonIgnore] public string Code => _code;
    public event PropertyChangedEventHandler? PropertyChanged;

    public async Task RefreshAsync()
    {
        if (_port is null || _pending is not null || _disposed) return;
        using var pending = new CancellationTokenSource();
        _pending = pending;
        _pendingOperation = PendingOperation.PairingRefresh;
        _status = "正在读取本机 Photoshop 设备配对状态…";
        Notify();
        try
        {
            var configured = await _port.IsConfiguredAsync(pending.Token).ConfigureAwait(true);
            pending.Token.ThrowIfCancellationRequested();
            if (_disposed || !ReferenceEquals(_pending, pending)) return;
            _status = configured
                ? "本机已保存设备配对。正式宿主由上方单独保存；配对、检查和生成开始前仍会强制重验宿主选择。"
                : "尚未配对。请先确认上方已有有效的正式宿主，再打开该 Photoshop 中的正式渲染插件开始设备配对。";
        }
        catch (OperationCanceledException) when (pending.IsCancellationRequested)
        {
            if (!_disposed) _status = "配对状态检查已取消。";
        }
        catch (Exception) when (pending.IsCancellationRequested)
        {
            if (!_disposed) _status = "配对状态检查已取消。";
        }
        catch
        {
            if (!_disposed) _status = "无法读取本机 Photoshop 配对存储。";
        }
        finally
        {
            Finish(pending);
        }
    }

    public async Task PairAsync()
    {
        if (_port is null || _pending is not null || _disposed) return;
        using var pending = new CancellationTokenSource();
        _pending = pending;
        _pendingOperation = PendingOperation.Pairing;
        var context = SynchronizationContext.Current;
        _status = "服务将先重验上方已保存的正式宿主。请将配对码填入该 Photoshop 的正式插件，勾选“记住此设备”并连接。五分钟内有效。";
        Notify();
        try
        {
            await _port.PairAsync((bytes, token) =>
            {
                var clear = bytes.IsEmpty;
                var next = clear ? string.Empty : Convert.ToBase64String(bytes.Span)
                    .TrimEnd('=').Replace('+', '-').Replace('/', '_');
                void Present()
                {
                    if (_disposed || !clear && (token.IsCancellationRequested || _pending != pending)) return;
                    _code = next;
                    PropertyChanged?.Invoke(this, new(nameof(Code)));
                }
                // Never make service shutdown wait on the UI dispatcher.
                if (context is null || SynchronizationContext.Current == context) Present();
                else context.Post(_ => Present(), null);
                return Task.CompletedTask;
            }, pending.Token).ConfigureAwait(true);
            pending.Token.ThrowIfCancellationRequested();
            if (_disposed || !ReferenceEquals(_pending, pending)) return;
            _status = "设备配对已保存，且配对时点的认证与宿主就绪检查通过。该结果不代表当前仍连接，也不证明之后更换的正式宿主。";
        }
        catch (OperationCanceledException) when (pending.IsCancellationRequested)
        {
            if (!_disposed) _status = "设备配对已取消。";
        }
        catch (Exception) when (pending.IsCancellationRequested)
        {
            if (!_disposed) _status = "设备配对已取消。";
        }
        catch (Exception error)
        {
            if (_disposed) return;
            _status = error.Message switch
            {
                "BRIDGE_PAIRING_EXPIRED" => "配对码已过期，请重新开始设备配对。",
                "BRIDGE_BUSY" or "BRIDGE_PORT_UNAVAILABLE" => "渲染通道正在使用，请在当前任务释放 Photoshop 后重试。",
                "BRIDGE_PAIRING_STORAGE_UNAVAILABLE" => "受保护配对存储未完成保存。两端可能尚未同步，请解决存储问题后重新配对。",
                "PHOTOSHOP_HOST_SELECTION_REQUIRED" => "无法开始设备配对：尚未保存正式 Photoshop 宿主。请先在上方查看候选，明确点击“设为正式宿主”并等待保存成功。",
                "PHOTOSHOP_HOST_SELECTION_INVALID" => "无法开始设备配对：已保存的正式宿主缺失、已更新、记录损坏或不可读。请先在上方重新读取并重新设为正式宿主。",
                _ => "设备配对失败，请检查正式插件的连接状态后重试。"
            };
        }
        finally
        {
            _code = string.Empty;
            Finish(pending);
        }
    }

    public Task CheckConnectionAsync() => CheckConnectionAsync(false);

    public Task CheckInstanceConnectionAsync() => CheckConnectionAsync(true);

    private async Task CheckConnectionAsync(bool instanceContract)
    {
        if (_connectionCheck is null || _pending is not null || _disposed) return;
        using var pending = new CancellationTokenSource();
        _pending = pending;
        _pendingOperation = PendingOperation.ConnectionCheck;
        _connectionStatus = "正在检查 Photoshop 连接…请确保 Photoshop 和配套组件已启动并完成设备配对。最多等待 60 秒，不会重新配对、改设置或渲染。";
        if (instanceContract) _connectionStatus = "正在检查 Photoshop 的实例能力；" + _connectionStatus;
        Notify();
        try
        {
            var result = await (instanceContract ? _connectionCheck.CheckInstanceIdentityAsync(pending.Token)
                : _connectionCheck.CheckAsync(pending.Token)).ConfigureAwait(true);
            pending.Token.ThrowIfCancellationRequested();
            if (_disposed || !ReferenceEquals(_pending, pending)) return;
            if (result.InstanceIdentityRequired != instanceContract)
                throw new InvalidOperationException("BRIDGE_INSTANCE_CHECK_NOT_VERIFIED");
            _latestConnectionCheck = result;
            _connectionStatus = DescribeConnectionCheck(result);
        }
        catch (OperationCanceledException) when (pending.IsCancellationRequested)
        {
            if (!_disposed)
                _connectionStatus = "认证连接检查已取消；保留上次历史结果，不代表当前连接。";
        }
        catch (Exception) when (pending.IsCancellationRequested)
        {
            if (!_disposed)
                _connectionStatus = "认证连接检查已取消；保留上次历史结果，不代表当前连接。";
        }
        catch (Exception error)
        {
            if (!_disposed)
                _connectionStatus = error.Message switch
                {
                    "BRIDGE_INSTANCE_CHECK_UNSUPPORTED" or "BRIDGE_INSTANCE_CHECK_NOT_VERIFIED" => "实例能力检查未完成：此入口未返回显式实例契约验证，不能用基础连接结果替代。未执行渲染。",
                    "PHOTOSHOP_HOST_SELECTION_REQUIRED" => "无法测试认证连接：尚未保存正式 Photoshop 宿主。请先在上方查看候选，明确点击“设为正式宿主”并等待保存成功。",
                    "PHOTOSHOP_HOST_SELECTION_INVALID" => "无法测试认证连接：已保存的正式宿主缺失、已更新、记录损坏或不可读。请先在上方重新读取并重新设为正式宿主。",
                    _ => "认证连接检查失败；未保留连接。请确认已有配对、有效正式宿主和正式插件状态后重试。"
                };
        }
        finally
        {
            Finish(pending);
        }
    }

    public void Cancel()
    {
        var pending = _pending;
        if (pending is not null && !pending.IsCancellationRequested)
        {
            pending.Cancel();
            if (!_disposed)
            {
                if (_pendingOperation == PendingOperation.ConnectionCheck)
                    _connectionStatus = "正在取消认证连接检查并释放短期会话…";
                else if (_pendingOperation == PendingOperation.PairingRefresh)
                    _status = "正在取消配对状态检查…";
                else if (_pendingOperation == PendingOperation.Pairing)
                    _status = "正在取消设备配对…";
            }
        }
        _code = string.Empty;
        Notify();
    }

    private void Notify()
    {
        if (_disposed) return;
        PropertyChanged?.Invoke(this, new(nameof(Status)));
        PropertyChanged?.Invoke(this, new(nameof(ConnectionStatus)));
        PropertyChanged?.Invoke(this, new(nameof(ConnectionHistory)));
        PropertyChanged?.Invoke(this, new(nameof(IsBusy)));
        PropertyChanged?.Invoke(this, new(nameof(IsPairing)));
        PropertyChanged?.Invoke(this, new(nameof(IsCheckingConnection)));
        PropertyChanged?.Invoke(this, new(nameof(CancelActionText)));
        PropertyChanged?.Invoke(this, new(nameof(Code)));
        ((AsyncRelayCommand)PairCommand).RaiseCanExecuteChanged();
        ((AsyncRelayCommand)RefreshCommand).RaiseCanExecuteChanged();
        ((AsyncRelayCommand)CheckConnectionCommand).RaiseCanExecuteChanged();
        ((AsyncRelayCommand)CheckInstanceConnectionCommand).RaiseCanExecuteChanged();
        ((RelayCommand)CancelCommand).RaiseCanExecuteChanged();
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

    private static string DescribeConnectionCheck(PhotoshopConnectionCheckResult result)
    {
        var observedAt = result.ObservedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
        var scope = result.InstanceIdentityRequired
            ? result.Status == PhotoshopConnectionCheckStatus.Ready
                ? "实例能力检查已通过；不代表真实出图通过。"
                : "实例能力检查未通过。"
            : "基础连接检查未包含实例能力验证。";
        return scope + (result.Status switch
        {
            PhotoshopConnectionCheckStatus.Ready =>
                $"认证连接检查通过（检查时间：{observedAt}）。这是本次检查的历史结果，不代表当前连接或可直接出图。更换宿主或组件后请重新检查。",
            PhotoshopConnectionCheckStatus.PairingRequired =>
                "无法测试认证连接：本机尚未保存设备配对，请先完成设备配对。",
            PhotoshopConnectionCheckStatus.Busy =>
                "无法测试认证连接：当前任务正在使用连接通道，请稍后重试。",
            PhotoshopConnectionCheckStatus.TimedOut =>
                "认证连接检查超时。请确认 Photoshop 和配套组件已启动并完成配对；未知执行请先查询恢复，不要直接重试。",
            PhotoshopConnectionCheckStatus.HostNotReady =>
                "Photoshop 当前无法开始检查，请关闭阻塞窗口或完成当前操作后重试；本次未执行渲染。",
            PhotoshopConnectionCheckStatus.Failed =>
                "认证连接检查未完成，请确认正式宿主和设备配对后重试；本次未执行渲染。",
            PhotoshopConnectionCheckStatus.HostSelectionRequired =>
                "无法测试认证连接：尚未选择正式 Photoshop 程序，请先在上方保存选择。",
            PhotoshopConnectionCheckStatus.HostSelectionInvalid =>
                "无法测试认证连接：已保存的 Photoshop 程序已失效，请重新读取并保存选择。",
            _ =>
                "认证连接检查返回未知状态，请重新检查；本次未执行渲染。"
        });
    }

    private static string DescribeConnectionHistory(PhotoshopConnectionCheckResult result)
    {
        var build = string.IsNullOrWhiteSpace(result.MatchedPhotoshopBuild)
            ? "未匹配"
            : result.MatchedPhotoshopBuild;
        var status = result.Status == PhotoshopConnectionCheckStatus.Ready ? "已通过" : "未通过";
        return $"上次检查：{result.ObservedAt.ToLocalTime():yyyy-MM-dd HH:mm} · {(result.InstanceIdentityRequired ? "实例能力" : "基础连接")} · {status}。"
            + "结果只代表当时的检查；更换宿主或组件后请重新检查。";
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _pending?.Cancel();
        _code = string.Empty;
        GC.SuppressFinalize(this);
    }

    private enum PendingOperation
    {
        None,
        PairingRefresh,
        Pairing,
        ConnectionCheck
    }
}
