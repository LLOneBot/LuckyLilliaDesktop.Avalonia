using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace LuckyLilliaDesktop.Services;

public interface IYunzaiInstallService
{
    Task<bool> InstallAsync(IProgress<InstallProgress>? progress = null, CancellationToken ct = default);
    bool IsInstalled { get; }
    string YunzaiPath { get; }
    string Node24Path { get; }
    void StartYunzai();
}

public class YunzaiInstallService : IYunzaiInstallService
{
    private readonly ILogger<YunzaiInstallService> _logger;
    private readonly IGitHubHelper _gitHubHelper;

    private const string YunzaiDir = "bin/yunzai";
    private const string Node24Dir = "bin/node24";
    private const string RedisDir = "bin/redis";
    private const string NodeVersion = "v24.4.1";
    private const string RedisVersion = "7.4.7";

    public bool IsInstalled => File.Exists(Path.Combine(YunzaiDir, "package.json"));
    public string YunzaiPath => Path.GetFullPath(YunzaiDir);
    public string Node24Path => Path.GetFullPath(Node24Dir);
    public string RedisPath => Path.GetFullPath(RedisDir);

    public YunzaiInstallService(ILogger<YunzaiInstallService> logger, IGitHubHelper gitHubHelper)
    {
        _logger = logger;
        _gitHubHelper = gitHubHelper;
    }

