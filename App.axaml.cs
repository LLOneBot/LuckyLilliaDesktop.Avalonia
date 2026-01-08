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

namespace LuckyLilliaDesktop;

public partial class App : Application
{
    public IServiceProvider Services { get; private set; } = null!;
    private NativeMenuItem? _trayShowMenuItem;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
        ConfigureServices();
    }

    private void ConfigureServices()
    {
        var services = new ServiceCollection();

        // 配置 Serilog
        var logger = new LoggerConfiguration()
            .WriteTo.Console(outputTemplate: "{Timestamp:HH:mm:ss} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
            .WriteTo.File("logs/app.log",
                rollingInterval: RollingInterval.Day,
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

        // ViewModels（瞬态）
        services.AddTransient<MainWindowViewModel>();
        services.AddTransient<HomeViewModel>();
        services.AddTransient<LogViewModel>();
        services.AddTransient<ConfigViewModel>();
        services.AddTransient<LLBotConfigViewModel>();
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

        base.OnFrameworkInitializationCompleted();
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
        // 停止所有进程
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

    private void Exit_Click(object? sender, EventArgs e)
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.Shutdown();
        }
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
