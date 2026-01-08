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
        try
        {
            // 仅在有控制台时设置编码
            try
            {
                Console.OutputEncoding = Encoding.UTF8;
                Console.InputEncoding = Encoding.UTF8;
            }
            catch { }
            
            AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
            AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
            
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            System.IO.File.WriteAllText("crash.log", $"{DateTime.Now}: {ex}");
            throw;
        }
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
