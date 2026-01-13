using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using LuckyLilliaDesktop.ViewModels;
using LuckyLilliaDesktop.Views;
using LuckyLilliaDesktop.Services;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using System;
using System.Threading.Tasks;

namespace LuckyLilliaDesktop;

public partial class App : Application
{
    public IServiceProvider Services { get; private set; } = null!;
    private NativeMenuItem? _trayShowMenuItem;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
        ConfigureServices();
        Log.Information("应用初始化完成");
    }

    private void ConfigureServices()
    {
        var services = new ServiceCollection();

        // 配置 Serilog - 每次启动创建新日志文件
        var logFileName = $"logs/{DateTime.Now:yyyyMMdd_HHmmss}.log";
        var logger = new LoggerConfiguration()
            .WriteTo.Console(outputTemplate: "{Timestamp:HH:mm:ss} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
            .WriteTo.File(logFileName,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
            .CreateLogger();

        // 核心服务（单例）
        services.AddSingleton<IConfigManager, ConfigManager>();
        services.AddSingleton<ILogCollector, LogCollector>();
        services.AddSingleton<IProcessManager, ProcessManager>();
        services.AddSingleton<IResourceMonitor, ResourceMonitor>();
        services.AddSingleton<IPmhqClient, PmhqClient>();
        services.AddSingleton<IDownloadService, DownloadService>();
        services.AddSingleton<IUpdateChecker, UpdateChecker>();
        services.AddSingleton<IUpdateStateService, UpdateStateService>();
        services.AddSingleton<IKoishiInstallService, KoishiInstallService>();

        // ViewModels（瞬态）
        services.AddTransient<MainWindowViewModel>();
        services.AddTransient<HomeViewModel>();
        services.AddTransient<LogViewModel>();
        services.AddTransient<ConfigViewModel>();
        services.AddTransient<LLBotConfigViewModel>();
        services.AddTransient<IntegrationWizardViewModel>();
        services.AddTransient<AboutViewModel>();

        // 日志
        services.AddLogging(builder =>
        {
            builder.AddSerilog(logger, dispose: true);
        });

        Services = services.BuildServiceProvider();
    }

    public override void OnFrameworkInitializationCompleted()
    {
        try
        {
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                var mainVM = Services.GetRequiredService<MainWindowViewModel>();
                desktop.MainWindow = new MainWindow
                {
                    DataContext = mainVM
                };

                desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                desktop.ShutdownRequested += OnShutdownRequested;

                // 获取托盘菜单项引用
                var trayIcons = TrayIcon.GetIcons(this);
                if (trayIcons?.Count > 0)
                {
                    var menu = trayIcons[0].Menu;
                    if (menu?.Items.Count > 0)
                    {
                        _trayShowMenuItem = menu.Items[0] as NativeMenuItem;
                    }
                }
            }

            Log.Information("框架初始化完成，主窗口已创建");
            base.OnFrameworkInitializationCompleted();
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "框架初始化失败");
            System.IO.File.WriteAllText("startup_error.log", $"{DateTime.Now}: {ex}");
            throw;
        }
    }

    public void UpdateTrayMenuText(string? nickname, string? uin)
    {
        if (_trayShowMenuItem == null) return;

        var trayIcons = TrayIcon.GetIcons(this);
        if (!string.IsNullOrEmpty(nickname) && !string.IsNullOrEmpty(uin))
        {
            _trayShowMenuItem.Header = $"LLBot - {nickname}({uin})";
            if (trayIcons?.Count > 0)
            {
                trayIcons[0].ToolTipText = $"{nickname}({uin})";
            }
            Log.Information("托盘菜单已更新: {Nickname}({Uin})", nickname, uin);
        }
        else
        {
            _trayShowMenuItem.Header = "显示主窗口";
            if (trayIcons?.Count > 0)
            {
                trayIcons[0].ToolTipText = "LLBot";
            }
        }
    }

    private async void OnShutdownRequested(object? sender, ShutdownRequestedEventArgs e)
    {
        var processManager = Services.GetService<IProcessManager>();
        if (processManager != null)
        {
            await processManager.StopAllAsync();
        }
    }

    private void TrayIcon_Clicked(object? sender, EventArgs e)
    {
        ShowMainWindow();
    }

    private void ShowWindow_Click(object? sender, EventArgs e)
    {
        ShowMainWindow();
    }

    private async void Exit_Click(object? sender, EventArgs e)
    {
        await ExitApplicationAsync();
    }

    /// <summary>
    /// 统一的应用退出方法
    /// </summary>
    public async Task ExitApplicationAsync()
    {
        Log.Information("开始退出应用...");
        if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop) return;

        // 保存窗口位置
        if (desktop.MainWindow is MainWindow mainWindow)
        {
            await mainWindow.SaveWindowStateAsync();
            Log.Information("窗口状态已保存");
        }

        // 停止所有进程
        var processManager = Services.GetService<IProcessManager>();
        if (processManager != null)
        {
            await processManager.StopAllAsync();
            Log.Information("所有进程已停止");
        }

        Log.Information("应用退出");
        desktop.Shutdown();
    }

    private void ShowMainWindow()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var mainWindow = desktop.MainWindow;
            if (mainWindow != null)
            {
                mainWindow.Show();
                mainWindow.WindowState = WindowState.Normal;
                mainWindow.Activate();
            }
        }
    }
}