    public async Task<bool> InstallAsync(IProgress<InstallProgress>? progress = null, CancellationToken ct = default)
    {
        const int totalSteps = 6;
        try
        {
            // Step 1: 下载 Redis（如果不存在）
            if (!File.Exists(Path.Combine(RedisDir, "redis-server.exe")))
            {
                Report(progress, 1, totalSteps, "下载 Redis", "正在下载...");
                var redisFileName = $"Redis-{RedisVersion}-Windows-x64-msys2.zip";
                var redisZip = Path.Combine(Path.GetTempPath(), redisFileName);

                string[] redisUrls = [
                    $"https://gh-proxy.com/https://github.com/redis-windows/redis-windows/releases/download/{RedisVersion}/{redisFileName}",
                    $"https://ghproxy.net/https://github.com/redis-windows/redis-windows/releases/download/{RedisVersion}/{redisFileName}",
                    $"https://github.com/redis-windows/redis-windows/releases/download/{RedisVersion}/{redisFileName}"
                ];

                var downloadSuccess = await _gitHubHelper.DownloadWithFallbackAsync(redisUrls, redisZip,
                    (downloaded, total) => Report(progress, 1, totalSteps, "下载 Redis",
                        $"正在下载... {downloaded / 1024 / 1024:F1} MB / {total / 1024 / 1024:F1} MB",
                        total > 0 ? (double)downloaded / total * 100 : 0), ct);

                if (!downloadSuccess)
                {
                    ReportError(progress, 1, totalSteps, "下载 Redis 失败");
                    return false;
                }

                Report(progress, 1, totalSteps, "解压 Redis", "正在解压...");
                var tempExtract = Path.Combine(Path.GetTempPath(), "redis-extract");
                await SafeDeleteDirectoryAsync(tempExtract);
                await Task.Run(() => ZipFile.ExtractToDirectory(redisZip, tempExtract, true), ct);
                await Task.Run(() => File.Delete(redisZip), ct);

                await SafeDeleteDirectoryAsync(RedisDir);
                var extractedDir = Path.Combine(tempExtract, $"Redis-{RedisVersion}-Windows-x64-msys2");
                await Task.Run(() => CopyDirectory(extractedDir, RedisDir), ct);
                await SafeDeleteDirectoryAsync(tempExtract);
                _logger.LogInformation("Redis 解压完成");
            }
            else
            {
                Report(progress, 1, totalSteps, "检查 Redis", "Redis 已存在，跳过下载");
            }

            // Step 2: 下载 Node.js 24（如果不存在）
            if (!File.Exists(Path.Combine(Node24Dir, "node.exe")))
            {
                Report(progress, 2, totalSteps, "下载 Node.js 24", "正在下载...");
                var nodeZip = Path.Combine(Path.GetTempPath(), $"node-{NodeVersion}-win-x64.zip");

                string[] nodeUrls = [
                    $"https://npmmirror.com/mirrors/node/{NodeVersion}/node-{NodeVersion}-win-x64.zip",
                    $"https://cdn.npmmirror.com/binaries/node/{NodeVersion}/node-{NodeVersion}-win-x64.zip",
                    $"https://nodejs.org/dist/{NodeVersion}/node-{NodeVersion}-win-x64.zip"
                ];

                var downloadSuccess = await _gitHubHelper.DownloadWithFallbackAsync(nodeUrls, nodeZip,
                    (downloaded, total) => Report(progress, 2, totalSteps, "下载 Node.js 24",
                        $"正在下载... {downloaded / 1024 / 1024:F1} MB / {total / 1024 / 1024:F1} MB",
                        total > 0 ? (double)downloaded / total * 100 : 0), ct);

                if (!downloadSuccess)
                {
                    ReportError(progress, 2, totalSteps, "下载 Node.js 24 失败");
                    return false;
                }

                Report(progress, 2, totalSteps, "解压 Node.js 24", "正在解压...");
                var tempExtract = Path.Combine(Path.GetTempPath(), "node24-extract");
                await SafeDeleteDirectoryAsync(tempExtract);
                await Task.Run(() => ZipFile.ExtractToDirectory(nodeZip, tempExtract, true), ct);
                await Task.Run(() => File.Delete(nodeZip), ct);

                // 复制到目标目录（跨盘符不能用 Move）
                await SafeDeleteDirectoryAsync(Node24Dir);
                var extractedDir = Path.Combine(tempExtract, $"node-{NodeVersion}-win-x64");
                await Task.Run(() => CopyDirectory(extractedDir, Node24Dir), ct);
                await SafeDeleteDirectoryAsync(tempExtract);
                _logger.LogInformation("Node.js 24 解压完成");
            }
            else
            {
                Report(progress, 2, totalSteps, "检查 Node.js 24", "Node.js 24 已存在，跳过下载");
            }

            // Step 3: 下载云崽源码
            Report(progress, 3, totalSteps, "下载云崽", "正在下载源码...");
            var yunzaiZip = Path.Combine(Path.GetTempPath(), "yunzai-main.zip");

            string[] yunzaiUrls = [
                "https://gh-proxy.com/https://github.com/TimeRainStarSky/Yunzai/archive/refs/heads/main.zip",
                "https://ghproxy.net/https://github.com/TimeRainStarSky/Yunzai/archive/refs/heads/main.zip",
                "https://mirror.ghproxy.com/https://github.com/TimeRainStarSky/Yunzai/archive/refs/heads/main.zip",
                "https://github.com/TimeRainStarSky/Yunzai/archive/refs/heads/main.zip"
            ];

            var downloadYunzai = await _gitHubHelper.DownloadWithFallbackAsync(yunzaiUrls, yunzaiZip,
                (downloaded, total) => Report(progress, 3, totalSteps, "下载云崽",
                    $"正在下载源码... {downloaded / 1024 / 1024:F1} MB / {total / 1024 / 1024:F1} MB",
                    total > 0 ? (double)downloaded / total * 100 : 0), ct);

            if (!downloadYunzai)
            {
                ReportError(progress, 3, totalSteps, "下载云崽失败");
                return false;
            }

            // Step 4: 解压云崽
            Report(progress, 4, totalSteps, "解压文件", "正在解压云崽...");
            var tempYunzai = Path.Combine(Path.GetTempPath(), "yunzai-extract");
            await SafeDeleteDirectoryAsync(tempYunzai);
            await Task.Run(() => ZipFile.ExtractToDirectory(yunzaiZip, tempYunzai, true), ct);
            await Task.Run(() => File.Delete(yunzaiZip), ct);

            await SafeDeleteDirectoryAsync(YunzaiDir);
            var extractedYunzai = Path.Combine(tempYunzai, "Yunzai-main");
            await Task.Run(() => CopyDirectory(extractedYunzai, YunzaiDir), ct);
            await SafeDeleteDirectoryAsync(tempYunzai);
            _logger.LogInformation("云崽源码解压完成");

            // Step 5: 安装 pnpm
            Report(progress, 5, totalSteps, "安装 pnpm", "正在安装 pnpm...");
            var nodeExe = Path.GetFullPath(Path.Combine(Node24Dir, "node.exe"));
            var npmCli = Path.GetFullPath(Path.Combine(Node24Dir, "node_modules", "npm", "bin", "npm-cli.js"));

            if (!await RunCommandAsync(nodeExe, $"\"{npmCli}\" install -g pnpm --registry=https://registry.npmmirror.com",
                Node24Path, line => Report(progress, 5, totalSteps, "安装 pnpm", line), ct))
            {
                ReportError(progress, 5, totalSteps, "安装 pnpm 失败");
                return false;
            }

            // Step 6: 安装云崽依赖
            Report(progress, 6, totalSteps, "安装依赖", "正在安装云崽依赖...");
            var pnpmCmd = Path.GetFullPath(Path.Combine(Node24Dir, "pnpm.cmd"));
            
            if (!await RunCommandAsync(pnpmCmd, "install --registry=https://registry.npmmirror.com",
                YunzaiPath, line => Report(progress, 6, totalSteps, "安装依赖", line), ct))
            {
                ReportError(progress, 6, totalSteps, "安装依赖失败");
                return false;
            }

            Report(progress, totalSteps, totalSteps, "完成", "云崽安装完成", 100, true);
            _logger.LogInformation("云崽安装完成");
            return true;
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("云崽安装已取消");
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "云崽安装失败");
            ReportError(progress, 0, totalSteps, ex.Message);
            return false;
        }
    }

    public void StartYunzai()
    {
        try
        {
            // 先启动 Redis
            var redisExe = Path.GetFullPath(Path.Combine(RedisDir, "redis-server.exe"));
            if (File.Exists(redisExe))
            {
                var redisProcs = Process.GetProcessesByName("redis-server");
                if (redisProcs.Length == 0)
                {
                    // 确保数据目录存在，解决 RDB 持久化问题
                    var dataDir = Path.GetFullPath(Path.Combine(RedisDir, "data"));
                    Directory.CreateDirectory(dataDir);
                    // Windows 路径需要替换反斜杠为正斜杠给 Redis
                    var dataDirForRedis = dataDir.Replace("\\", "/");
                    
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = redisExe,
                        Arguments = $"--dir \"{dataDirForRedis}\"",
                        WorkingDirectory = RedisPath,
                        UseShellExecute = true,
                        WindowStyle = ProcessWindowStyle.Minimized
                    });
                    _logger.LogInformation("Redis 已启动，数据目录: {Dir}", dataDir);
                    System.Threading.Thread.Sleep(1500);
                }
                else
                {
                    _logger.LogInformation("Redis 已在运行中");
                }
            }
            else
            {
                _logger.LogWarning("Redis 未安装: {Path}", redisExe);
            }

            var yunzaiPath = Path.GetFullPath(YunzaiDir);
            var nodeExe = Path.GetFullPath(Path.Combine(Node24Dir, "node.exe"));

            if (!File.Exists(nodeExe))
            {
                _logger.LogError("Node.js 24 未安装");
                return;
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/k \"\"{nodeExe}\" . & pause\"",
                WorkingDirectory = yunzaiPath,
                UseShellExecute = true
            });
            _logger.LogInformation("云崽已启动");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "启动云崽失败");
        }
    }

    private async Task<bool> RunCommandAsync(string exe, string args, string workDir,
        Action<string>? onOutput = null, CancellationToken ct = default)
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
        if (process == null) return false;

        var outputTask = Task.Run(async () =>
        {
            while (!process.StandardOutput.EndOfStream)
            {
                var line = await process.StandardOutput.ReadLineAsync(ct);
                if (!string.IsNullOrEmpty(line))
                {
                    _logger.LogDebug("{Output}", line);
                    onOutput?.Invoke(line);
                }
            }
        }, ct);

        var errorTask = Task.Run(async () =>
        {
            while (!process.StandardError.EndOfStream)
            {
                var line = await process.StandardError.ReadLineAsync(ct);
                if (!string.IsNullOrEmpty(line))
                    _logger.LogWarning("{Error}", line);
            }
        }, ct);

        await process.WaitForExitAsync(ct);
        await Task.WhenAll(outputTask, errorTask);

        return process.ExitCode == 0;
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

    private static async Task SafeDeleteDirectoryAsync(string path)
    {
        if (!Directory.Exists(path)) return;
        foreach (var file in Directory.GetFiles(path, "*", SearchOption.AllDirectories))
        {
            try { File.SetAttributes(file, FileAttributes.Normal); } catch { }
        }
        for (int i = 0; i < 3; i++)
        {
            try { Directory.Delete(path, true); return; }
            catch (UnauthorizedAccessException) when (i < 2) { await Task.Delay(500); }
            catch (IOException) when (i < 2) { await Task.Delay(500); }
        }
    }
}
