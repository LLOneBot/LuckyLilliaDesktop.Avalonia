using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace LuckyLilliaDesktop.Services;

public class InstallProgress
{
    public int Step { get; set; }
    public int TotalSteps { get; set; }
    public string StepName { get; set; } = "";
    public string Status { get; set; } = "";
    public double Percentage { get; set; }
    public bool IsCompleted { get; set; }
    public bool HasError { get; set; }
    public string? ErrorMessage { get; set; }
}

public interface IKoishiInstallService
{
    Task<bool> InstallAsync(bool forceReinstall = false, IProgress<InstallProgress>? progress = null, CancellationToken ct = default);
    bool IsInstalled { get; }
}

public partial class KoishiInstallService : IKoishiInstallService
{
    private readonly ILogger<KoishiInstallService> _logger;
    private readonly IGitHubHelper _gitHubHelper;
    private readonly IConfigManager _configManager;

    private const string KoishiDir = "bin/koishi";

    public bool IsInstalled => File.Exists(Path.Combine(KoishiDir, "koi.exe"));

    public KoishiInstallService(ILogger<KoishiInstallService> logger, IConfigManager configManager, IGitHubHelper gitHubHelper)
    {
        _logger = logger;
        _configManager = configManager;
        _gitHubHelper = gitHubHelper;
    }

    public async Task<bool> InstallAsync(bool forceReinstall = false, IProgress<InstallProgress>? progress = null, CancellationToken ct = default)
    {
        const int totalSteps = 5;
        try
        {
            // 检查是否需要下载安装
            bool needDownload = !IsInstalled || forceReinstall;
            int startStep = needDownload ? 1 : 4;

            if (needDownload)
            {
                // Step 1: 获取最新版本
                Report(progress, 1, totalSteps, "获取版本信息", "正在获取最新版本...");
                var tag = await _gitHubHelper.GetLatestTagAsync("koishijs", "koishi-desktop", ct);
                if (string.IsNullOrEmpty(tag))
                {
                    ReportError(progress, 1, totalSteps, "无法获取最新版本信息");
                    return false;
                }
                _logger.LogInformation("获取到 Koishi 最新版本: {Tag}", tag);
                Report(progress, 1, totalSteps, "获取版本信息", $"最新版本: {tag}");

                // Step 2: 下载
                Report(progress, 2, totalSteps, "下载 Koishi", "正在下载...");
                var tempZip = Path.Combine(Path.GetTempPath(), $"koishi-{tag}.zip");
                var baseUrl = $"https://github.com/koishijs/koishi-desktop/releases/download/{tag}/koishi-desktop-win-x64-{tag}.zip";
                
                string[] downloadUrls = [
                    $"https://gh-proxy.com/{baseUrl}",
                    $"https://ghproxy.net/{baseUrl}",
                    $"https://mirror.ghproxy.com/{baseUrl}",
                    baseUrl
                ];
                
                var downloadSuccess = await _gitHubHelper.DownloadWithFallbackAsync(downloadUrls, tempZip,
                    (downloaded, total) => Report(progress, 2, totalSteps, "下载 Koishi",
                        $"正在下载... {downloaded / 1024 / 1024:F1} MB / {total / 1024 / 1024:F1} MB",
                        total > 0 ? (double)downloaded / total * 100 : 0), ct);
                
                if (!downloadSuccess)
                {
                    ReportError(progress, 2, totalSteps, "下载失败");
                    return false;
                }

                // Step 3: 解压
                Report(progress, 3, totalSteps, "解压文件", "正在解压...");
                if (Directory.Exists(KoishiDir))
                    Directory.Delete(KoishiDir, true);
                Directory.CreateDirectory(KoishiDir);
                await Task.Run(() => ZipFile.ExtractToDirectory(tempZip, KoishiDir, true), ct);
                File.Delete(tempZip);
                Report(progress, 3, totalSteps, "解压文件", "解压完成");
                _logger.LogInformation("Koishi 解压完成");
            }
            else
            {
                Report(progress, 3, totalSteps, "跳过下载", "已存在 Koishi，跳过下载步骤");
            }

            // Step 4: 修改配置文件
            Report(progress, 4, totalSteps, "修改配置", "正在修改 koishi.yml...");
            await ConfigureKoishiAsync(ct);
            Report(progress, 4, totalSteps, "修改配置", "配置修改完成");

            // Step 5: 安装依赖更新
            Report(progress, 5, totalSteps, "更新依赖", "正在安装 npm-check-updates...");
            await UpdateDependenciesAsync(progress, 5, totalSteps, ct);

            Report(progress, totalSteps, totalSteps, "完成", "安装完成", 100, true);
            _logger.LogInformation("Koishi 安装配置完成");
            return true;
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Koishi 安装已取消");
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Koishi 安装失败");
            ReportError(progress, 0, totalSteps, ex.Message);
            return false;
        }
    }

