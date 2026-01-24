using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace LuckyLilliaDesktop.Services;

public interface IAstrBotInstallService
{
    Task<bool> InstallAsync(IProgress<InstallProgress>? progress = null, CancellationToken ct = default);
    bool IsInstalled { get; }
    void StartAstrBot();
}

public class AstrBotInstallService : IAstrBotInstallService
{
    private readonly ILogger<AstrBotInstallService> _logger;
    private readonly IGitHubHelper _gitHubHelper;
    private readonly IPythonHelper _pythonHelper;

    private const string AstrBotDir = "bin/astrbot";

    public bool IsInstalled => File.Exists(Path.Combine(AstrBotDir, "main.py"));

    public AstrBotInstallService(ILogger<AstrBotInstallService> logger, IGitHubHelper gitHubHelper, IPythonHelper pythonHelper)
    {
        _logger = logger;
        _gitHubHelper = gitHubHelper;
        _pythonHelper = pythonHelper;
    }

    public async Task<bool> InstallAsync(IProgress<InstallProgress>? progress = null, CancellationToken ct = default)
    {
        const int totalSteps = 5;
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

            // Step 2: 下载 AstrBot 源码
            Report(progress, 2, totalSteps, "下载 AstrBot", "正在下载源码...");
            var tempZip = Path.Combine(Path.GetTempPath(), "astrbot-master.zip");
            
            string[] downloadUrls = [
                "https://gh-proxy.com/https://github.com/AstrBotDevs/AstrBot/archive/refs/heads/master.zip",
                "https://ghproxy.net/https://github.com/AstrBotDevs/AstrBot/archive/refs/heads/master.zip",
                "https://mirror.ghproxy.com/https://github.com/AstrBotDevs/AstrBot/archive/refs/heads/master.zip",
                "https://github.com/AstrBotDevs/AstrBot/archive/refs/heads/master.zip"
            ];

            var downloadSuccess = await _gitHubHelper.DownloadWithFallbackAsync(downloadUrls, tempZip,
                (downloaded, total) => Report(progress, 2, totalSteps, "下载 AstrBot",
                    $"正在下载源码... {downloaded / 1024 / 1024:F1} MB / {total / 1024 / 1024:F1} MB",
                    total > 0 ? (double)downloaded / total * 100 : 0), ct);

            if (!downloadSuccess)
            {
                ReportError(progress, 2, totalSteps, "下载 AstrBot 失败");
                return false;
            }

            // Step 3: 解压源码
            Report(progress, 3, totalSteps, "解压文件", "正在解压源码...");
            var tempExtractDir = Path.Combine(Path.GetTempPath(), "astrbot-extract");
            if (Directory.Exists(tempExtractDir))
                Directory.Delete(tempExtractDir, true);
            await Task.Run(() => ZipFile.ExtractToDirectory(tempZip, tempExtractDir, true), ct).ConfigureAwait(false);
            File.Delete(tempZip);
            
            var extractedDir = Path.Combine(tempExtractDir, "AstrBot-master");
            if (Directory.Exists(AstrBotDir))
                Directory.Delete(AstrBotDir, true);
            
            await Task.Run(() => CopyDirectory(extractedDir, AstrBotDir), ct).ConfigureAwait(false);
            Directory.Delete(tempExtractDir, true);
            _logger.LogInformation("AstrBot 源码解压完成");

            // Step 3.5: 下载 dist.zip 到 data 目录
            Report(progress, 3, totalSteps, "下载数据", "正在获取最新版本...");
            var tag = await _gitHubHelper.GetLatestTagAsync("AstrBotDevs", "AstrBot", ct);
            if (string.IsNullOrEmpty(tag))
            {
                _logger.LogWarning("无法获取 AstrBot 最新版本，跳过 dist.zip 下载");
            }
            else
            {
                Report(progress, 3, totalSteps, "下载数据", $"正在下载 dist.zip ({tag})...");
                var distZip = Path.Combine(Path.GetTempPath(), "astrbot-dist.zip");
                var baseUrl = $"https://github.com/AstrBotDevs/AstrBot/releases/download/{tag}/dist.zip";
                string[] distUrls = [
                    $"https://gh-proxy.com/{baseUrl}",
                    $"https://ghproxy.net/{baseUrl}",
                    $"https://mirror.ghproxy.com/{baseUrl}",
                    baseUrl
                ];

                if (await _gitHubHelper.DownloadWithFallbackAsync(distUrls, distZip,
                    (downloaded, total) => Report(progress, 3, totalSteps, "下载数据",
                        $"正在下载 dist.zip... {downloaded / 1024 / 1024:F1} MB / {total / 1024 / 1024:F1} MB",
                        total > 0 ? (double)downloaded / total * 100 : 0), ct))
                {
                    var dataDir = Path.Combine(AstrBotDir, "data");
                    Directory.CreateDirectory(dataDir);
                    await Task.Run(() => ZipFile.ExtractToDirectory(distZip, dataDir, true), ct).ConfigureAwait(false);
                    File.Delete(distZip);
                    _logger.LogInformation("dist.zip 解压到 data 目录完成");
                }
                else
                {
                    _logger.LogWarning("下载 dist.zip 失败，跳过");
                }
            }

            // Step 4: 使用 uv 创建虚拟环境并安装依赖
            Report(progress, 4, totalSteps, "安装依赖", "正在使用 uv 创建虚拟环境并安装依赖...");
            var astrBotPath = Path.GetFullPath(AstrBotDir);
            var requirementsFile = Path.Combine(astrBotPath, "requirements.txt");
            if (!await _pythonHelper.UvInstallRequirementsAsync(astrBotPath, requirementsFile,
                line => Report(progress, 4, totalSteps, "安装依赖", line), ct))
            {
                ReportError(progress, 4, totalSteps, "安装依赖失败");
                return false;
            }

            Report(progress, 5, 5, "完成", "AstrBot 安装完成", 100, true);
            _logger.LogInformation("AstrBot 安装完成");
            return true;
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("AstrBot 安装已取消");
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AstrBot 安装失败");
            ReportError(progress, 0, totalSteps, ex.Message);
            return false;
        }
    }

    public void StartAstrBot()
    {
        try
        {
            var astrBotPath = Path.GetFullPath(AstrBotDir);
            var venvPython = Path.Combine(astrBotPath, ".venv", "Scripts", "python.exe");

            if (!File.Exists(venvPython))
            {
                _logger.LogError("AstrBot 未正确安装");
                return;
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/k \"\"{venvPython}\" main.py & pause\"",
                WorkingDirectory = astrBotPath,
                UseShellExecute = true
            });
            _logger.LogInformation("AstrBot 已启动");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "启动 AstrBot 失败");
        }
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

    private static void CopyDirectory(string sourceDir, string destDir)
    {
        Directory.CreateDirectory(destDir);
        
        foreach (var file in Directory.GetFiles(sourceDir))
            File.Copy(file, Path.Combine(destDir, Path.GetFileName(file)));
        
        foreach (var dir in Directory.GetDirectories(sourceDir))
            CopyDirectory(dir, Path.Combine(destDir, Path.GetFileName(dir)));
    }
}
