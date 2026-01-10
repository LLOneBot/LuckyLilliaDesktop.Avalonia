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
        public const string Pmhq = "pmhq-dist-win-x64";
        public const string LLBot = "llonebot-dist";
        public const string Node = "llonebot-node-win-x64";
        public const string FFmpeg = "llonebot-ffmpeg-win-x64";
        public const string App = "lucky-lillia-desktop-win-x64";
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
        public const string PmhqExe = "bin/pmhq/pmhq-win-x64.exe";
        public const string LLBotDir = "bin/llbot";
        public const string LLBotScript = "bin/llbot/llbot.js";
        public const string NodeExe = "bin/llbot/node.exe";
        public const string FFmpegExe = "bin/llbot/ffmpeg.exe";
        public const string FFprobeExe = "bin/llbot/ffprobe.exe";
    }

    /// <summary>
    /// NPM 镜像源
    /// </summary>
    public static readonly string[] NpmRegistryMirrors =
    {
        "https://registry.npmmirror.com",
        "https://mirrors.huaweicloud.com/repository/npm",
        "https://mirrors.cloud.tencent.com/npm",
        "https://registry.npmjs.org"
    };

    /// <summary>
    /// QQ 下载地址
    /// </summary>
    public const string QQDownloadUrl = "https://dldir1v6.qq.com/qqfile/qq/QQNT/c50d6326/QQ9.9.22.40768_x64.exe";

    /// <summary>
    /// 超时设置
    /// </summary>
    public static class Timeouts
    {
        public const int UpdateCheck = 10;
        public const int Download = 300;
    }
}
