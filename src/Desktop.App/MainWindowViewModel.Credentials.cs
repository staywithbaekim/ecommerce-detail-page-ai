using Desktop.Application.Ai;

namespace Desktop.App;

public sealed partial class MainWindowViewModel : ISettingsPageHost
{
    private AiCredentialViewModel Credentials => SettingsPageViewModel.Credentials;
    private async Task<bool> EnsureAiCredentialConfiguredForScanAsync()
    {
        if (await Credentials.EnsureConfiguredForScanAsync().ConfigureAwait(true)) return true;
        SelectedNavigationItem = NavigationItems.First(item => item.Key == "settings");
        BatchStatusMessage = Credentials.AiCredentialState == AiCredentialState.NotConfigured
            ? "请先在设置 → AI 服务中保存 API Key；保存不会自动调用 AI。"
            : "本机凭据读取失败，已阻止 AI 请求；请在设置中重新读取或更换 Key。";
        return false;
    }
}
