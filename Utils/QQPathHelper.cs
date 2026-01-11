using Microsoft.Win32;
using System;
using System.IO;
using System.Runtime.Versioning;

namespace LuckyLilliaDesktop.Utils;

public static class QQPathHelper
{
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
