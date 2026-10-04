using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace LuckyLilliaDesktop.Utils;

/// <summary>
/// 仅用于兼容旧版本创建的登录计划任务。
/// 新版本不会再创建计划任务，新的开机自启仍只使用 HKCU Run。
/// </summary>
internal static class LegacyScheduledTaskManager
{
    private const string TaskName = "LuckyLilliaDesktop";
    private const int ProcessTimeoutMilliseconds = 8_000;
    private const int OutputCleanupTimeoutMilliseconds = 2_000;
    private const int HResultFileNotFound = unchecked((int)0x80070002);
    private const int HResultPathNotFound = unchecked((int)0x80070003);
    private const int MaxDiagnosticLength = 1_500;

    private static readonly string[] KnownExecutableNames =
        ["LuckyLilliaDesktop.exe", "lucky-lillia-desktop.exe", "LuckyLillia.exe"];

    [SupportedOSPlatform("windows")]
    public static LegacyScheduledTaskInspection Inspect(string currentExecutablePath)
    {
        var query = RunSchtasks("/Query", "/TN", TaskName, "/XML", "/HResult");
        if (query.TimedOut)
        {
            return LegacyScheduledTaskInspection.Unavailable(
                "查询旧版开机自启计划任务超时。");
        }

        if (query.ExitCode is 2 or 3 or HResultFileNotFound or HResultPathNotFound)
            return LegacyScheduledTaskInspection.Missing();

        if (query.ExitCode != 0)
        {
            return LegacyScheduledTaskInspection.Unavailable(
                BuildFailureMessage("查询旧版开机自启计划任务失败", query));
        }

        try
        {
            var document = XDocument.Parse(query.Output.TrimStart('\uFEFF'));
            var commands = document
                .Descendants()
                .Where(element => element.Name.LocalName == "Command")
                .Select(element => element.Value)
                .Where(command => !string.IsNullOrWhiteSpace(command));

            return commands.Any(command => IsApplicationCommand(command, currentExecutablePath))
                ? LegacyScheduledTaskInspection.Owned()
                : LegacyScheduledTaskInspection.NotOwned();
        }
        catch (Exception ex)
        {
            return LegacyScheduledTaskInspection.Unavailable(
                $"解析旧版开机自启计划任务失败：{ex.Message}");
        }
    }

    [SupportedOSPlatform("windows")]
    public static StartupOperationResult TryDeleteOwnedTask(string currentExecutablePath)
    {
        var inspection = Inspect(currentExecutablePath);
        switch (inspection.State)
        {
            case LegacyScheduledTaskState.Missing:
            case LegacyScheduledTaskState.NotOwned:
                return StartupOperationResult.Succeeded();
            case LegacyScheduledTaskState.Unavailable:
                return StartupOperationResult.Failed(inspection.ErrorMessage);
        }

        var deletion = RunSchtasks("/Delete", "/TN", TaskName, "/F");
        var verification = Inspect(currentExecutablePath);
        if (verification.State == LegacyScheduledTaskState.Missing)
            return StartupOperationResult.Succeeded();

        if (deletion.TimedOut)
            return StartupOperationResult.Failed("删除旧版开机自启计划任务超时。");

        if (deletion.ExitCode != 0)
        {
            return StartupOperationResult.Failed(
                BuildFailureMessage("删除旧版开机自启计划任务失败", deletion));
        }

        return verification.State switch
        {
            LegacyScheduledTaskState.NotOwned => StartupOperationResult.Failed(
                "旧版开机自启计划任务在删除期间被其他程序修改，为避免继续操作已停止清理。"),
            LegacyScheduledTaskState.Unavailable => StartupOperationResult.Failed(
                $"删除旧版开机自启计划任务后无法完成校验：{verification.ErrorMessage}"),
            _ => StartupOperationResult.Failed("删除命令已完成，但旧版开机自启计划任务仍然存在。")
        };
    }

