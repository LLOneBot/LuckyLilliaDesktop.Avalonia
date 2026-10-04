using Microsoft.Win32;
using System;
using System.IO;
using System.Runtime.Versioning;

namespace LuckyLilliaDesktop.Utils;

/// <summary>
/// 管理 Windows 当前用户的登录自启。
/// 新的自启配置仅写入 HKCU Run，不需要提升权限；同时兼容清理旧版本
/// 曾创建的同名计划任务，避免升级后无法关闭或重复启动。
/// </summary>
public static class StartupManager
{
    private const string StartupRegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string StartupValueName = "LuckyLilliaDesktop";
    private const int MaxStartupCommandLength = 260;
    private static readonly string[] KnownExecutableNames =
        ["LuckyLilliaDesktop.exe", "lucky-lillia-desktop.exe", "LuckyLillia.exe"];
    private static readonly object SyncRoot = new();

    internal const string StartupDelayArgument = "--startup-delay=5";

    /// <summary>
    /// 检查当前用户的开机自启状态。注册表未启用时，也会识别旧版本
    /// 遗留且确认属于本应用的计划任务。
    /// </summary>
    public static bool IsStartupEnabled()
    {
        if (!PlatformHelper.IsWindows || !TryGetCurrentExecutablePath(out var executablePath, out _))
            return false;

        try
        {
            lock (SyncRoot)
            {
                var validation = ValidateRegistryStartup(executablePath, out var isEnabled);
                if (!validation.Success || isEnabled)
                    return validation.Success && isEnabled;

                // 仅识别旧版本遗留的同名计划任务，便于用户正常关闭并完成迁移。
                return LegacyScheduledTaskManager.Inspect(executablePath).State ==
                       LegacyScheduledTaskState.Owned;
            }
        }
        catch
        {
            return false;
        }
    }

    public static bool EnableStartup() => TryEnableStartup().Success;

    internal static StartupOperationResult TryEnableStartup()
    {
        if (!PlatformHelper.IsWindows)
            return StartupOperationResult.Failed("当前平台不支持 Windows 开机自启。");

        try
        {
            lock (SyncRoot)
            {
                if (!TryGetCurrentExecutablePath(out var executablePath, out var pathError))
                    return StartupOperationResult.Failed(pathError);

                var validation = ValidateRegistryStartup(executablePath, out var isEnabled);
                if (!validation.Success || isEnabled)
                    return validation;

                return WriteStartupRegistryValue(executablePath);
            }
        }
        catch (Exception ex)
        {
            return StartupOperationResult.Failed($"创建开机自启配置时发生异常：{ex.Message}");
        }
    }

    public static bool DisableStartup() => TryDisableStartup().Success;

    internal static StartupOperationResult TryDisableStartup()
    {
        if (!PlatformHelper.IsWindows)
            return StartupOperationResult.Failed("当前平台不支持 Windows 开机自启。");

        try
        {
            lock (SyncRoot)
            {
                if (!TryGetCurrentExecutablePath(out var executablePath, out var pathError))
                    return StartupOperationResult.Failed(pathError);

                if (!TryReadStartupRegistryValue(out var value, out var readError))
                    return StartupOperationResult.Failed(readError);

                StartupOperationResult registryCleanup;
                if (!value.Exists)
                {
                    registryCleanup = StartupOperationResult.Succeeded();
                }
                else
                {
                    if (value.Text == null || !IsApplicationStartupCommand(value.Text, executablePath))
                    {
                        return StartupOperationResult.Failed(
                            "检测到同名启动项，但无法确认它属于当前应用；为避免误删已保留该值。");
                    }

                    registryCleanup = DeleteStartupRegistryValue(value.Text);
                }

                if (!registryCleanup.Success)
                    return registryCleanup;

                // 兼容 3.0.8 早期版本创建的同名计划任务。新版本不再创建它，
                // 但关闭自启时必须一并清理，否则用户会遇到“取消后仍然启动”。
                return LegacyScheduledTaskManager.TryDeleteOwnedTask(executablePath);
            }
        }
        catch (Exception ex)
        {
            return StartupOperationResult.Failed($"关闭开机自启时发生异常：{ex.Message}");
        }
    }

