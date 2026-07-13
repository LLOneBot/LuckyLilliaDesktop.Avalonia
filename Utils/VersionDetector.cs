using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace LuckyLilliaDesktop.Utils;

/// <summary>
/// PMHQ / LLBot 本地版本号检测。主页启动门槛 (CheckVersionRequirementAsync) 与关于页共用同一实现,
/// 避免两套逻辑给出不一致的结果 (曾出现关于页 `pmhq --version` 读到 8.0.5, 而启动门槛只读
/// package.json 却因文件缺失拿到 null, 误报"无法确认 PMHQ 版本")。
/// PMHQ 以 `pmhq --version` 为主、package.json 兜底; LLBot 是 node 脚本, 只读同目录 package.json。
/// </summary>
public static class VersionDetector
{
    // 兜底超时, 只防子进程挂死: 杀软对无签名注入器 exe 的首扫实测能把 --version 拖到 ~75s
    // (用户日志 20260712_230236), 设短了会误杀慢机器, 回退 package.json 后启动门槛会误报版本不可确认
    private static readonly TimeSpan VersionProcessTimeout = TimeSpan.FromSeconds(120);

    // key: exe 全路径+mtime+size。同一 exe 进程内只探测一次, 并发调用共享同一个 Task,
    // 避免启动时更新检查/关于页/启动门槛各跑一遍慢子进程
    private static readonly ConcurrentDictionary<string, Lazy<Task<string?>>> ExeVersionCache = new();

    /// <summary>
    /// 检测 PMHQ 版本: 优先运行 `pmhq --version` (exe 存在才跑), 失败/超时/无法解析时退回读同目录 package.json。
    /// 子进程全程跑在线程池上, 调用方 (含 UI 线程) 只是 await, 不会被慢 exe 拖死。
    /// </summary>
    public static async Task<string?> DetectPmhqVersionAsync(string? pmhqPath, ILogger? logger = null)
    {
        if (string.IsNullOrEmpty(pmhqPath)) return null;

        var pmhqDir = Path.GetDirectoryName(pmhqPath);

        // 1) 优先运行 pmhq --version (package.json 未必随发布落盘, 不可靠, 见类注释)
        if (File.Exists(pmhqPath))
        {
            var version = await GetExeVersionCachedAsync(pmhqPath, pmhqDir, logger);
            if (!string.IsNullOrEmpty(version))
            {
                return version;
            }
        }

        // 2) 退回 package.json
        return ReadVersionFromPackageJson(pmhqDir, logger, "PMHQ");
    }

    private static async Task<string?> GetExeVersionCachedAsync(string exePath, string? workDir, ILogger? logger)
    {
        string key;
        try
        {
            var info = new FileInfo(exePath);
            key = $"{info.FullName}|{info.LastWriteTimeUtc.Ticks}|{info.Length}";
        }
        catch
        {
            key = exePath;
        }

        // Task.Run 连 Process.Start 一起挪出调用线程: 杀软实时扫描可能直接堵在 CreateProcess,
        // 不只堵在读输出
        var lazy = ExeVersionCache.GetOrAdd(key, _ => new Lazy<Task<string?>>(
            () => Task.Run(() => RunVersionProcessAsync(exePath, workDir, logger))));

        var version = await lazy.Value;
        if (string.IsNullOrEmpty(version))
        {
            // 失败可能是临时性的, 不缓存, 下次调用重试
            ExeVersionCache.TryRemove(key, out _);
        }
        return version;
    }

    private static async Task<string?> RunVersionProcessAsync(string exePath, string? workDir, ILogger? logger)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = "--version",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = workDir ?? Environment.CurrentDirectory
            };

            using var proc = Process.Start(psi);
            if (proc == null) return null;

            // 先挂上异步读再等退出, 避免管道写满互相等死; 读流也套同一个超时
            var stdoutTask = proc.StandardOutput.ReadToEndAsync();
            var stderrTask = proc.StandardError.ReadToEndAsync();

            string stdout, stderr;
            using var cts = new CancellationTokenSource(VersionProcessTimeout);
            try
            {
                await proc.WaitForExitAsync(cts.Token);
                stdout = await stdoutTask.WaitAsync(cts.Token);
                stderr = await stderrTask.WaitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                try { proc.Kill(entireProcessTree: true); } catch { }
                logger?.LogWarning("`pmhq --version` 超过 {Timeout}s 未退出, 回退到 package.json",
                    (int)VersionProcessTimeout.TotalSeconds);
                return null;
            }

            // 部分程序把版本打到 stderr; 两个都尝试
            var output = !string.IsNullOrWhiteSpace(stdout) ? stdout : stderr;
            var version = ExtractVersion(output);
            if (!string.IsNullOrEmpty(version))
            {
                return version;
            }
            logger?.LogWarning("`pmhq --version` 未解析到版本号 (exit={Exit}, output={Output}), 回退到 package.json",
                proc.ExitCode, output?.Trim());
            return null;
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "运行 `pmhq --version` 失败, 回退到 package.json");
            return null;
        }
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
