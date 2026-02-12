using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Threading;
using System.Threading.Tasks;
using LuckyLilliaDesktop.Utils;
using Microsoft.Extensions.Logging;

namespace LuckyLilliaDesktop.Services;

public interface IZeroBotPluginInstallService
{
    Task<bool> InstallAsync(IProgress<InstallProgress>? progress = null, CancellationToken ct = default);
    bool IsInstalled { get; }
    string ZeroBotPluginPath { get; }
    void StartZeroBotPlugin();
}

public class ZeroBotPluginInstallService : IZeroBotPluginInstallService
{
    private readonly ILogger<ZeroBotPluginInstallService> _logger;
    private readonly IGitHubHelper _gitHubHelper;

    private const string ZeroBotDir = "bin/ZeroBot-Plugin";

    private static string GetDownloadFileName()
    {
        if (PlatformHelper.IsWindows)
            return "zbp_windows_amd64.zip";
        if (PlatformHelper.IsMacOS)
        {
            var arch = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture == System.Runtime.InteropServices.Architecture.Arm64 ? "arm64" : "amd64";
            return $"zbp_darwin_{arch}.zip";
        }
        // Linux
        return "zbp_linux_amd64.zip";
    }

    private static string GetExeFileName() => PlatformHelper.IsWindows ? "zbp.exe" : "zbp";

    public bool IsInstalled => File.Exists(Path.Combine(ZeroBotDir, GetExeFileName()));
    public string ZeroBotPluginPath => Path.GetFullPath(ZeroBotDir);

    public ZeroBotPluginInstallService(ILogger<ZeroBotPluginInstallService> logger, IGitHubHelper gitHubHelper)
    {
        _logger = logger;
        _gitHubHelper = gitHubHelper;
    }

    public async Task<bool> InstallAsync(IProgress<InstallProgress>? progress = null, CancellationToken ct = default)
    {
        const int totalSteps = 3;
        try
        {
            // Step 1: 获取最新版本
            Report(progress, 1, totalSteps, "获取版本信息", "正在获取最新版本...");
            var tag = await _gitHubHelper.GetLatestTagAsync("FloatTech", "ZeroBot-Plugin", ct);
            if (string.IsNullOrEmpty(tag))
            {
                ReportError(progress, 1, totalSteps, "无法获取最新版本信息");
                return false;
            }
            _logger.LogInformation("获取到 ZeroBot-Plugin 最新版本: {Tag}", tag);

            // Step 2: 下载
            Report(progress, 2, totalSteps, "下载 ZeroBot-Plugin", "正在下载...");
            var downloadFileName = GetDownloadFileName();
            var tempZip = Path.Combine(Path.GetTempPath(), downloadFileName);

            var downloadUrls = _gitHubHelper.GetGitHubUrlsWithProxy($"https://github.com/FloatTech/ZeroBot-Plugin/releases/download/{tag}/{downloadFileName}");

            var downloadSuccess = await _gitHubHelper.DownloadWithFallbackAsync(downloadUrls, tempZip,
                (downloaded, total) => Report(progress, 2, totalSteps, "下载 ZeroBot-Plugin",
                    $"正在下载... {downloaded / 1024 / 1024:F1} MB / {total / 1024 / 1024:F1} MB",
                    total > 0 ? (double)downloaded / total * 100 : 0), ct);

            if (!downloadSuccess)
            {
                ReportError(progress, 2, totalSteps, "下载 ZeroBot-Plugin 失败");
                return false;
            }

            // Step 3: 解压（覆盖已有文件）
            Report(progress, 3, totalSteps, "解压文件", "正在解压...");
            Directory.CreateDirectory(ZeroBotDir);
            await Task.Run(() => ZipFile.ExtractToDirectory(tempZip, ZeroBotDir, true), ct).ConfigureAwait(false);
            File.Delete(tempZip);

            Report(progress, totalSteps, totalSteps, "完成", "ZeroBot-Plugin 安装完成", 100, true);
            _logger.LogInformation("ZeroBot-Plugin 安装完成");
            return true;
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("ZeroBot-Plugin 安装已取消");
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ZeroBot-Plugin 安装失败");
            ReportError(progress, 0, totalSteps, ex.Message);
            return false;
        }
    }

    public void StartZeroBotPlugin()
    {
        try
        {
            var zbpPath = Path.GetFullPath(ZeroBotDir);
            var exePath = Path.Combine(zbpPath, GetExeFileName());

            if (!File.Exists(exePath))
            {
                _logger.LogError("ZeroBot-Plugin 未正确安装");
                return;
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = exePath,
                WorkingDirectory = zbpPath,
                UseShellExecute = true
            });
            _logger.LogInformation("ZeroBot-Plugin 已启动");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "启动 ZeroBot-Plugin 失败");
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
            catch (UnauthorizedAccessException) when (i < 2) { await Task.Delay(500); }
            catch (IOException) when (i < 2) { await Task.Delay(500); }
        }
    }
}
