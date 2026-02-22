using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace LuckyLilliaDesktop.Utils;

/// <summary>
/// 平台检测和路径管理工具类
/// </summary>
public static class PlatformHelper
{
    /// <summary>
    /// 判断当前是否为Windows平台
    /// </summary>
    [SupportedOSPlatformGuard("windows")]
    public static bool IsWindows => OperatingSystem.IsWindows();

    /// <summary>
    /// 判断当前是否为macOS平台
    /// </summary>
    [SupportedOSPlatformGuard("macos")]
    public static bool IsMacOS => OperatingSystem.IsMacOS();

    /// <summary>
    /// 判断当前是否为Linux平台
    /// </summary>
    [SupportedOSPlatformGuard("linux")]
    public static bool IsLinux => OperatingSystem.IsLinux();

    /// <summary>
    /// 获取当前平台的可执行文件扩展名
    /// Windows: ".exe", macOS/Linux: ""
    /// </summary>
    public static string ExecutableExtension => IsWindows ? ".exe" : "";

    /// <summary>
    /// 获取当前平台的标识符（用于下载对应的二进制文件）
    /// </summary>
    public static string PlatformIdentifier
    {
        get
        {
            if (IsWindows)
            {
                return RuntimeInformation.ProcessArchitecture == Architecture.X64 ? "win-x64" : "win-arm64";
            }

            if (IsMacOS)
            {
                return RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "macos-arm64" : "macos-x64";
            }

            if (IsLinux)
            {
                return RuntimeInformation.ProcessArchitecture == Architecture.X64 ? "linux-x64" : "linux-arm64";
            }

            return "unknown";
        }
    }

    /// <summary>
    /// 根据基础路径和文件名获取完整的可执行文件路径
    /// </summary>
    /// <param name="basePath">基础路径（如 "bin/llbot"）</param>
    /// <param name="fileName">文件名（不含扩展名，如 "node"）</param>
    /// <returns>完整路径（如 Windows: "bin/llbot/node.exe", macOS: "bin/llbot/node"）</returns>
    public static string GetExecutablePath(string basePath, string fileName)
    {
        return Path.Combine(basePath, fileName + ExecutableExtension);
    }

    /// <summary>
    /// 获取平台特定的NPM包名
    /// </summary>
    /// <param name="packageBaseName">包基础名称（如 "pmhq-dist"）</param>
    /// <returns>完整包名（如 "pmhq-dist-win-x64" 或 "pmhq-dist-osx-x64"）</returns>
    public static string GetPlatformPackageName(string packageBaseName)
    {
        return $"{packageBaseName}-{PlatformIdentifier}";
    }
}
