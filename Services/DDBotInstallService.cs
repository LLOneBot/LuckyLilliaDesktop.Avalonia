using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Threading;
using System.Threading.Tasks;
using LuckyLilliaDesktop.Utils;
using Microsoft.Extensions.Logging;

namespace LuckyLilliaDesktop.Services;

public interface IDDBotInstallService
{
    Task<bool> InstallAsync(IProgress<InstallProgress>? progress = null, CancellationToken ct = default);
    bool IsInstalled { get; }
    string DDBotPath { get; }
    void StartDDBot();
}

public class DDBotInstallService : IDDBotInstallService
{
    private readonly ILogger<DDBotInstallService> _logger;
    private readonly IGitHubHelper _gitHubHelper;

    private const string DDBotDir = "bin/ddbot";

    private static string GetDownloadFileName()
    {
        if (PlatformHelper.IsWindows)
            return "DDBOT-WSa-fix_A041-windows-amd64.zip";
        if (PlatformHelper.IsMacOS)
        {
            var arch = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture == System.Runtime.InteropServices.Architecture.Arm64 ? "arm64" : "amd64";
            return $"DDBOT-WSa-fix_A041-darwin-{arch}.zip";
        }
        // Linux
        return "DDBOT-WSa-fix_A041-linux-amd64.zip";
    }

    private static string GetExeFileName() => PlatformHelper.IsWindows ? "DDBOT-WSa.exe" : "DDBOT-WSa";

    public bool IsInstalled => File.Exists(Path.Combine(DDBotDir, GetExeFileName()));
    public string DDBotPath => Path.GetFullPath(DDBotDir);

    public DDBotInstallService(ILogger<DDBotInstallService> logger, IGitHubHelper gitHubHelper)
    {
        _logger = logger;
        _gitHubHelper = gitHubHelper;
    }

    public async Task<bool> InstallAsync(IProgress<InstallProgress>? progress = null, CancellationToken ct = default)
    {
        const int totalSteps = 2;
        try
        {
            // Step 1: 下载 DDBOT-WSa
            Report(progress, 1, totalSteps, "下载 DDBot", "正在下载...");
            var downloadFileName = GetDownloadFileName();
            var tempZip = Path.Combine(Path.GetTempPath(), downloadFileName);

            string[] downloadUrls = [
                $"https://gh-proxy.com/https://github.com/cnxysoft/DDBOT-WSa/releases/download/fix_A041/{downloadFileName}",
                $"https://ghproxy.net/https://github.com/cnxysoft/DDBOT-WSa/releases/download/fix_A041/{downloadFileName}",
                $"https://mirror.ghproxy.com/https://github.com/cnxysoft/DDBOT-WSa/releases/download/fix_A041/{downloadFileName}",
                $"https://github.com/cnxysoft/DDBOT-WSa/releases/download/fix_A041/{downloadFileName}"
            ];

            var downloadSuccess = await _gitHubHelper.DownloadWithFallbackAsync(downloadUrls, tempZip,
                (downloaded, total) => Report(progress, 1, totalSteps, "下载 DDBot",
                    $"正在下载... {downloaded / 1024 / 1024:F1} MB / {total / 1024 / 1024:F1} MB",
                    total > 0 ? (double)downloaded / total * 100 : 0), ct);

            if (!downloadSuccess)
            {
                ReportError(progress, 1, totalSteps, "下载 DDBot 失败");
                return false;
            }

            // Step 2: 解压（覆盖已有文件）
            Report(progress, 2, totalSteps, "解压文件", "正在解压...");
            Directory.CreateDirectory(DDBotDir);
            await Task.Run(() => ZipFile.ExtractToDirectory(tempZip, DDBotDir, true), ct).ConfigureAwait(false);
            File.Delete(tempZip);
            _logger.LogInformation("DDBot 解压完成");

            Report(progress, totalSteps, totalSteps, "完成", "DDBot 安装完成", 100, true);
            _logger.LogInformation("DDBot 安装完成");
            return true;
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("DDBot 安装已取消");
            return false;
        }
        catch (System.Exception ex)
        {
            _logger.LogError(ex, "DDBot 安装失败");
            ReportError(progress, 0, totalSteps, ex.Message);
            return false;
        }
    }

    public void StartDDBot()
    {
        try
        {
            var ddbotPath = Path.GetFullPath(DDBotDir);
            var exePath = Path.Combine(ddbotPath, GetExeFileName());

            if (!File.Exists(exePath))
            {
                _logger.LogError("DDBot 未正确安装");
                return;
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = exePath,
                WorkingDirectory = ddbotPath,
                UseShellExecute = true
            });
            _logger.LogInformation("DDBot 已启动");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "启动 DDBot 失败");
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

    private static async Task SafeDeleteDirectoryAsync(string path)
    {
        if (!Directory.Exists(path)) return;

        foreach (var file in Directory.GetFiles(path, "*", SearchOption.AllDirectories))
        {
            try { File.SetAttributes(file, FileAttributes.Normal); } catch { }
        }

        for (int i = 0; i < 3; i++)
        {
            try
            {
                Directory.Delete(path, true);
                return;
            }
            catch (System.UnauthorizedAccessException) when (i < 2) { await Task.Delay(500); }
            catch (IOException) when (i < 2) { await Task.Delay(500); }
        }
    }
}
