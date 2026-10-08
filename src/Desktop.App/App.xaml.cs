using System.Configuration;
using System.Data;
using System.Diagnostics.CodeAnalysis;
using System.Windows;
using System.Security.Cryptography;
using System.Text;
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
    // No diagnostic destination exists until every argument has been validated.
    private DesktopDataOptions? _dataOptions;

    internal static (AppLaunchOptions LaunchOptions, DesktopDataOptions DataOptions) ResolveStartupOptions(
        IEnumerable<string> arguments, Func<DesktopDataOptions> defaultOptions)
    {
        var launchOptions = AppLaunchOptions.Parse(arguments);
        var options = launchOptions.DataRoot is null
            ? defaultOptions()
            : new DesktopDataOptions(launchOptions.DataRoot);
        return (launchOptions, options);
    }

    internal static string SingleInstanceIdentity(AppLaunchOptions options)
    {
        const string production = "EcommerceDetailPage.DesktopV1";
        var formalRoot = DesktopDataOptions.ForCurrentUser().RootDirectory;
        if (options.DataRoot is null || string.Equals(System.IO.Path.TrimEndingDirectorySeparator(options.DataRoot),
            System.IO.Path.TrimEndingDirectorySeparator(formalRoot), StringComparison.OrdinalIgnoreCase)) return production;
        // An explicit Debug root must not activate a window owning another database.
        // Default/Release single-instance behavior stays unchanged.
        return production + ".Isolated." +
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(options.DataRoot)).ToUpperInvariant())));
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var startupPhase = "launch_options";
        try
        {
            // Release parses too: unsupported isolation arguments fail before any
            // single-instance handoff, service/store construction or startup record.
            var (launchOptions, options) = ResolveStartupOptions(e.Args, DesktopDataOptions.ForCurrentUser);
            _dataOptions = options;

            startupPhase = "single_instance";
            _singleInstance = new SingleInstanceCoordinator(SingleInstanceIdentity(launchOptions));
            if (!await _singleInstance.TryAcquireAsync().ConfigureAwait(true))
            {
                Shutdown(0);
                return;
            }

            startupPhase = "service_registration";
            var services = new ServiceCollection();
            services.AddDesktopFoundation(options);
            services.AddSingleton(launchOptions);
            services.AddSingleton<IFolderPicker, WindowsFolderPicker>();
            services.AddSingleton<IBatchInputWorkflow, BatchInputWorkflowAdapter>();
            services.AddSingleton<MainWindowViewModel>();
            services.AddSingleton<MainWindow>();
            _services = services.BuildServiceProvider(validateScopes: true);

            startupPhase = "store_initialization";
            var store = _services.GetRequiredService<SqliteDesktopStore>();
            await store.InitializeAsync().ConfigureAwait(true);
            _storeReady = true;
            _instanceId = $"app-{Guid.NewGuid():N}";
            startupPhase = "instance_record";
            await _services.GetRequiredService<IAppInstanceStore>().RecordStartAsync(
                new AppInstanceRecord(_instanceId, DateTimeOffset.UtcNow, null, SafeExit: false),
                CancellationToken.None).ConfigureAwait(true);

            startupPhase = "main_window_creation";
            var window = _services.GetRequiredService<MainWindow>();
            MainWindow = window;
            _singleInstance.StartActivationListener(() => Dispatcher.Invoke(() => ActivateMainWindow(window)));
            ShutdownMode = ShutdownMode.OnMainWindowClose;
            startupPhase = "main_window_show";
            window.Show();
        }
        catch (Exception exception)
        {
            var diagnosticWritten = StartupDiagnostics.TryLogFailure(
                _dataOptions,
                "DESKTOP_STARTUP_FAILED",
                startupPhase,
                exception);
            MessageBox.Show(
                _dataOptions is null
                    ? "启动参数无效或当前构建不支持。未打开数据根，也未写入诊断日志。\n调试版隔离参数：--data-root=\"本地绝对目录\"；正式版请不带开发参数启动。"
                    : diagnosticWritten
                    ? $"应用启动失败（阶段：{startupPhase}）。\n诊断日志：{StartupDiagnostics.DisplayLogPath}"
                    : $"应用启动失败（阶段：{startupPhase}）。\n诊断日志写入失败，请保留此错误阶段。",
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
            try
            {
                if (_storeReady && _services is not null && _instanceId is not null)
                {
                    _services.GetRequiredService<IAppInstanceStore>()
                        .RecordSafeExitAsync(_instanceId, DateTimeOffset.UtcNow, CancellationToken.None)
                        .GetAwaiter()
                        .GetResult();
                }
            }
            catch (Exception exception)
            {
                StartupDiagnostics.TryLogFailure(
                    _dataOptions,
                    "DESKTOP_SAFE_EXIT_RECORD_FAILED",
                    "safe_exit_record",
                    exception);
            }

            try
            {
                _services?.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
            catch (Exception exception)
            {
                StartupDiagnostics.TryLogFailure(
                    _dataOptions,
                    "DESKTOP_SERVICE_DISPOSE_FAILED",
                    "service_dispose",
                    exception);
            }

            try
            {
                _singleInstance?.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
            catch (Exception exception)
            {
                StartupDiagnostics.TryLogFailure(
                    _dataOptions,
                    "DESKTOP_SINGLE_INSTANCE_DISPOSE_FAILED",
                    "single_instance_dispose",
                    exception);
            }
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
