using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Threading;
using System.Threading.Tasks;
using LuckyLilliaDesktop.Utils;
using Microsoft.Extensions.Logging;

namespace LuckyLilliaDesktop.Services;

public interface IZhenxunInstallService
{
    Task<bool> InstallAsync(IProgress<InstallProgress>? progress = null, CancellationToken ct = default);
    bool IsInstalled { get; }
    string ZhenxunPath { get; }
    void StartZhenxun();
    Task ConfigureEnvAsync(string superUser, int port = 8080);
}

public class ZhenxunInstallService : IZhenxunInstallService
{
    private readonly ILogger<ZhenxunInstallService> _logger;
    private readonly IGitHubHelper _gitHubHelper;
    private readonly IPythonHelper _pythonHelper;

    private const string ZhenxunDir = "bin/zhenxun";

    // 检测 pyproject.toml 存在来代表源码拉取成功
    public bool IsInstalled => File.Exists(Path.Combine(ZhenxunDir, "pyproject.toml"));
    public string ZhenxunPath => Path.GetFullPath(ZhenxunDir);

    public ZhenxunInstallService(ILogger<ZhenxunInstallService> logger, IGitHubHelper gitHubHelper, IPythonHelper pythonHelper)
    {
        _logger = logger;
        _gitHubHelper = gitHubHelper;
        _pythonHelper = pythonHelper;
    }

