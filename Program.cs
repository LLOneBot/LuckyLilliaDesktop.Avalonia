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
            {
                // macOS .app bundle 特殊处理
                if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.OSX))
                {
                    // 检查是否在 .app bundle 中运行
                    if (exeDir.Contains(".app/Contents/MacOS"))
                    {
                        // 从 /path/to/App.app/Contents/MacOS 提取 .app 所在目录
                        var appBundlePath = exeDir.Substring(0, exeDir.IndexOf(".app/Contents/MacOS"));
                        var parentDir = System.IO.Path.GetDirectoryName(appBundlePath);

                        // 如果在 /Applications 目录，使用标准的 Application Support 目录
                        if (!string.IsNullOrEmpty(parentDir) && parentDir == "/Applications")
                        {
                            var homeDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                            var appSupportDir = System.IO.Path.Combine(homeDir, "Library", "Application Support", "LuckyLilliaDesktop");

                            // 确保目录存在
                            if (!System.IO.Directory.Exists(appSupportDir))
                            {
                                System.IO.Directory.CreateDirectory(appSupportDir);
                            }

                            Environment.CurrentDirectory = appSupportDir;
                        }
                        else if (!string.IsNullOrEmpty(parentDir) && System.IO.Directory.Exists(parentDir))
                        {
                            // 其他位置：使用 .app 的父目录（自定义工作区）
                            Environment.CurrentDirectory = parentDir;
                        }
                        else
                        {
                            // 后备方案
                            Environment.CurrentDirectory = exeDir;
                        }
                    }
                    else
                    {
                        // 开发环境：使用 exe 所在目录
                        Environment.CurrentDirectory = exeDir;
                    }
                }
                else
                {
                    Environment.CurrentDirectory = exeDir;
                }
            }

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