    /// <summary>
    /// 清理程序移动后遗留的无效注册表值，并把旧版本创建的同名
    /// 计划任务安全迁移到 HKCU Run。新版本不会创建新的计划任务。
    /// </summary>
    internal static StartupOperationResult TryCleanupInvalidRegistryStartup()
    {
        if (!PlatformHelper.IsWindows)
            return StartupOperationResult.Succeeded();

        try
        {
            lock (SyncRoot)
            {
                if (!TryGetCurrentExecutablePath(out var executablePath, out var pathError))
                    return StartupOperationResult.Failed(pathError);

                var registryValidation = ValidateRegistryStartup(executablePath, out var registryEnabled);
                if (!registryValidation.Success)
                    return registryValidation;

                var legacyTask = LegacyScheduledTaskManager.Inspect(executablePath);
                switch (legacyTask.State)
                {
                    case LegacyScheduledTaskState.Missing:
                    case LegacyScheduledTaskState.NotOwned:
                        return StartupOperationResult.Succeeded();
                    case LegacyScheduledTaskState.Unavailable:
                        return StartupOperationResult.Failed(legacyTask.ErrorMessage);
                }

                // 旧版本可能只创建了计划任务。先写入并校验新的 HKCU Run 值，
                // 再删除旧任务，保证迁移失败时不会直接丢失原有自启能力。
                if (!registryEnabled)
                {
                    var migration = WriteStartupRegistryValue(executablePath);
                    if (!migration.Success)
                    {
                        return StartupOperationResult.Failed(
                            $"检测到旧版开机自启计划任务，但迁移到注册表失败：{migration.ErrorMessage}");
                    }
                }

                var cleanup = LegacyScheduledTaskManager.TryDeleteOwnedTask(executablePath);
                return cleanup.Success
                    ? StartupOperationResult.Succeeded()
                    : StartupOperationResult.Failed(
                        "注册表开机自启已就绪，但旧版计划任务清理失败，登录时可能重复启动：" +
                        cleanup.ErrorMessage);
            }
        }
        catch (Exception ex)
        {
            return StartupOperationResult.Failed($"检查或迁移开机自启配置时发生异常：{ex.Message}");
        }
    }

    [SupportedOSPlatform("windows")]
    private static StartupOperationResult ValidateRegistryStartup(
        string executablePath,
        out bool isEnabled)
    {
        isEnabled = false;

        if (!TryReadStartupRegistryValue(out var value, out var readError))
            return StartupOperationResult.Failed(readError);

        if (!value.Exists)
            return StartupOperationResult.Succeeded();

        if (value.Text == null)
        {
            return StartupOperationResult.Failed(
                "检测到同名启动项，但其注册表类型不是字符串；为避免误删已保留该值。");
        }

        if (StartupCommandLine.TargetsExecutable(value.Text, executablePath))
        {
            isEnabled = true;
            return StartupOperationResult.Succeeded();
        }

        if (!IsApplicationStartupCommand(value.Text, executablePath))
        {
            return StartupOperationResult.Failed(
                "检测到同名启动项，但无法确认它属于当前应用；为避免误删已保留该值。");
        }

        // 值确认属于本应用，但已指向旧路径或命令无效。删除方法会再次读取并
        // 精确比对原值，防止检查期间被其他进程修改后误删新值。
        return DeleteStartupRegistryValue(value.Text);
    }

    [SupportedOSPlatform("windows")]
    private static StartupOperationResult WriteStartupRegistryValue(string executablePath)
    {
        var startupCommand = $"\"{executablePath}\" {StartupDelayArgument}";
        if (startupCommand.Length > MaxStartupCommandLength)
        {
            return StartupOperationResult.Failed(
                $"当前程序路径过长，启动命令超过 {MaxStartupCommandLength} 个字符。" +
                "请将程序移动到更短的目录后重试。");
        }

        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(StartupRegistryPath, writable: true);
            if (key == null)
                return StartupOperationResult.Failed("无法打开当前用户的开机启动注册表项。");

            var previousValue = key.GetValue(
                StartupValueName,
                defaultValue: null,
                RegistryValueOptions.DoNotExpandEnvironmentNames);
            var previousKind = previousValue == null
                ? (RegistryValueKind?)null
                : key.GetValueKind(StartupValueName);

            if (previousValue is string previousText)
            {
                if (StartupCommandLine.TargetsExecutable(previousText, executablePath))
                    return StartupOperationResult.Succeeded();

                if (!IsApplicationStartupCommand(previousText, executablePath))
                {
                    return StartupOperationResult.Failed(
                        "检测到同名启动项，但无法确认它属于当前应用；为避免覆盖已取消设置。");
                }
            }
            else if (previousValue != null)
            {
                return StartupOperationResult.Failed(
                    "检测到同名启动项，但其注册表类型不是字符串；为避免覆盖已取消设置。");
            }

            try
            {
                key.SetValue(StartupValueName, startupCommand, RegistryValueKind.String);

                var writtenValue = key.GetValue(
                    StartupValueName,
                    defaultValue: null,
                    RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
                if (!string.Equals(writtenValue, startupCommand, StringComparison.Ordinal) ||
                    !StartupCommandLine.TargetsExecutable(writtenValue, executablePath))
                {
                    throw new InvalidOperationException("写入后的启动项校验失败。");
                }
            }
            catch
            {
                RestoreRegistryValueIfUnchanged(
                    key,
                    startupCommand,
                    previousValue,
                    previousKind);
                throw;
            }

            return StartupOperationResult.Succeeded();
        }
        catch (Exception ex)
        {
            return StartupOperationResult.Failed($"写入开机自启注册表值失败：{ex.Message}");
        }
    }

