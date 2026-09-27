using System.ComponentModel;
using System.Windows.Input;
using Desktop.Application.Ai;

namespace Desktop.App;

/// <summary>Settings-owned local credential operations; does not invoke AI or navigate.</summary>
public sealed class AiCredentialViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly IAiCredentialManagementPort? _aiCredentialManagement;
    private bool _disposed;

    public AiCredentialViewModel(IAiCredentialManagementPort? management)
    {
        _aiCredentialManagement = management;
        State.ConfigureCredentialManagement(management is not null);
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
        State.PropertyChanged += State_PropertyChanged;
    }

    public SettingsPageState State { get; } = new();
    public ICommand SaveAiCredentialCommand { get; }
    public ICommand RemoveAiCredentialCommand { get; }
    public ICommand RefreshAiCredentialCommand { get; }
    public event EventHandler? AiCredentialInputCleared;
    public event PropertyChangedEventHandler? PropertyChanged;
    public AiCredentialState AiCredentialState => State.AiCredentialState;

    public string AiCredentialStatusState => State.AiCredentialStatusState;

    public string AiCredentialStatusText => State.AiCredentialStatusText;

    public string AiCredentialMaskedSuffix => State.AiCredentialMaskedSuffix;

    public string AiCredentialStatusDetailText => State.AiCredentialStatusDetailText;

    public string AiCredentialSaveButtonText => State.AiCredentialSaveButtonText;

    public string AiCredentialActionMessage => State.AiCredentialActionMessage;

    public bool IsAiCredentialBusy => State.IsAiCredentialBusy;

    public bool CanSaveAiCredential => State.CanSaveAiCredential;

    public bool CanRemoveAiCredential => State.CanRemoveAiCredential;

    public bool CanRefreshAiCredential => State.CanRefreshAiCredential;

    public void Initialize()
    {
        if (_aiCredentialManagement is null)
        {
            State.SetCredentialStatus(new(
                AiCredentialState.Unavailable,
                null,
                "AI_CREDENTIAL_MANAGEMENT_UNAVAILABLE"));
            State.SetCredentialActionMessage("本机凭据管理未接入；未发起 AI 请求。");
            return;
        }

        _ = RefreshAiCredentialStatusAsync();
    }

    public void SetAiCredentialInput(string? value) => State.SetCredentialInput(value);

    internal void ClearAiCredentialInput()
    {
        if (!State.ClearCredentialInput())
        {
            return;
        }

        AiCredentialInputCleared?.Invoke(this, EventArgs.Empty);
    }

    public async Task<bool> EnsureConfiguredForScanAsync()
    {
        // Offline fixtures intentionally omit the production credential manager.
        if (_aiCredentialManagement is null) return true;
        await RefreshAiCredentialStatusAsync().ConfigureAwait(true);
        return AiCredentialState == AiCredentialState.Configured;
    }

    private async Task RefreshAiCredentialStatusAsync()
    {
        if (_aiCredentialManagement is null || State.IsAiCredentialBusy)
        {
            return;
        }

        State.SetCredentialBusy(true);
        try
        {
            var status = await _aiCredentialManagement
                .GetStatusAsync(CancellationToken.None)
                .ConfigureAwait(true);
            State.SetCredentialStatus(status);
            State.SetCredentialActionMessage(status.State switch
            {
                AiCredentialState.Configured => "已读取本机凭据；仅显示脱敏尾号。",
                AiCredentialState.NotConfigured => "本机尚未保存 API Key；保存后才会允许新的 AI 请求。",
                _ => "本机凭据读取失败，未发起 AI 请求。"
            });
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            State.SetCredentialStatus(new(
                AiCredentialState.Unavailable,
                null,
                "AI_CREDENTIAL_READ_FAILED"));
            State.SetCredentialActionMessage("读取本机凭据失败，未发起 AI 请求。");
        }
        finally
        {
            State.SetCredentialBusy(false);
        }
    }

    private async Task SaveAiCredentialAsync()
    {
        if (!CanSaveAiCredential || _aiCredentialManagement is null)
        {
            return;
        }

        var input = State.CredentialInput;
        State.SetCredentialBusy(true);
        try
        {
            await _aiCredentialManagement.SaveAsync(input, CancellationToken.None).ConfigureAwait(true);
            ClearAiCredentialInput();
            var status = await _aiCredentialManagement
                .GetStatusAsync(CancellationToken.None)
                .ConfigureAwait(true);
            State.SetCredentialStatus(status);
            State.SetCredentialActionMessage(status.State == AiCredentialState.Configured
                ? "Key 已安全保存；保存动作没有调用 AI。"
                : "Key 已写入但状态复核未通过；未发起 AI 请求。");
        }
        catch (ArgumentException exception)
        {
            State.SetCredentialActionMessage(exception.Message);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            State.SetCredentialActionMessage("Key 保存失败，本机凭据未改变；未发起 AI 请求。");
        }
        finally
        {
            State.SetCredentialBusy(false);
        }
    }

    private async Task RemoveAiCredentialAsync()
    {
        if (!CanRemoveAiCredential || _aiCredentialManagement is null)
        {
            return;
        }

        State.SetCredentialBusy(true);
        try
        {
            await _aiCredentialManagement.RemoveAsync(CancellationToken.None).ConfigureAwait(true);
            ClearAiCredentialInput();
            State.SetCredentialStatus(new(AiCredentialState.NotConfigured, null));
            State.SetCredentialActionMessage("Key 已从本机 Windows 凭据存储移除；新的 AI 请求已阻止。");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            State.SetCredentialActionMessage("Key 移除失败，本机凭据未改变；未发起 AI 请求。");
        }
        finally
        {
            State.SetCredentialBusy(false);
        }
    }

    private void SetAiCredentialActionMessage(string message)
    {
        State.SetCredentialActionMessage(message);
    }

    private void State_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        PropertyChanged?.Invoke(this, e);
        switch (e.PropertyName)
        {
            case nameof(CanSaveAiCredential):
                ((AsyncRelayCommand)SaveAiCredentialCommand).RaiseCanExecuteChanged();
                break;
            case nameof(CanRemoveAiCredential):
                ((AsyncRelayCommand)RemoveAiCredentialCommand).RaiseCanExecuteChanged();
                break;
            case nameof(CanRefreshAiCredential):
                ((AsyncRelayCommand)RefreshAiCredentialCommand).RaiseCanExecuteChanged();
                break;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        ClearAiCredentialInput();
        State.PropertyChanged -= State_PropertyChanged;
        GC.SuppressFinalize(this);
    }
}
