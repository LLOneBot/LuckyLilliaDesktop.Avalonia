using Microsoft.Win32;
using System;
using System.IO;
using System.Runtime.Versioning;

namespace LuckyLilliaDesktop.Utils;

public static class QQPathHelper
{
    /// <summary>
    /// 获取默认的QQ路径（跨平台）
    /// </summary>
    public static string? GetDefaultQQPath()
    {
        if (PlatformHelper.IsWindows)
        {
            return GetQQPathFromRegistry();
        }

        if (PlatformHelper.IsMacOS)
        {
            return GetMacOSQQPath();
        }

        return null;
    }

    /// <summary>
    /// 获取macOS上的QQ路径
    /// </summary>
    public static string? GetMacOSQQPath()
    {
        // 默认安装路径
        var defaultPath = "bin/qq/QQ.app/Contents/MacOS/QQ";
        if (File.Exists(defaultPath))
        {
            return Path.GetFullPath(defaultPath);
        }

        return null;
    }

    [SupportedOSPlatform("windows")]
    public static string? GetQQPathFromRegistry()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\QQ");

            if (key == null) return null;

            var uninstallPath = key.GetValue("UninstallString") as string;
            if (string.IsNullOrEmpty(uninstallPath)) return null;

            if (uninstallPath.StartsWith("\"") && uninstallPath.EndsWith("\""))
            {
                uninstallPath = uninstallPath[1..^1];
            }

            var qqDir = Path.GetDirectoryName(uninstallPath);
            if (string.IsNullOrEmpty(qqDir)) return null;

            var qqExePath = Path.Combine(qqDir, "QQ.exe");
            return File.Exists(qqExePath) ? qqExePath : null;
        }
        catch
        {
            return null;
        }
    }
}
