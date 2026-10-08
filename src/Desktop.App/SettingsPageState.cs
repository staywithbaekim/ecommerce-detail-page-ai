using System.ComponentModel;
using System.Runtime.CompilerServices;
using Desktop.Application.Ai;

namespace Desktop.App;

/// <summary>
/// Presentation state for the settings page.  The settings-owned credential
/// coordinator performs port calls, while this object owns the observable
/// status, input buffer and action text shown by the page.
/// </summary>
public sealed class SettingsPageState : INotifyPropertyChanged
{
    private AiCredentialStatusSnapshot _credentialStatus = new(
        AiCredentialState.Unavailable,
        null,
        "AI_CREDENTIAL_NOT_CHECKED");
    private string _credentialInput = string.Empty;
    private string _credentialActionMessage = "尚未检查本机凭据。";
    private bool _credentialBusy;
    private bool _credentialManagementAvailable;

    public AiCredentialState AiCredentialState => _credentialStatus.State;

    public string AiCredentialStatusState => _credentialStatus.ErrorCode == "AI_CREDENTIAL_NOT_CHECKED"
        ? "Checking"
        : _credentialStatus.State switch
        {
            AiCredentialState.Configured => "Configured",
            AiCredentialState.NotConfigured => "NotConfigured",
            _ => "Unavailable"
        };

    public string AiCredentialStatusText => _credentialStatus.ErrorCode switch
    {
        "AI_CREDENTIAL_NOT_CHECKED" => "正在读取本机凭据",
        "AI_CREDENTIAL_MANAGEMENT_UNAVAILABLE" => "本机凭据管理未接入",
        "AI_CREDENTIAL_WINDOWS_ONLY" => "当前环境不支持 Windows 凭据管理",
        "AI_CREDENTIAL_READ_FAILED" => "本机凭据读取失败",
        _ when _credentialStatus.State == AiCredentialState.Configured =>
            $"已配置 Key · {_credentialStatus.MaskedSuffix}",
        _ when _credentialStatus.State == AiCredentialState.NotConfigured => "尚未配置 Key",
        _ => "本机凭据不可用"
    };

    public string AiCredentialMaskedSuffix => _credentialStatus.MaskedSuffix ?? "未显示";

    public string AiCredentialStatusDetailText => _credentialStatus.State switch
    {
        AiCredentialState.Configured => "Key 已保存到当前 Windows 用户的安全凭据存储；应用不会显示或记录完整 Key。",
        AiCredentialState.NotConfigured => "首次扫描并分析前，请先在这里保存 AI API Key。保存动作不会自动调用 AI。",
        _ when _credentialStatus.ErrorCode == "AI_CREDENTIAL_NOT_CHECKED" => "正在检查当前 Windows 用户的凭据存储。",
        _ => "无法安全读取当前 Windows 用户的凭据存储；不会发起 AI 请求。"
    };

    public string AiCredentialSaveButtonText => _credentialStatus.State == AiCredentialState.Configured
        ? "更换 Key"
        : "保存并启用";

    public string AiCredentialActionMessage => _credentialActionMessage;

    public bool IsAiCredentialBusy => _credentialBusy;

    public string CredentialInput => _credentialInput;

    public bool CanSaveAiCredential => _credentialManagementAvailable
        && !_credentialBusy
        && !string.IsNullOrEmpty(_credentialInput);

    public bool CanRemoveAiCredential => _credentialManagementAvailable
        && !_credentialBusy
        && _credentialStatus.State == AiCredentialState.Configured;

    public bool CanRefreshAiCredential => _credentialManagementAvailable && !_credentialBusy;

    public void ConfigureCredentialManagement(bool available)
    {
        if (_credentialManagementAvailable == available)
        {
            return;
        }

        _credentialManagementAvailable = available;
        NotifyCredentialStateChanged();
    }

    public void SetCredentialStatus(AiCredentialStatusSnapshot status)
    {
        _credentialStatus = status;
        NotifyCredentialStateChanged();
    }

    public void SetCredentialActionMessage(string message)
    {
        if (string.Equals(_credentialActionMessage, message, StringComparison.Ordinal))
        {
            return;
        }

        _credentialActionMessage = message;
        OnPropertyChanged(nameof(AiCredentialActionMessage));
    }

    public void SetCredentialBusy(bool busy)
    {
        if (_credentialBusy == busy)
        {
            return;
        }

        _credentialBusy = busy;
        NotifyCredentialStateChanged();
    }

    public bool SetCredentialInput(string? value)
    {
        var next = value ?? string.Empty;
        if (string.Equals(_credentialInput, next, StringComparison.Ordinal))
        {
            return false;
        }

        _credentialInput = next;
        OnPropertyChanged(nameof(CredentialInput));
        OnPropertyChanged(nameof(CanSaveAiCredential));
        return true;
    }

    public bool ClearCredentialInput() => SetCredentialInput(string.Empty);

    private void NotifyCredentialStateChanged()
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
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    public event PropertyChangedEventHandler? PropertyChanged;
}
