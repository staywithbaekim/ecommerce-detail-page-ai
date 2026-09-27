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
        CancelCommand = new RelayCommand(Cancel,
            () => _pending is not null && !_pending.IsCancellationRequested && !_disposed);
    }

    public ICommand PairCommand { get; }
    public ICommand RefreshCommand { get; }
    public ICommand CheckConnectionCommand { get; }
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

    public async Task CheckConnectionAsync()
    {
        if (_connectionCheck is null || _pending is not null || _disposed) return;
        using var pending = new CancellationTokenSource();
        _pending = pending;
        _pendingOperation = PendingOperation.ConnectionCheck;
        _connectionStatus = "正在测试认证连接…等待已保存正式宿主中的配套插件自动接入。请确保 Photoshop 已启动且组件已初始化、未暂停等待；最多等待 60 秒完成认证与心跳验证。不会重新配对、改设置或渲染。";
        Notify();
        try
        {
            var result = await _connectionCheck.CheckAsync(pending.Token).ConfigureAwait(true);
            pending.Token.ThrowIfCancellationRequested();
            if (_disposed || !ReferenceEquals(_pending, pending)) return;
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
        return result.Status switch
        {
            PhotoshopConnectionCheckStatus.Ready =>
                $"认证连接检查通过（检查时间：{observedAt}）。检查时使用的已保存正式宿主通过了选择重验；监听已释放。下次测试或生成由桌面发起，已初始化且未暂停的配套插件可自动接入；交付状态不明时停止自动等待。此结果不证明组件已安装或自动等待仍可用。这是历史结果，不代表当前连接，也不会因上方查看下拉变化而证明新查看项或之后更换的正式宿主有效；不代表逐机能力或可生产。",
            PhotoshopConnectionCheckStatus.PairingRequired =>
                "无法测试认证连接：本机尚未保存设备配对。请先显式完成设备配对；检查不会自动生成或更换秘密。",
            PhotoshopConnectionCheckStatus.Busy =>
                "无法测试认证连接：渲染通道或固定端口正在使用。不会抢占当前任务，请等待释放后重试。",
            PhotoshopConnectionCheckStatus.TimedOut =>
                "认证连接检查在 60 秒上限内未完成。请检查已保存宿主是否启动、配套组件是否安装并初始化、是否暂停等待或因未知执行而停止，以及批准完整构建与两端配对；超时本身不能确定具体原因。未知执行请先在桌面查询恢复，不要强行重投。监听已释放，未证明当前连接。",
            PhotoshopConnectionCheckStatus.HostNotReady =>
                "已收到至少 3 次认证心跳，但检查时 Photoshop 宿主不是 ready，检查已立即结束。请处理宿主忙碌或模态窗口后重试；本次未执行渲染。",
            PhotoshopConnectionCheckStatus.Failed =>
                "认证连接检查自身未能完成。请检查本机受保护配对存储、正式宿主选择与受控通道后重试；本次未执行渲染，也未保存新配对。",
            PhotoshopConnectionCheckStatus.HostSelectionRequired =>
                "无法测试认证连接：尚未保存正式 Photoshop 宿主。请先在上方查看候选，明确点击“设为正式宿主”并等待保存成功。",
            PhotoshopConnectionCheckStatus.HostSelectionInvalid =>
                "无法测试认证连接：已保存的正式宿主缺失、已更新、记录损坏或不可读。请先在上方重新读取并重新设为正式宿主。",
            _ =>
                "认证连接检查返回未知状态。请重新检查；本次未执行渲染，也未保留连接。"
        };
    }

    private static string DescribeConnectionHistory(PhotoshopConnectionCheckResult result)
    {
        var build = string.IsNullOrWhiteSpace(result.MatchedPhotoshopBuild)
            ? "未匹配"
            : result.MatchedPhotoshopBuild;
        return $"检查时间：{result.ObservedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss}；"
            + $"认证心跳：{Math.Max(0, result.AuthenticatedHeartbeats)} 次；"
            + $"匹配完整构建：{build}。这是进程内历史；结果仅在监听释放后发布，检查结束后不保留连接，下次测试或生成需重新连接。"
            + "它不会随上方查看下拉改变而证明新查看项有效；如果正式宿主后来被更换、更新或失效，必须重新检查。";
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
