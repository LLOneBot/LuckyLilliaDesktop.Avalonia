using System.IO;
using System.Text.Json;
using LuckyLilliaDesktop.Models;

namespace LuckyLilliaDesktop.Utils;

/// <summary>
/// 开发模式。只由 app_settings.json 的 <c>dev</c> 开启, 没有对应的命令行开关。
///
/// 作用: (1) 放宽 auth_token 的联网校验 —— 启动时不再向 manager-server 请求
/// /api/sign/info, 手动输入 token 时也不校验; token 本身仍照常读写 data/auth_token.txt
/// 并传给 PMHQ (--auth-token) / LLBot。(2) 启动 LLBot 时透传 <c>--dev</c>。
/// 系统时间、版本门槛等其他启动检查不受影响。
/// </summary>
internal static class DevMode
{
    /// <summary>透传给 LLBot 的参数。Desktop 自己不解析命令行。</summary>
    internal const string Argument = "--dev";

    private const string ConfigPath = "app_settings.json";

    public static bool IsEnabled { get; private set; }

    /// <summary>
    /// 必须在工作目录切换之后、Avalonia 启动之前调用: ConfigPath 是相对路径, 而读 IsEnabled
    /// 的那几处 (auth_token 校验、LLBot 启动参数) 都在 Avalonia 起来之后。
    /// 这里自己同步读一次而不走 ConfigManager —— DI 容器要等 App.Initialize 才建好。
    /// </summary>
    public static void Initialize()
    {
        IsEnabled = ReadConfigDev();
    }

    private static bool ReadConfigDev()
    {
        try
        {
            if (!File.Exists(ConfigPath)) return false;
            var config = JsonSerializer.Deserialize(File.ReadAllText(ConfigPath), AppJsonContext.Default.AppConfig);
            return config?.Dev ?? false;
        }
        catch
        {
            // 配置坏了不该拦住启动: ConfigManager 随后会走它自己的 fallback 并把错误记进日志
            return false;
        }
    }
}
