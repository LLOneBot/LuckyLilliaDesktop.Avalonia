using Avalonia;
using Avalonia.ReactiveUI;
using System;
using System.Text;

namespace LuckyLilliaDesktop;

class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        try
        {
            // 开机自启时工作目录为 System32，需要切换到 exe 所在目录
            var exeDir = AppContext.BaseDirectory;
            if (!string.IsNullOrEmpty(exeDir))
                Environment.CurrentDirectory = exeDir;

            try
            {
                Console.OutputEncoding = Encoding.UTF8;
                Console.InputEncoding = Encoding.UTF8;
            }
            catch { }

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
            .With(new Win32PlatformOptions
            {
                // 使用软件渲染，避免低配机器GPU加速导致高CPU占用
                RenderingMode = new[] { Win32RenderingMode.Software }
            })
            .WithInterFont()
            .LogToTrace()
            .UseReactiveUI();
}
