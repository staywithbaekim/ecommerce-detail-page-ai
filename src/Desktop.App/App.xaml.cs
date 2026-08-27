using System.Configuration;
using System.Data;
using System.Diagnostics.CodeAnalysis;
using System.Windows;
using Desktop.Application.Diagnostics;
using Desktop.Infrastructure;
using Desktop.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace Desktop.App;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
[SuppressMessage("Design", "CA1001", Justification = "WPF owns the Application lifecycle; OnExit disposes the single-instance coordinator and service provider.")]
public partial class App : System.Windows.Application
{
    private ServiceProvider? _services;
    private SingleInstanceCoordinator? _singleInstance;
    private string? _instanceId;
    private bool _storeReady;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _singleInstance = new SingleInstanceCoordinator("EcommerceDetailPage.DesktopV1");
        if (!await _singleInstance.TryAcquireAsync().ConfigureAwait(true))
        {
            Shutdown(0);
            return;
        }

        try
        {
#if DEBUG
            var launchOptions = AppLaunchOptions.Parse(e.Args);
            var options = string.IsNullOrWhiteSpace(launchOptions.DataRoot)
                ? DesktopDataOptions.ForCurrentUser()
                : new DesktopDataOptions(launchOptions.DataRoot);
#else
            var launchOptions = AppLaunchOptions.Default;
            var options = DesktopDataOptions.ForCurrentUser();
#endif

            var services = new ServiceCollection();
            services.AddDesktopFoundation(options);
            services.AddSingleton(launchOptions);
            services.AddSingleton<MainWindowViewModel>();
            services.AddSingleton<MainWindow>();
            _services = services.BuildServiceProvider(validateScopes: true);

            var store = _services.GetRequiredService<SqliteDesktopStore>();
            await store.InitializeAsync().ConfigureAwait(true);
            _storeReady = true;
            _instanceId = $"app-{Guid.NewGuid():N}";
            await _services.GetRequiredService<IAppInstanceStore>().RecordStartAsync(
                new AppInstanceRecord(_instanceId, DateTimeOffset.UtcNow, null, SafeExit: false),
                CancellationToken.None).ConfigureAwait(true);

            var window = _services.GetRequiredService<MainWindow>();
            MainWindow = window;
            _singleInstance.StartActivationListener(() => Dispatcher.Invoke(() => ActivateMainWindow(window)));
            ShutdownMode = ShutdownMode.OnMainWindowClose;
            window.Show();
        }
        catch (Exception)
        {
            MessageBox.Show(
                "应用启动失败。请查看本地诊断日志。",
                "电商详情页智能生产系统",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            if (_storeReady && _services is not null && _instanceId is not null)
            {
                _services.GetRequiredService<IAppInstanceStore>()
                    .RecordSafeExitAsync(_instanceId, DateTimeOffset.UtcNow, CancellationToken.None)
                    .GetAwaiter()
                    .GetResult();
            }

            _services?.DisposeAsync().AsTask().GetAwaiter().GetResult();
            _singleInstance?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        finally
        {
            base.OnExit(e);
        }
    }

    private static void ActivateMainWindow(Window window)
    {
        if (window.WindowState == WindowState.Minimized)
        {
            window.WindowState = WindowState.Normal;
        }

        window.Show();
        window.Activate();
        window.Topmost = true;
        window.Topmost = false;
        window.Focus();
    }
}