    private static bool IsApplicationCommand(string commandLine, string currentExecutablePath)
    {
        if (!StartupCommandLine.TryGetExecutablePath(commandLine, out var configuredExecutablePath))
            return false;

        try
        {
            var configuredFileName = Path.GetFileName(configuredExecutablePath);
            var currentFileName = Path.GetFileName(currentExecutablePath);
            if (string.Equals(configuredFileName, currentFileName, StringComparison.OrdinalIgnoreCase))
                return true;

            return KnownExecutableNames.Any(knownName =>
                string.Equals(knownName, configuredFileName, StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return false;
        }
    }

    [SupportedOSPlatform("windows")]
    private static SchtasksResult RunSchtasks(params string[] arguments)
    {
        var schtasksPath = Path.Combine(Environment.SystemDirectory, "schtasks.exe");
        if (!File.Exists(schtasksPath))
        {
            return new SchtasksResult(
                ExitCode: -1,
                Output: string.Empty,
                Error: $"系统中未找到 schtasks.exe：{schtasksPath}",
                TimedOut: false);
        }

        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = schtasksPath,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                }
            };

            foreach (var argument in arguments)
                process.StartInfo.ArgumentList.Add(argument);

            if (!process.Start())
            {
                return new SchtasksResult(
                    ExitCode: -1,
                    Output: string.Empty,
                    Error: "无法启动 schtasks.exe。",
                    TimedOut: false);
            }

            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();

            if (!process.WaitForExit(ProcessTimeoutMilliseconds))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch
                {
                    // 后续通过 TimedOut 返回原始超时错误。
                }

                WaitForOutput(outputTask, errorTask);
                return new SchtasksResult(
                    ExitCode: -1,
                    Output: GetCompletedOutput(outputTask),
                    Error: GetCompletedOutput(errorTask),
                    TimedOut: true);
            }

            WaitForOutput(outputTask, errorTask);
            return new SchtasksResult(
                process.ExitCode,
                GetCompletedOutput(outputTask),
                GetCompletedOutput(errorTask),
                TimedOut: false);
        }
        catch (Exception ex)
        {
            return new SchtasksResult(
                ExitCode: -1,
                Output: string.Empty,
                Error: ex.Message,
                TimedOut: false);
        }
    }

    private static void WaitForOutput(Task<string> outputTask, Task<string> errorTask)
    {
        try
        {
            Task.WaitAll([outputTask, errorTask], OutputCleanupTimeoutMilliseconds);
        }
        catch
        {
            // 诊断输出不是核心结果；后续只读取已经完成的任务。
        }
    }

    private static string GetCompletedOutput(Task<string> task) =>
        task.IsCompletedSuccessfully ? task.Result : string.Empty;

    private static string BuildFailureMessage(string operation, SchtasksResult result)
    {
        var detail = !string.IsNullOrWhiteSpace(result.Error)
            ? result.Error.Trim()
            : result.Output.Trim();

        if (detail.Length > MaxDiagnosticLength)
            detail = detail[..MaxDiagnosticLength] + "…";

        var exitCode = $"0x{unchecked((uint)result.ExitCode):X8}";
        return string.IsNullOrWhiteSpace(detail)
            ? $"{operation}（退出代码：{exitCode}）。"
            : $"{operation}（退出代码：{exitCode}）：{detail}";
    }

    private readonly record struct SchtasksResult(
        int ExitCode,
        string Output,
        string Error,
        bool TimedOut);
}

internal enum LegacyScheduledTaskState
{
    Missing,
    Owned,
    NotOwned,
    Unavailable
}

internal readonly record struct LegacyScheduledTaskInspection(
    LegacyScheduledTaskState State,
    string ErrorMessage)
{
    public static LegacyScheduledTaskInspection Missing() =>
        new(LegacyScheduledTaskState.Missing, string.Empty);

    public static LegacyScheduledTaskInspection Owned() =>
        new(LegacyScheduledTaskState.Owned, string.Empty);

    public static LegacyScheduledTaskInspection NotOwned() =>
        new(LegacyScheduledTaskState.NotOwned, string.Empty);

    public static LegacyScheduledTaskInspection Unavailable(string errorMessage) =>
        new(
            LegacyScheduledTaskState.Unavailable,
            string.IsNullOrWhiteSpace(errorMessage) ? "未知错误。" : errorMessage.Trim());
}
