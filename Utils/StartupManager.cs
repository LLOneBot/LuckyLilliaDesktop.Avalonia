using Microsoft.Win32;
using System;
using System.Diagnostics;

namespace LuckyLilliaDesktop.Utils;

public static class StartupManager
{
    private const string TaskName = "LuckyLilliaDesktop";
    private const string LegacyRegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static bool IsStartupEnabled()
    {
        try
        {
            var result = RunSchtasks($"/Query /TN \"{TaskName}\" /FO CSV /NH");
            return result.ExitCode == 0 && result.Output.Contains(TaskName);
        }
        catch
        {
            return false;
        }
    }

    public static bool EnableStartup()
    {
        try
        {
            var exePath = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exePath) || !System.IO.File.Exists(exePath))
                return false;

            var workingDir = System.IO.Path.GetDirectoryName(exePath) ?? "";

            RunSchtasks($"/Delete /TN \"{TaskName}\" /F");
            CleanupLegacyRegistry();

            var args = $"/Create /TN \"{TaskName}\" " +
                       $"/TR \"'{exePath}'\" " +
                       $"/SC ONLOGON " +
                       $"/RL HIGHEST " +
                       $"/F " +
                       $"/DELAY 0000:05";

            var result = RunSchtasks(args);
            if (result.ExitCode != 0) return false;

            var psArgs = $"-NoProfile -Command \"$task = Get-ScheduledTask -TaskName '{TaskName}'; " +
                         $"$task.Actions[0].WorkingDirectory = '{workingDir.Replace("'", "''")}'; " +
                         $"Set-ScheduledTask -InputObject $task\"";

            var psResult = RunProcess("powershell.exe", psArgs);
            return psResult.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    public static bool DisableStartup()
    {
        try
        {
            CleanupLegacyRegistry();
            RunSchtasks($"/Delete /TN \"{TaskName}\" /F");
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 启动时调用：如果检测到旧版注册表自启项，自动迁移到 Task Scheduler
    /// </summary>
    public static void MigrateFromLegacyRegistry()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(LegacyRegistryPath, true);
            if (key?.GetValue(TaskName) == null) return;

            if (!IsStartupEnabled())
                EnableStartup();

            key.DeleteValue(TaskName, false);
        }
        catch { }
    }

    private static void CleanupLegacyRegistry()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(LegacyRegistryPath, true);
            key?.DeleteValue(TaskName, false);
        }
        catch { }
    }

    private static ProcessResult RunSchtasks(string arguments)
    {
        return RunProcess("schtasks.exe", arguments);
    }

    private static ProcessResult RunProcess(string fileName, string arguments)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        process.Start();
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit(10000);
        return new ProcessResult(process.ExitCode, output, error);
    }

    private record ProcessResult(int ExitCode, string Output, string Error);
}