    [SupportedOSPlatform("windows")]
    private static void RestoreRegistryValueIfUnchanged(
        RegistryKey key,
        string attemptedValue,
        object? previousValue,
        RegistryValueKind? previousKind)
    {
        try
        {
            var currentValue = key.GetValue(
                StartupValueName,
                defaultValue: null,
                RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
            if (!string.Equals(currentValue, attemptedValue, StringComparison.Ordinal))
                return;

            if (previousValue == null || previousKind == null)
                key.DeleteValue(StartupValueName, throwOnMissingValue: false);
            else
                key.SetValue(StartupValueName, previousValue, previousKind.Value);
        }
        catch
        {
            // 保留原始写入异常；不在恢复失败时覆盖更有价值的错误信息。
        }
    }

    private static bool TryGetCurrentExecutablePath(out string executablePath, out string errorMessage)
    {
        executablePath = Environment.ProcessPath ?? string.Empty;
        if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath))
        {
            errorMessage = "无法获取当前程序的可执行文件路径。";
            return false;
        }

        try
        {
            executablePath = Path.GetFullPath(executablePath);
            errorMessage = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            errorMessage = $"无法规范化当前程序路径：{ex.Message}";
            return false;
        }
    }

    private static bool IsApplicationStartupCommand(
        string commandLine,
        string currentExecutablePath)
    {
        if (!StartupCommandLine.TryGetExecutablePath(commandLine, out var configuredExecutablePath))
            return false;

        try
        {
            var configuredFileName = Path.GetFileName(configuredExecutablePath);
            var currentFileName = Path.GetFileName(currentExecutablePath);
            if (string.Equals(configuredFileName, currentFileName, StringComparison.OrdinalIgnoreCase))
                return true;

            return Array.Exists(
                KnownExecutableNames,
                knownName => string.Equals(
                    knownName,
                    configuredFileName,
                    StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return false;
        }
    }

    [SupportedOSPlatform("windows")]
    private static bool TryReadStartupRegistryValue(
        out StartupRegistryValue value,
        out string errorMessage)
    {
        value = default;
        errorMessage = string.Empty;

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(StartupRegistryPath, writable: false);
            if (key == null)
                return true;

            var rawValue = key.GetValue(
                StartupValueName,
                defaultValue: null,
                RegistryValueOptions.DoNotExpandEnvironmentNames);
            if (rawValue == null)
                return true;

            value = new StartupRegistryValue(true, rawValue as string);
            return true;
        }
        catch (Exception ex)
        {
            errorMessage = $"读取开机自启注册表值失败：{ex.Message}";
            return false;
        }
    }

    [SupportedOSPlatform("windows")]
    private static StartupOperationResult DeleteStartupRegistryValue(string expectedValue)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(StartupRegistryPath, writable: true);
            if (key == null)
                return StartupOperationResult.Succeeded();

            var currentValue = key.GetValue(
                StartupValueName,
                defaultValue: null,
                RegistryValueOptions.DoNotExpandEnvironmentNames);
            if (currentValue == null)
                return StartupOperationResult.Succeeded();

            if (currentValue is not string currentText ||
                !string.Equals(currentText, expectedValue, StringComparison.Ordinal))
            {
                return StartupOperationResult.Failed(
                    "开机自启注册表值在操作期间发生变化，为避免误删已取消操作。");
            }

            // 安全边界：仅删除 HKCU\...\Run 下名为 LuckyLilliaDesktop 的单个值。
            // 不删除 Run 键本身，也不枚举或修改其他程序的启动项。
            key.DeleteValue(StartupValueName, throwOnMissingValue: false);

            var remainingValue = key.GetValue(
                StartupValueName,
                defaultValue: null,
                RegistryValueOptions.DoNotExpandEnvironmentNames);
            return remainingValue == null
                ? StartupOperationResult.Succeeded()
                : StartupOperationResult.Failed("删除启动项后校验失败，该值仍然存在。");
        }
        catch (Exception ex)
        {
            return StartupOperationResult.Failed($"删除开机自启注册表值失败：{ex.Message}");
        }
    }

    private readonly record struct StartupRegistryValue(bool Exists, string? Text);
}

internal readonly record struct StartupOperationResult(bool Success, string ErrorMessage)
{
    public static StartupOperationResult Succeeded() => new(true, string.Empty);

    public static StartupOperationResult Failed(string errorMessage) =>
        new(false, string.IsNullOrWhiteSpace(errorMessage) ? "未知错误。" : errorMessage.Trim());
}
