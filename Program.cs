using Avalonia;
using System;
using System.Text;
using System.Linq;
using System.Threading;
using LuckyLilliaDesktop.Utils;

namespace LuckyLilliaDesktop;

class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        try
        {
            // 统一进程名为品牌名 LuckyLillia.exe: 若以某些分发名启动, 改名后以新名字重启。
            // 命中才改、改成固定品牌名, 非随机、非伪装系统进程; 任何失败都按原名继续, 不阻断启动。
            MaybeNormalizeProcessName(args);

            args = ApplyStartupDelay(args);

            // Windows AppCompat 可能给本 exe 打上 __COMPAT_LAYER (如 DetectorsAppHealth),
            // 该变量会被子进程继承。QQ 一旦继承, apphelp 的 GetProcAddress 挂钩
            // (SE_GetProcAddressForCaller) 会在 PMHQ 手动注入的 pmhq.dll (未在 PEB 登记,
            // 无模块名) 调用 GetProcAddress 时对 NULL 模块名执行 _wcsicmp, 读空指针 ->
            // QQ 崩溃 (0xC0000005)。启动即清除, 让 PMHQ/QQ/LLBot 继承干净的环境。
            Environment.SetEnvironmentVariable("__COMPAT_LAYER", null);

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

            // 工作目录已定, 现在才能读 app_settings.json 里的 dev
            DevMode.Initialize();

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

    // 归一化后的目标进程名 (品牌名)。
    private const string BrandExeName = "LuckyLillia.exe";

    // 命中即改名重启: 文件名(忽略大小写)包含任一关键字, 或完整等于某个分发名。
    // 关键字用 Contains, 覆盖 llbot.exe / LLBot.exe / llbot-x64.exe 等一切带 llbot 的名字。
    // 目标名 LuckyLillia.exe 与开发名 LuckyLilliaDesktop.exe 都不含 llbot, 不会被改 (也防重启死循环)。
    private static readonly string[] NormalizeNameKeywords = { "llbot" };
    private static readonly string[] NormalizeExactNames = { "lucky-lillia-desktop.exe" };

    /// <summary>
    /// Windows 下把进程名统一成 <see cref="BrandExeName"/>: 运行中的 exe 允许 rename (不允许
    /// delete), 同卷改名是元数据操作、不中断当前执行; 改完以新名字重启、原进程退出, 任务管理器/
    /// 自启项即显示品牌名。仅在当前名命中关键字或分发名时动作 (该判断同时是
    /// 防重启死循环的闸), 任何一步失败都静默回退、按原名正常启动。
    /// </summary>
    private static void MaybeNormalizeProcessName(string[] args)
    {
        if (!OperatingSystem.IsWindows()) return;

        string exePath, dir, curName;
        try
        {
            exePath = Environment.ProcessPath ?? string.Empty;
            if (string.IsNullOrEmpty(exePath)) return;
            dir = System.IO.Path.GetDirectoryName(exePath) ?? string.Empty;
            curName = System.IO.Path.GetFileName(exePath);
            if (string.IsNullOrEmpty(dir)) return;
        }
        catch
        {
            return;
        }

        var matched =
            NormalizeNameKeywords.Any(k => curName.Contains(k, StringComparison.OrdinalIgnoreCase))
            || NormalizeExactNames.Contains(curName, StringComparer.OrdinalIgnoreCase);
        if (!matched) return;

        var targetPath = System.IO.Path.Combine(dir, BrandExeName);

        try
        {
            // 目标已存在 (上次改名残留): 未被占用则删; 被占用 (多半另一实例在跑) 则放弃改名。
            if (System.IO.File.Exists(targetPath))
            {
                try { System.IO.File.Delete(targetPath); }
                catch { return; }
            }

            System.IO.File.Move(exePath, targetPath);
        }
        catch
        {
            // 无写权限 (如装在 Program Files 且非管理员) 等: 不改名, 原名继续。
            return;
        }

        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = targetPath,
                UseShellExecute = false,
                WorkingDirectory = dir,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);

            System.Diagnostics.Process.Start(psi);
            Environment.Exit(0);
        }
        catch
        {
            // 重启失败: 文件已是品牌名, 下次启动即生效; 本次仍以当前进程正常启动, 不阻断。
        }
    }

    private static string[] ApplyStartupDelay(string[] args)
    {
        if (!OperatingSystem.IsWindows() ||
            !args.Contains(StartupManager.StartupDelayArgument, StringComparer.Ordinal))
        {
            return args;
        }

        Thread.Sleep(TimeSpan.FromSeconds(5));
        return args
            .Where(argument => !string.Equals(
                argument,
                StartupManager.StartupDelayArgument,
                StringComparison.Ordinal))
            .ToArray();
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