    public async Task<bool> InstallAsync(IProgress<InstallProgress>? progress = null, CancellationToken ct = default)
    {
        const int totalSteps = 4;
        try
        {
            // Step 1: 检查/下载 Python
            if (!_pythonHelper.IsInstalled)
            {
                Report(progress, 1, totalSteps, "下载 Python", "正在下载 Python 绿色版...");
                var success = await _pythonHelper.EnsureInstalledAsync(
                    (downloaded, total) => Report(progress, 1, totalSteps, "下载 Python",
                        $"正在下载... {downloaded / 1024 / 1024:F1} MB / {total / 1024 / 1024:F1} MB",
                        total > 0 ? (double)downloaded / total * 100 : 0), ct);
                if (!success)
                {
                    ReportError(progress, 1, totalSteps, "下载 Python 失败");
                    return false;
                }
            }
            else
            {
                Report(progress, 1, totalSteps, "检查 Python", "Python 已存在，跳过下载");
            }

            // Step 2: 下载 zhenxun_bot 源码
            Report(progress, 2, totalSteps, "下载真寻Bot", "正在下载源码...");
            var tempZip = Path.Combine(Path.GetTempPath(), "zhenxun-main.zip");

            var downloadUrls = _gitHubHelper.GetGitHubUrlsWithProxy("https://github.com/zhenxun-org/zhenxun_bot/archive/refs/heads/main.zip");

            var downloadSuccess = await _gitHubHelper.DownloadWithFallbackAsync(downloadUrls, tempZip,
                (downloaded, total) => Report(progress, 2, totalSteps, "下载真寻Bot",
                    $"正在下载源码... {downloaded / 1024 / 1024:F1} MB / {total / 1024 / 1024:F1} MB",
                    total > 0 ? (double)downloaded / total * 100 : 0), ct);

            if (!downloadSuccess)
            {
                ReportError(progress, 2, totalSteps, "下载真寻Bot失败");
                return false;
            }

            // Step 3: 解压源码
            Report(progress, 3, totalSteps, "解压文件", "正在解压源码...");
            var tempExtractDir = Path.Combine(Path.GetTempPath(), "zhenxun-extract");
            await SafeDeleteDirectoryAsync(tempExtractDir);
            await Task.Run(() => ZipFile.ExtractToDirectory(tempZip, tempExtractDir, true), ct).ConfigureAwait(false);
            File.Delete(tempZip);
            
            var extractedDir = Path.Combine(tempExtractDir, "zhenxun_bot-main");
            await Task.Run(() => CopyDirectory(extractedDir, ZhenxunDir, overwrite: true), ct).ConfigureAwait(false);
            await SafeDeleteDirectoryAsync(tempExtractDir);
            _logger.LogInformation("真寻Bot源码解压完成");

            // Step 4: 使用 uv sync 安装依赖
            Report(progress, 4, totalSteps, "安装依赖", "正在使用 uv sync 同步环境依赖...");
            var zhenxunPath = Path.GetFullPath(ZhenxunDir);
            
            var pyprojectFile = Path.Combine(zhenxunPath, "pyproject.toml");
            if (!await _pythonHelper.UvInstallRequirementsAsync(zhenxunPath, pyprojectFile,
                line => Report(progress, 4, totalSteps, "安装依赖", line), ct))
            {
                ReportError(progress, 4, totalSteps, "安装依赖失败");
                return false;
            }

            Report(progress, totalSteps, totalSteps, "完成", "真寻Bot安装完成", 100, true);
            _logger.LogInformation("真寻Bot安装完成");
            return true;
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("真寻Bot安装已取消");
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "真寻Bot安装失败");
            ReportError(progress, 0, totalSteps, ex.Message);
            return false;
        }
    }

    public void StartZhenxun()
    {
        try
        {
            var zhenxunPath = Path.GetFullPath(ZhenxunDir);
            var uvExe = _pythonHelper.ResolveUvExecutablePath();

            if (string.IsNullOrEmpty(uvExe) || !File.Exists(uvExe))
            {
                _logger.LogError("uv 未正确安装，无法启动");
                return;
            }

            _logger.LogInformation("准备启动真寻Bot CLI，工作目录: {Path}", zhenxunPath);

            // 使用 uv run 调度 zhenxun cli
            const string targetCommand = "zx run"; 

            if (PlatformHelper.IsWindows)
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = $"/k \"\"{uvExe}\" run {targetCommand} & pause\"",
                    WorkingDirectory = zhenxunPath,
                    UseShellExecute = true
                });
            }
            else
            {
                if (PlatformHelper.IsMacOS)
                {
                    var escapedPath = zhenxunPath.Replace("\"", "\\\"");
                    var escapedUv = uvExe.Replace("\"", "\\\"");

                    var fullCommand = $"cd '{escapedPath}' && '{escapedUv}' run {targetCommand}";
                    _logger.LogInformation("Terminal 执行命令: {Command}", fullCommand);

                    var script = $"tell application \\\"Terminal\\\" to do script \\\"{fullCommand}\\\"";

                    var psi = new ProcessStartInfo
                    {
                        FileName = "osascript",
                        Arguments = $"-e \"{script}\"",
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true
                    };

                    var proc = Process.Start(psi);
                    if (proc != null)
                    {
                        proc.WaitForExit();
                    }
                }
                else
                {
                    // Linux
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "xterm",
                        Arguments = $"-e \"{uvExe} run {targetCommand} && read -p 'Press enter to exit...'\"",
                        WorkingDirectory = zhenxunPath,
                        UseShellExecute = true
                    });
                }
            }
            _logger.LogInformation("真寻Bot CLI 启动命令已执行");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "启动真寻Bot失败");
        }
    }

    public async Task ConfigureEnvAsync(string superUser, int port = 8080)
    {
        // 新版真寻在没有 .env.dev 时会自动创建包含全部必备字段的标准配置模板，并引导去 WebUI
        // 故此方法留空，避免写入不全/旧格式的文件干扰真寻自身的配置初始化机制
        _logger.LogInformation("将由真寻自带 WebUI 管理配置，跳过桌面端强制写入。");
        await Task.CompletedTask;
    }

    private static void Report(IProgress<InstallProgress>? progress, int step, int totalSteps,
        string stepName, string status, double pct = 0, bool completed = false)
    {
        progress?.Report(new InstallProgress
        {
            Step = step,
            TotalSteps = totalSteps,
            StepName = stepName,
            Status = status,
            Percentage = pct,
            IsCompleted = completed
        });
    }

    private static void ReportError(IProgress<InstallProgress>? progress, int step, int totalSteps, string message)
    {
        progress?.Report(new InstallProgress
        {
            Step = step,
            TotalSteps = totalSteps,
            StepName = "错误",
            Status = message,
            HasError = true,
            ErrorMessage = message
        });
    }

    private static void CopyDirectory(string sourceDir, string destDir, bool overwrite = false)
    {
        Directory.CreateDirectory(destDir);
        
        foreach (var file in Directory.GetFiles(sourceDir))
            File.Copy(file, Path.Combine(destDir, Path.GetFileName(file)), overwrite);
        
        foreach (var dir in Directory.GetDirectories(sourceDir))
            CopyDirectory(dir, Path.Combine(destDir, Path.GetFileName(dir)), overwrite);
    }

    private static async Task SafeDeleteDirectoryAsync(string path)
    {
        if (!Directory.Exists(path)) return;

        // 先清除只读属性
        foreach (var file in Directory.GetFiles(path, "*", SearchOption.AllDirectories))
        {
            try
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }
            catch { }
        }

        // 重试删除
        for (int i = 0; i < 3; i++)
        {
            try
            {
                Directory.Delete(path, true);
                return;
            }
            catch (UnauthorizedAccessException) when (i < 2)
            {
                await Task.Delay(500);
            }
            catch (IOException) when (i < 2)
            {
                await Task.Delay(500);
            }
        }
    }
}