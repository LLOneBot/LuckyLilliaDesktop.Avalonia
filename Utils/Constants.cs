namespace LuckyLilliaDesktop.Utils;

/// <summary>
/// 应用程序常量定义
/// </summary>
public static class Constants
{
    /// <summary>
    /// NPM 包名映射
    /// </summary>
    public static class NpmPackages
    {
        public static string Pmhq => PlatformHelper.GetPlatformPackageName("pmhq-dist");
        public const string LLBot = "llonebot-dist";
        public static string Node => PlatformHelper.GetPlatformPackageName("llonebot-node");
        public static string FFmpeg => PlatformHelper.GetPlatformPackageName("llonebot-ffmpeg");
        public static string App => PlatformHelper.GetPlatformPackageName("lucky-lillia-desktop");
    }

    /// <summary>
    /// GitHub 仓库映射
    /// </summary>
    public static class GitHubRepos
    {
        public const string LLBot = "LLOneBot/LuckyLilliaBot";
        public const string App = "LLOneBot/LuckyLilliaDesktop.Avalonia";
        public const string Pmhq = "linyuchen/pmhq";
    }

    /// <summary>
    /// 默认路径
    /// </summary>
    public static class DefaultPaths
    {
        public const string PmhqDir = "bin/pmhq";
        public static string PmhqExe => PlatformHelper.GetExecutablePath("bin/pmhq", $"pmhq-{PlatformHelper.PlatformIdentifier}");
        public const string LLBotDir = "bin/llbot";
        public const string LLBotScript = "bin/llbot/llbot.js";
        public static string NodeExe => PlatformHelper.GetExecutablePath("bin/llbot", "node");
        public static string FFmpegExe => PlatformHelper.GetExecutablePath("bin/llbot", "ffmpeg");
        public static string FFprobeExe => PlatformHelper.GetExecutablePath("bin/llbot", "ffprobe");

        // QQ 相关路径
        public const string QQDir = "bin/qq";
        public static string QQExe => PlatformHelper.IsMacOS
            ? "bin/qq/QQ.app/Contents/MacOS/QQ"
            : PlatformHelper.GetExecutablePath("bin/qq", "QQ");
    }

    /// <summary>
    /// NPM 镜像源（下载优先）
    /// </summary>
    public static readonly string[] NpmDownloadMirrors =
    {
        "https://registry.npmmirror.com",
        "https://mirrors.huaweicloud.com/repository/npm",
        "https://mirrors.cloud.tencent.com/npm",
        "https://registry.npmjs.org"
    };

    /// <summary>
    /// NPM 官方源（版本检查优先）
    /// </summary>
    public static readonly string[] NpmVersionCheckMirrors =
    {
        "https://registry.npmjs.org",
        "https://registry.npmmirror.com",
        "https://mirrors.huaweicloud.com/repository/npm",
        "https://mirrors.cloud.tencent.com/npm"
    };

    /// <summary>
    /// QQ 下载地址
    /// </summary>
    public static string QQDownloadUrl => PlatformHelper.IsMacOS
        ? "https://github.com/LLOneBot/exe/releases/download/0.0.0/QQ-macos.zip"
        : "https://dldir1v6.qq.com/qqfile/qq/QQNT/c50d6326/QQ9.9.22.40768_x64.exe";

    /// <summary>
    /// 超时设置
    /// </summary>
    public static class Timeouts
    {
        public const int UpdateCheck = 10;
        public const int Download = 300;
    }
}