    private async Task ConfigureKoishiAsync(CancellationToken ct)
    {
        var configPath = Path.Combine(KoishiDir, "data/instances/default/koishi.yml");
        if (!File.Exists(configPath))
        {
            _logger.LogWarning("Koishi 配置文件不存在: {Path}", configPath);
            return;
        }

        var content = await File.ReadAllTextAsync(configPath, ct);

        // 修改 market endpoint 为国内镜像
        content = MarketEndpointRegex().Replace(content, 
            "market:\n      search:\n        endpoint: https://registry.koishi.t4wefan.pub/index.json");

        // 启用 adapter-satori 并配置 endpoint
        var satoriPort = _configManager.GetSetting("satori_port", "5600");
        content = SatoriRegex().Replace(content, 
            $"adapter-satori:\n      endpoint: http://127.0.0.1:{satoriPort}");

        await File.WriteAllTextAsync(configPath, content, ct);
        _logger.LogInformation("Koishi 配置已更新: market镜像 + adapter-satori");
    }

    private async Task UpdateDependenciesAsync(IProgress<InstallProgress>? progress, int step, int totalSteps, CancellationToken ct)
    {
        var instanceDir = Path.GetFullPath(Path.Combine(KoishiDir, "data/instances/default"));
        var koishiExe = Path.GetFullPath(Path.Combine(KoishiDir, "bin/koishi.exe"));
        var yarnPath = Path.GetFullPath(Path.Combine(KoishiDir, "bin/yarn.cjs"));

        if (!File.Exists(koishiExe) || !File.Exists(yarnPath))
        {
            _logger.LogWarning("koishi.exe 或 yarn.cjs 不存在，跳过依赖更新");
            return;
        }

        // 安装 npm-check-updates
        Report(progress, step, totalSteps, "更新依赖", "正在安装 npm-check-updates...");
        await RunCommandAsync(koishiExe, $"\"{yarnPath}\" add npm-check-updates", instanceDir, ct);

        // 运行 ncu -u 更新 package.json
        Report(progress, step, totalSteps, "更新依赖", "正在检查依赖更新...");
        var ncuPath = Path.GetFullPath(Path.Combine(instanceDir, "node_modules/.bin/npm-check-updates.cmd"));
        if (File.Exists(ncuPath))
        {
            await RunCommandAsync(ncuPath, "-u", instanceDir, ct);
        }
        else
        {
            _logger.LogWarning("npm-check-updates.cmd 不存在: {Path}", ncuPath);
        }

        // yarn install 安装更新后的依赖
        Report(progress, step, totalSteps, "更新依赖", "正在安装更新后的依赖...");
        await RunCommandAsync(koishiExe, $"\"{yarnPath}\" install", instanceDir, ct);

        _logger.LogInformation("Koishi 依赖更新完成");
    }

    private async Task RunCommandAsync(string exe, string args, string workDir, CancellationToken ct)
    {
        _logger.LogInformation("执行命令: {Exe} {Args} in {Dir}", exe, args, workDir);
        
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            Arguments = args,
            WorkingDirectory = workDir,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        using var process = Process.Start(psi);
        if (process == null) return;

        await process.WaitForExitAsync(ct);
        
        var output = await process.StandardOutput.ReadToEndAsync(ct);
        var error = await process.StandardError.ReadToEndAsync(ct);
        
        if (!string.IsNullOrEmpty(output))
            _logger.LogDebug("命令输出: {Output}", output);
        if (!string.IsNullOrEmpty(error))
            _logger.LogWarning("命令错误: {Error}", error);
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

    [GeneratedRegex(@"market:\s*\n\s*search:\s*\n\s*endpoint:[^\n]+", RegexOptions.Multiline)]
    private static partial Regex MarketEndpointRegex();

    [GeneratedRegex(@"~adapter-satori:\s*\{\}", RegexOptions.Multiline)]
    private static partial Regex SatoriRegex();
}
