using System.Windows.Input;
using Desktop.Application.Ai;

namespace Desktop.App;

public sealed partial class MainWindowViewModel
{
    private IAiCredentialManagementPort? _aiCredentialManagement;
    private AiCredentialStatusSnapshot _aiCredentialStatus = new(
        AiCredentialState.Unavailable,
        null,
        "AI_CREDENTIAL_NOT_CHECKED");
    private string _aiCredentialInput = string.Empty;
    private string _aiCredentialActionMessage = "尚未检查本机凭据。";
    private bool _aiCredentialBusy;

    public ICommand SaveAiCredentialCommand { get; private set; } = null!;
    public ICommand RemoveAiCredentialCommand { get; private set; } = null!;
    public ICommand RefreshAiCredentialCommand { get; private set; } = null!;

    /// <summary>
    /// Raised after a successful save or when the view model is disposed so
    /// the PasswordBox can clear its UI buffer. The event carries no secret.
    /// </summary>
    public event EventHandler? AiCredentialInputCleared;

    public AiCredentialState AiCredentialState => _aiCredentialStatus.State;

    public string AiCredentialStatusState => _aiCredentialStatus.ErrorCode == "AI_CREDENTIAL_NOT_CHECKED"
        ? "Checking"
        : _aiCredentialStatus.State switch
        {
            AiCredentialState.Configured => "Configured",
            AiCredentialState.NotConfigured => "NotConfigured",
            _ => "Unavailable"
        };

    public string AiCredentialStatusText => _aiCredentialStatus.ErrorCode switch
    {
        "AI_CREDENTIAL_NOT_CHECKED" => "正在读取本机凭据",
        "AI_CREDENTIAL_MANAGEMENT_UNAVAILABLE" => "本机凭据管理未接入",
        "AI_CREDENTIAL_WINDOWS_ONLY" => "当前环境不支持 Windows 凭据管理",
        "AI_CREDENTIAL_READ_FAILED" => "本机凭据读取失败",
        _ when _aiCredentialStatus.State == AiCredentialState.Configured =>
            $"已配置 Key · {_aiCredentialStatus.MaskedSuffix}",
        _ when _aiCredentialStatus.State == AiCredentialState.NotConfigured => "尚未配置 Key",
        _ => "本机凭据不可用"
    };

    public string AiCredentialMaskedSuffix => _aiCredentialStatus.MaskedSuffix ?? "未显示";

    public string AiCredentialStatusDetailText => _aiCredentialStatus.State switch
    {
        AiCredentialState.Configured => "Key 已保存到当前 Windows 用户的安全凭据存储；应用不会显示或记录完整 Key。",
        AiCredentialState.NotConfigured => "首次扫描并分析前，请先在这里保存火山方舟 API Key。保存动作不会自动调用 AI。",
        _ when _aiCredentialStatus.ErrorCode == "AI_CREDENTIAL_NOT_CHECKED" => "正在检查当前 Windows 用户的凭据存储。",
        _ => "无法安全读取当前 Windows 用户的凭据存储；不会发起 AI 请求。"
    };

    public string AiCredentialSaveButtonText => _aiCredentialStatus.State == AiCredentialState.Configured
        ? "更换 Key"
        : "保存并启用";

    public string AiCredentialActionMessage => _aiCredentialActionMessage;

    public bool IsAiCredentialBusy => _aiCredentialBusy;

    public bool CanSaveAiCredential => _aiCredentialManagement is not null
        && !_aiCredentialBusy
        && !string.IsNullOrEmpty(_aiCredentialInput);

    public bool CanRemoveAiCredential => _aiCredentialManagement is not null
        && !_aiCredentialBusy
        && _aiCredentialStatus.State == AiCredentialState.Configured;

    public bool CanRefreshAiCredential => _aiCredentialManagement is not null && !_aiCredentialBusy;

    private void InitializeAiCredentialManagement(IAiCredentialManagementPort? management)
    {
        _aiCredentialManagement = management;
        SaveAiCredentialCommand = new AsyncRelayCommand(
            SaveAiCredentialAsync,
            () => CanSaveAiCredential,
            exception => SetAiCredentialActionMessage("Key 保存失败，本机凭据未改变。"));
        RemoveAiCredentialCommand = new AsyncRelayCommand(
            RemoveAiCredentialAsync,
            () => CanRemoveAiCredential,
            exception => SetAiCredentialActionMessage("Key 移除失败，本机凭据未改变。"));
        RefreshAiCredentialCommand = new AsyncRelayCommand(
            RefreshAiCredentialStatusAsync,
            () => CanRefreshAiCredential,
            exception => SetAiCredentialActionMessage("读取本机凭据失败，未发起 AI 请求。"));

        if (_aiCredentialManagement is null)
        {
            _aiCredentialStatus = new(
                AiCredentialState.Unavailable,
                null,
                "AI_CREDENTIAL_MANAGEMENT_UNAVAILABLE");
            _aiCredentialActionMessage = "本机凭据管理未接入；未发起 AI 请求。";
            NotifyAiChanged();
            return;
        }

        _ = RefreshAiCredentialStatusAsync();
    }

