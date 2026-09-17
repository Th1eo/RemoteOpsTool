using Microsoft.Extensions.DependencyInjection;
using RemoteOpsTool.Services;
using RemoteOpsTool.Services.Capability;
using RemoteOpsTool.Services.Interfaces;
using RemoteOpsTool.ViewModels;
using RemoteOpsTool.Views;
using System.Windows;

namespace RemoteOpsTool;

public partial class App : System.Windows.Application
{
    public static IServiceProvider Services { get; private set; } = null!;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Register once so every main window and child dialog is constrained to
        // the active monitor's work area in DPI-aware WPF DIPs.
        Helpers.WindowHelper.RegisterResponsiveSizing();

        Exit += (_, _) => LogService.FlushAndDispose();

        try
        {
            var services = new ServiceCollection();

            // Services
            services.AddSingleton<ISettingsService, SettingsService>();
            services.AddSingleton<ICredentialService, CredentialService>();
            services.AddSingleton<ILogService, LogService>();
            services.AddSingleton<ICacheService, CacheService>();
            services.AddSingleton<IPsExecService, PsExecService>();
            services.AddSingleton<ITaskSchedulerService, TaskSchedulerService>();
            services.AddSingleton<IToolSetupService, ToolSetupService>();
            services.AddSingleton<IDameWareService, DameWareService>();
            services.AddSingleton<IFileDiskService, FileDiskService>();
            services.AddSingleton<IDeviceService, DeviceService>();
            services.AddSingleton<IServiceManagerService, ServiceManagerService>();
            services.AddSingleton<IPrinterService, PrinterService>();
            services.AddSingleton<ISoftwareService, SoftwareService>();
            services.AddSingleton<IEnvVarService, EnvVarService>();
            services.AddSingleton<ISystemInfoService, SystemInfoService>();
            services.AddSingleton<INetworkService, NetworkService>();
            services.AddSingleton<ICapabilityService, CapabilityService>();

            // ViewModels
            services.AddSingleton<MainViewModel>();

            // MainWindow
            services.AddSingleton<MainWindow>();

            Services = services.BuildServiceProvider();

            // Load settings and credentials
            var settings = Services.GetRequiredService<ISettingsService>();
            var credentials = Services.GetRequiredService<ICredentialService>();
            await Task.WhenAll(settings.LoadAsync(), credentials.LoadAsync());

            // Enable file logging if debug mode is on
            var log = Services.GetRequiredService<ILogService>();
            log.FileLogEnabled = settings.Settings.DebugMode;

            // Show main window
            var mainWindow = Services.GetRequiredService<MainWindow>();
            mainWindow.Show();
            _ = CheckPsExecToolsInBackgroundAsync(settings, log);
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(
                $"Failed to start RemoteOpsTool:\n\n{ex}",
                "Startup Error",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Error);
        }
    }

    public static T GetService<T>() where T : notnull => Services.GetRequiredService<T>();

    private static async Task CheckPsExecToolsInBackgroundAsync(ISettingsService settings, ILogService log)
    {
        try
        {
            await Task.Yield();
            var toolSetup = Services.GetRequiredService<IToolSetupService>();
            log.Info("正在后台检查 PsExec 工具...");
            if (!toolSetup.IsPsToolsAvailable())
            {
                log.Info("PsExec 未找到，正在后台自动下载并配置...");
                var path = settings.Settings.PsToolsPath;
                if (string.IsNullOrEmpty(path))
                    path = Constants.AppConstants.DefaultToolsPath;
                await toolSetup.ConfigurePsToolsAsync(path);
            }
            else
            {
                log.Info("PsExec 工具已就绪。");
            }
        }
        catch (Exception ex)
        {
            log.Warn($"PsTools 后台检查/配置失败: {ex.Message}");
        }
    }
}
