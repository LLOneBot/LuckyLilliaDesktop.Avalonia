using Avalonia;
using System;
using System.Text;
using LuckyLilliaDesktop.Utils;

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

                        switch (string.IsNullOrEmpty(parentDir))
                        {
                            // 如果在 /Applications 目录，使用标准的 Application Support 目录
                            case false when parentDir == "/Applications":
                            {
                                var homeDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                                var appSupportDir = System.IO.Path.Combine(homeDir, "Library", "Application Support", "LuckyLilliaDesktop");

                                // 确保目录存在
                                if (!System.IO.Directory.Exists(appSupportDir))
                                {
                                    System.IO.Directory.CreateDirectory(appSupportDir);
                                }

                                Environment.CurrentDirectory = appSupportDir;
                                break;
                            }
                            case false when parentDir.Contains("/AppTranslocation/"):
                            {
                                // macOS App Translocation：尝试获取原始路径，去除隔离标记后重启
                                var translocatedAppPath = exeDir.Substring(0, exeDir.IndexOf(".app/Contents/MacOS")) + ".app";
                                var originalAppPath = Utils.AppTranslocationHelper.GetOriginalPath(translocatedAppPath);

                                if (!string.IsNullOrEmpty(originalAppPath) && originalAppPath != translocatedAppPath)
                                {
                                    // 去除隔离标记
                                    var xattr = new System.Diagnostics.Process();
                                    xattr.StartInfo.FileName = "/usr/bin/xattr";
                                    xattr.StartInfo.Arguments = $"-cr \"{originalAppPath}\"";
                                    xattr.StartInfo.UseShellExecute = false;
                                    xattr.StartInfo.CreateNoWindow = true;
                                    xattr.Start();
                                    xattr.WaitForExit();

                                    // 从原始路径重新启动
                                    System.Diagnostics.Process.Start("/usr/bin/open", $"\"{originalAppPath}\"");
                                    Environment.Exit(0);
                                    return;
                                }

                                // 获取原始路径失败时，回退到 Application Support
                                var homeDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                                var appSupportDir = System.IO.Path.Combine(homeDir, "Library", "Application Support", "LuckyLilliaDesktop");
                                if (!System.IO.Directory.Exists(appSupportDir))
                                    System.IO.Directory.CreateDirectory(appSupportDir);
                                Environment.CurrentDirectory = appSupportDir;
                                break;
                            }
                            case false when System.IO.Directory.Exists(parentDir):
                                // 其他位置：使用 .app 的父目录（自定义工作区）
                                Environment.CurrentDirectory = parentDir;
                                break;
                            default:
                                // 后备方案
                                Environment.CurrentDirectory = exeDir;
                                break;
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
            catch
            {
                // ignored
            }

            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            System.IO.File.WriteAllText("crash.log", $"{DateTime.Now}: {ex}");
            throw;
        }
    }

    private static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .With(new Win32PlatformOptions
            {
                // 优先使用硬件渲染，软件渲染作为兜底；软件渲染在拖动/动画/阴影场景下帧率很低。
                RenderingMode = RenderingPerformanceHelper.UseReducedMotion
                    ? [Win32RenderingMode.Software]
                    : [Win32RenderingMode.AngleEgl, Win32RenderingMode.Software]
            })
            .WithInterFont()
            .LogToTrace();
}
