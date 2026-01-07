using Avalonia;
using Avalonia.ReactiveUI;
using LuckyLilliaDesktop.Services;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Text;

namespace LuckyLilliaDesktop;

class Program
{
    private static App? _app;
    
    [STAThread]
    public static void Main(string[] args)
    {
        // 设置控制台编码为 UTF-8
        Console.OutputEncoding = Encoding.UTF8;
        Console.InputEncoding = Encoding.UTF8;
        
        // 注册进程退出事件（包括正常退出、Ctrl+C、异常等）
        AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
        Console.CancelKeyPress += OnCancelKeyPress;
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace()
            .UseReactiveUI()
            .AfterSetup(builder =>
            {
                _app = builder.Instance as App;
            });

    private static void OnProcessExit(object? sender, EventArgs e)
    {
        CleanupProcesses();
    }

    private static void OnCancelKeyPress(object? sender, ConsoleCancelEventArgs e)
    {
        CleanupProcesses();
    }

    private static void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        CleanupProcesses();
    }

    private static void CleanupProcesses()
    {
        try
        {
            var processManager = _app?.Services.GetService<IProcessManager>();
            processManager?.StopAllAsync().GetAwaiter().GetResult();
        }
        catch { }
    }
}
