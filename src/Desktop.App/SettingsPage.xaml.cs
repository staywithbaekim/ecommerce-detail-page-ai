using System.Windows;
using System.Windows.Controls;

namespace Desktop.App;

public partial class SettingsPage : UserControl
{
    private AiCredentialViewModel? _credentials;

    public SettingsPage()
    {
        InitializeComponent();
        Loaded += (_, _) => ObserveCredentials((DataContext as SettingsPageViewModel)?.Credentials);
        Unloaded += (_, _) =>
        {
            ObserveCredentials(null);
            (DataContext as SettingsPageViewModel)?.CancelPhotoshopOperations();
        };
        IsVisibleChanged += (_, _) =>
        {
            if (!IsVisible)
                (DataContext as SettingsPageViewModel)?.CancelPhotoshopOperations();
        };
        DataContextChanged += (_, args) =>
        {
            (args.OldValue as SettingsPageViewModel)?.CancelPhotoshopOperations();
            if (IsLoaded) ObserveCredentials((DataContext as SettingsPageViewModel)?.Credentials);
        };
    }

    private void AiApiKeyInput_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (sender is PasswordBox passwordBox
            && DataContext is SettingsPageViewModel page)
        {
            page.SetAiCredentialInput(passwordBox.Password);
        }
    }

    private void ObserveCredentials(AiCredentialViewModel? credentials)
    {
        if (ReferenceEquals(_credentials, credentials)) return;
        if (_credentials is not null) _credentials.AiCredentialInputCleared -= Credentials_InputCleared;
        _credentials = credentials;
        if (_credentials is not null) _credentials.AiCredentialInputCleared += Credentials_InputCleared;
    }

    private void Credentials_InputCleared(object? sender, EventArgs e) => AiApiKeyInputBox.Clear();
}
