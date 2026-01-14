using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using LuckyLilliaDesktop.ViewModels;
using LuckyLilliaDesktop.Views;
using LuckyLilliaDesktop.Services;
using LuckyLilliaDesktop.Models;
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
        
        // 设置为全局 logger
        Log.Logger = logger;

        // 核心服务（单例）
        services.AddSingleton<IConfigManager, ConfigManager>();
        services.AddSingleton<ILogCollector, LogCollector>();
        services.AddSingleton<IProcessManager, ProcessManager>();
        services.AddSingleton<IResourceMonitor, ResourceMonitor>();
        services.AddSingleton<ISelfInfoService, SelfInfoService>();
        services.AddSingleton<IPmhqClient, PmhqClient>();
        services.AddSingleton<IDownloadService, DownloadService>();
        services.AddSingleton<IUpdateChecker, UpdateChecker>();
        services.AddSingleton<IUpdateStateService, UpdateStateService>();
        services.AddSingleton<IGitHubHelper, GitHubHelper>();
        services.AddSingleton<IPythonHelper, PythonHelper>();
        services.AddSingleton<IKoishiInstallService, KoishiInstallService>();
        services.AddSingleton<IAstrBotInstallService, AstrBotInstallService>();
        services.AddSingleton<IZhenxunInstallService, ZhenxunInstallService>();

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

    private void OnShutdownRequested(object? sender, ShutdownRequestedEventArgs e)
    {
        ExitApplicationAsync();
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
    public Task ExitApplicationAsync()
    {
        Log.Information("ExitApplicationAsync 被调用");
        
        try
        {
            var resourceMonitor = Services.GetService<IResourceMonitor>();
            var pmhqClient = Services.GetService<IPmhqClient>();
            var qqPid = resourceMonitor?.QQPid;
            
            // 如果缓存没有 PID，尝试从 API 获取（在新线程中）
            if (!qqPid.HasValue && pmhqClient?.HasPort == true)
            {
                try
                {
                    var task = Task.Run(() => pmhqClient.FetchQQPidAsync());
                    if (task.Wait(1000))
                    {
                        qqPid = task.Result;
                    }
                }
                catch { }
            }
            
            Log.Information("QQ PID: {Pid}", qqPid);
            
            // 终止 QQ 进程
            if (qqPid.HasValue && qqPid.Value > 0)
            {
                Log.Information("正在终止 QQ 进程, PID: {Pid}", qqPid.Value);
                try
                {
                    var qqProc = System.Diagnostics.Process.GetProcessById(qqPid.Value);
                    qqProc.Kill(entireProcessTree: true);
                    qqProc.Dispose();
                    Log.Information("QQ 进程已终止");
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "终止 QQ 进程失败");
                }
            }
            
            // 终止 LLBot 进程
            var processManager = Services.GetService<IProcessManager>();
            if (processManager != null)
            {
                try
                {
                    var llbotStatus = processManager.GetProcessStatus("LLBot");
                    if (llbotStatus == ProcessStatus.Running)
                    {
                        // 通过反射获取 _llbotProcess 的 PID
                        var field = processManager.GetType().GetField("_llbotProcess", 
                            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                        if (field?.GetValue(processManager) is System.Diagnostics.Process llbotProc && !llbotProc.HasExited)
                        {
                            var llbotPid = llbotProc.Id;
                            Log.Information("正在终止 LLBot 进程, PID: {Pid}", llbotPid);
                            
                            var proc = System.Diagnostics.Process.GetProcessById(llbotPid);
                            proc.Kill(entireProcessTree: true);
                            proc.Dispose();
                            Log.Information("LLBot 进程已终止");
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "终止 LLBot 进程失败");
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "清理进程失败");
        }
        
        Log.Information("应用退出");
        Environment.Exit(0);
        return Task.CompletedTask;
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