    public void SetAiCredentialInput(string? value)
    {
        var next = value ?? string.Empty;
        if (string.Equals(_aiCredentialInput, next, StringComparison.Ordinal))
        {
            return;
        }

        _aiCredentialInput = next;
        OnPropertyChanged(nameof(CanSaveAiCredential));
        (SaveAiCredentialCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
    }

    internal void ClearAiCredentialInput()
    {
        if (_aiCredentialInput.Length == 0)
        {
            return;
        }

        _aiCredentialInput = string.Empty;
        OnPropertyChanged(nameof(CanSaveAiCredential));
        (SaveAiCredentialCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        AiCredentialInputCleared?.Invoke(this, EventArgs.Empty);
    }

    private async Task<bool> EnsureAiCredentialConfiguredForScanAsync()
    {
        // Unit/UI fixtures without the production composition root intentionally
        // do not install a credential manager; retain their existing offline
        // behavior. The production desktop always registers one.
        if (_aiCredentialManagement is null)
        {
            return true;
        }

        await RefreshAiCredentialStatusAsync().ConfigureAwait(true);
        if (_aiCredentialStatus.State == AiCredentialState.Configured)
        {
            return true;
        }

        SelectedNavigationItem = NavigationItems.First(item => item.Key == "settings");
        BatchStatusMessage = _aiCredentialStatus.State == AiCredentialState.NotConfigured
            ? "请先在设置 → AI 服务中保存 API Key；保存不会自动调用 AI。"
            : "本机凭据读取失败，已阻止 AI 请求；请在设置中重新读取或更换 Key。";
        return false;
    }

    private async Task RefreshAiCredentialStatusAsync()
    {
        if (_aiCredentialManagement is null || _aiCredentialBusy)
        {
            return;
        }

        _aiCredentialBusy = true;
        NotifyAiChanged();
        try
        {
            _aiCredentialStatus = await _aiCredentialManagement
                .GetStatusAsync(CancellationToken.None)
                .ConfigureAwait(true);
            _aiCredentialActionMessage = _aiCredentialStatus.State switch
            {
                AiCredentialState.Configured => "已读取本机凭据；仅显示脱敏尾号。",
                AiCredentialState.NotConfigured => "本机尚未保存 API Key；保存后才会允许新的 AI 请求。",
                _ => "本机凭据读取失败，未发起 AI 请求。"
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            _aiCredentialStatus = new(
                AiCredentialState.Unavailable,
                null,
                "AI_CREDENTIAL_READ_FAILED");
            _aiCredentialActionMessage = "读取本机凭据失败，未发起 AI 请求。";
        }
        finally
        {
            _aiCredentialBusy = false;
            NotifyAiChanged();
        }
    }

    private async Task SaveAiCredentialAsync()
    {
        if (!CanSaveAiCredential || _aiCredentialManagement is null)
        {
            return;
        }

        var input = _aiCredentialInput;
        _aiCredentialBusy = true;
        NotifyAiChanged();
        try
        {
            await _aiCredentialManagement.SaveAsync(input, CancellationToken.None).ConfigureAwait(true);
            ClearAiCredentialInput();
            _aiCredentialStatus = await _aiCredentialManagement
                .GetStatusAsync(CancellationToken.None)
                .ConfigureAwait(true);
            _aiCredentialActionMessage = _aiCredentialStatus.State == AiCredentialState.Configured
                ? "Key 已安全保存；保存动作没有调用 AI。"
                : "Key 已写入但状态复核未通过；未发起 AI 请求。";
        }
        catch (ArgumentException exception)
        {
            _aiCredentialActionMessage = exception.Message;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            _aiCredentialActionMessage = "Key 保存失败，本机凭据未改变；未发起 AI 请求。";
        }
        finally
        {
            _aiCredentialBusy = false;
            NotifyAiChanged();
        }
    }

    private async Task RemoveAiCredentialAsync()
    {
        if (!CanRemoveAiCredential || _aiCredentialManagement is null)
        {
            return;
        }

        _aiCredentialBusy = true;
        NotifyAiChanged();
        try
        {
            await _aiCredentialManagement.RemoveAsync(CancellationToken.None).ConfigureAwait(true);
            ClearAiCredentialInput();
            _aiCredentialStatus = new(AiCredentialState.NotConfigured, null);
            _aiCredentialActionMessage = "Key 已从本机 Windows 凭据存储移除；新的 AI 请求已阻止。";
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            _aiCredentialActionMessage = "Key 移除失败，本机凭据未改变；未发起 AI 请求。";
        }
        finally
        {
            _aiCredentialBusy = false;
            NotifyAiChanged();
        }
    }

    private void SetAiCredentialActionMessage(string message)
    {
        _aiCredentialActionMessage = message;
        NotifyAiChanged();
    }

    private void NotifyAiCredentialChanged()
    {
        OnPropertyChanged(nameof(AiCredentialState));
        OnPropertyChanged(nameof(AiCredentialStatusState));
        OnPropertyChanged(nameof(AiCredentialStatusText));
        OnPropertyChanged(nameof(AiCredentialMaskedSuffix));
        OnPropertyChanged(nameof(AiCredentialStatusDetailText));
        OnPropertyChanged(nameof(AiCredentialSaveButtonText));
        OnPropertyChanged(nameof(AiCredentialActionMessage));
        OnPropertyChanged(nameof(IsAiCredentialBusy));
        OnPropertyChanged(nameof(CanSaveAiCredential));
        OnPropertyChanged(nameof(CanRemoveAiCredential));
        OnPropertyChanged(nameof(CanRefreshAiCredential));
        (SaveAiCredentialCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (RemoveAiCredentialCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (RefreshAiCredentialCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
    }
}
