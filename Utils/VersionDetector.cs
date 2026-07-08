using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace LuckyLilliaDesktop.Utils;

/// <summary>
/// PMHQ / LLBot 本地版本号检测。主页启动门槛 (CheckVersionRequirement) 与关于页共用同一实现,
/// 避免两套逻辑给出不一致的结果 (曾出现关于页 `pmhq --version` 读到 8.0.5, 而启动门槛只读
/// package.json 却因文件缺失拿到 null, 误报"无法确认 PMHQ 版本")。
/// PMHQ 以 `pmhq --version` 为主、package.json 兜底; LLBot 是 node 脚本, 只读同目录 package.json。
/// </summary>
public static class VersionDetector
{
    /// <summary>
    /// 检测 PMHQ 版本: 优先运行 `pmhq --version` (exe 存在才跑), 失败/超时/无法解析时退回读同目录 package.json。
    /// </summary>
    public static string? DetectPmhqVersion(string? pmhqPath, ILogger? logger = null)
    {
        if (string.IsNullOrEmpty(pmhqPath)) return null;

        var pmhqDir = Path.GetDirectoryName(pmhqPath);

        // 1) 优先运行 pmhq --version (package.json 未必随发布落盘, 不可靠, 见类注释)
        if (File.Exists(pmhqPath))
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = pmhqPath,
                    Arguments = "--version",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    WorkingDirectory = pmhqDir ?? Environment.CurrentDirectory
                };

                using var proc = Process.Start(psi);
                if (proc != null)
                {
                    var stdout = proc.StandardOutput.ReadToEnd();
                    var stderr = proc.StandardError.ReadToEnd();
                    if (!proc.WaitForExit(5000))
                    {
                        try { proc.Kill(entireProcessTree: true); } catch { }
                        logger?.LogWarning("`pmhq --version` 超时, 回退到 package.json");
                    }
                    else
                    {
                        // 部分程序把版本打到 stderr; 两个都尝试
                        var output = !string.IsNullOrWhiteSpace(stdout) ? stdout : stderr;
                        var version = ExtractVersion(output);
                        if (!string.IsNullOrEmpty(version))
                        {
                            return version;
                        }
                        logger?.LogWarning("`pmhq --version` 未解析到版本号 (exit={Exit}, output={Output}), 回退到 package.json",
                            proc.ExitCode, output?.Trim());
                    }
                }
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "运行 `pmhq --version` 失败, 回退到 package.json");
            }
        }

        // 2) 退回 package.json
        return ReadVersionFromPackageJson(pmhqDir, logger, "PMHQ");
    }

    /// <summary>
    /// 检测 LLBot 版本: 读取脚本同目录的 package.json。
    /// </summary>
    public static string? DetectLLBotVersion(string? llbotPath, ILogger? logger = null)
    {
        if (string.IsNullOrEmpty(llbotPath)) return null;
        var llbotDir = Path.GetDirectoryName(llbotPath);
        return ReadVersionFromPackageJson(llbotDir, logger, "LLBot");
    }

    private static string? ReadVersionFromPackageJson(string? dir, ILogger? logger, string component)
    {
        try
        {
            if (string.IsNullOrEmpty(dir)) return null;

            var packageJsonPath = Path.Combine(dir, "package.json");
            if (File.Exists(packageJsonPath))
            {
                var json = File.ReadAllText(packageJsonPath);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("version", out var versionElement))
                {
                    return versionElement.GetString();
                }
            }
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "从 package.json 检测 {Component} 版本失败", component);
        }
        return null;
    }

    /// <summary>
    /// 从 `--version` 命令输出里提取版本号。
    /// 兼容 "1.2.3" / "v1.2.3" / "PMHQ Rust 注入器 v1.2.3 (build ...)" 等常见格式。
    /// </summary>
    private static string? ExtractVersion(string? output)
    {
        if (string.IsNullOrWhiteSpace(output)) return null;
        var match = Regex.Match(output, @"\d+\.\d+\.\d+(?:[-.+][0-9A-Za-z.-]+)?");
        return match.Success ? match.Value : output.Trim();
    }
}
