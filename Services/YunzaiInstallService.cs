using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Threading;
using System.Threading.Tasks;
using LuckyLilliaDesktop.Utils;
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
    private readonly IGitHubArchiveHelper _archiveHelper;
    private readonly IGitHubHelper _gitHubHelper;
    private readonly IGitHubCLIHelper _gitCLIHelper;

    private const string YunzaiDir = "bin/yunzai";
    private const string Node24Dir = "bin/node24";
    private const string RedisDir = "bin/redis";
    private const string NodeVersion = "v24.4.1";
    private const string RedisVersion = "7.4.7";

    public bool IsInstalled => File.Exists(Path.Combine(YunzaiDir, "package.json"));
    public string YunzaiPath => Path.GetFullPath(YunzaiDir);
    public string Node24Path => Path.GetFullPath(Node24Dir);
    public string RedisPath => Path.GetFullPath(RedisDir);

    public YunzaiInstallService(ILogger<YunzaiInstallService> logger, 
        IGitHubArchiveHelper archiveHelper, IGitHubHelper gitHubHelper, IGitHubCLIHelper gitCLIHelper)
    {
        _logger = logger;
        _archiveHelper = archiveHelper;
        _gitHubHelper = gitHubHelper;
        _gitCLIHelper = gitCLIHelper;
    }

    public async Task<bool> InstallAsync(IProgress<InstallProgress>? progress = null, CancellationToken ct = default)
    {
        const int totalSteps = 11;
        try
        {
            // Step 1: 下载 Redis（如果不存在）
            var redisExeName = PlatformHelper.IsWindows ? "redis-server.exe" : "redis-server";
            if (!File.Exists(Path.Combine(RedisDir, redisExeName)))
            {
                Report(progress, 1, totalSteps, "下载 Redis", "正在下载...");

                string redisFileName;
                string[] redisUrls;
                string extractedDirName;

                if (PlatformHelper.IsWindows)
                {
                    redisFileName = $"Redis-{RedisVersion}-Windows-x64-msys2.zip";
                    redisUrls = [
                        $"https://gh-proxy.com/https://github.com/redis-windows/redis-windows/releases/download/{RedisVersion}/{redisFileName}",
                        $"https://ghproxy.net/https://github.com/redis-windows/redis-windows/releases/download/{RedisVersion}/{redisFileName}",
                        $"https://github.com/redis-windows/redis-windows/releases/download/{RedisVersion}/{redisFileName}"
                    ];
                    extractedDirName = $"Redis-{RedisVersion}-Windows-x64-msys2";
                }
                else if (PlatformHelper.IsMacOS)
                {
                    // macOS ARM64 或 x64
                    var arch = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture == System.Runtime.InteropServices.Architecture.Arm64 ? "arm64" : "x86_64";
                    redisFileName = $"redis-{RedisVersion}-macos-{arch}.tar.gz";
                    redisUrls = [
                        $"https://github.com/redis/redis/archive/refs/tags/{RedisVersion}.tar.gz"
                    ];
                    extractedDirName = $"redis-{RedisVersion}";
                }
                else
                {
                    // Linux
                    redisFileName = $"redis-{RedisVersion}-linux-x64.tar.gz";
                    redisUrls = [
                        $"https://github.com/redis/redis/archive/refs/tags/{RedisVersion}.tar.gz"
                    ];
                    extractedDirName = $"redis-{RedisVersion}";
                }

                var redisArchive = Path.Combine(Path.GetTempPath(), redisFileName);

                var downloadSuccess = await _gitHubHelper.DownloadWithFallbackAsync(redisUrls, redisArchive,
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

                if (PlatformHelper.IsWindows)
                {
                    await Task.Run(() => ZipFile.ExtractToDirectory(redisArchive, tempExtract, true), ct).ConfigureAwait(false);
                }
                else
                {
                    // macOS/Linux 使用 tar 解压
                    var tarPsi = new ProcessStartInfo
                    {
                        FileName = "tar",
                        Arguments = $"-xzf \"{redisArchive}\" -C \"{tempExtract}\"",
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };
                    using var tarProc = Process.Start(tarPsi);
                    if (tarProc != null)
                    {
                        await tarProc.WaitForExitAsync(ct);
                    }
                }

                await Task.Run(() => File.Delete(redisArchive), ct).ConfigureAwait(false);

                await SafeDeleteDirectoryAsync(RedisDir);
                var extractedDir = Path.Combine(tempExtract, extractedDirName);
                await Task.Run(() => CopyDirectory(extractedDir, RedisDir), ct).ConfigureAwait(false);
                await SafeDeleteDirectoryAsync(tempExtract);

                // macOS/Linux 需要编译 Redis
                if (!PlatformHelper.IsWindows)
                {
                    Report(progress, 1, totalSteps, "编译 Redis", "正在编译...");
                    var makePsi = new ProcessStartInfo
                    {
                        FileName = "make",
                        WorkingDirectory = RedisDir,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true
                    };
                    using var makeProc = Process.Start(makePsi);
                    if (makeProc != null)
                    {
                        await makeProc.WaitForExitAsync(ct);
                        if (makeProc.ExitCode != 0)
                        {
                            ReportError(progress, 1, totalSteps, "编译 Redis 失败");
                            return false;
                        }
                    }
                }

                _logger.LogInformation("Redis 安装完成");
            }
            else
            {
                Report(progress, 1, totalSteps, "检查 Redis", "Redis 已存在，跳过下载");
            }

            // Step 2: 下载 Node.js 24（如果不存在）
            var nodeExeName = PlatformHelper.IsWindows ? "node.exe" : "node";
            if (!File.Exists(Path.Combine(Node24Dir, nodeExeName)))
            {
                Report(progress, 2, totalSteps, "下载 Node.js 24", "正在下载...");

                string nodePlatform;
                string nodeArchive;
                string extractedDirName;

                if (PlatformHelper.IsWindows)
                {
                    nodePlatform = "win-x64";
                    nodeArchive = $"node-{NodeVersion}-{nodePlatform}.zip";
                    extractedDirName = $"node-{NodeVersion}-{nodePlatform}";
                }
                else if (PlatformHelper.IsMacOS)
                {
                    var arch = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture == System.Runtime.InteropServices.Architecture.Arm64 ? "arm64" : "x64";
                    nodePlatform = $"darwin-{arch}";
                    nodeArchive = $"node-{NodeVersion}-{nodePlatform}.tar.gz";
                    extractedDirName = $"node-{NodeVersion}-{nodePlatform}";
                }
                else
                {
                    nodePlatform = "linux-x64";
                    nodeArchive = $"node-{NodeVersion}-{nodePlatform}.tar.xz";
                    extractedDirName = $"node-{NodeVersion}-{nodePlatform}";
                }

                var nodeArchivePath = Path.Combine(Path.GetTempPath(), nodeArchive);

                string[] nodeUrls = [
                    $"https://npmmirror.com/mirrors/node/{NodeVersion}/{nodeArchive}",
                    $"https://cdn.npmmirror.com/binaries/node/{NodeVersion}/{nodeArchive}",
                    $"https://nodejs.org/dist/{NodeVersion}/{nodeArchive}"
                ];

                var downloadSuccess = await _gitHubHelper.DownloadWithFallbackAsync(nodeUrls, nodeArchivePath,
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

                if (PlatformHelper.IsWindows)
                {
                    await Task.Run(() => ZipFile.ExtractToDirectory(nodeArchivePath, tempExtract, true), ct).ConfigureAwait(false);
                }
                else if (PlatformHelper.IsMacOS)
                {
                    // macOS 使用 tar.gz
                    var tarPsi = new ProcessStartInfo
                    {
                        FileName = "tar",
                        Arguments = $"-xzf \"{nodeArchivePath}\" -C \"{tempExtract}\"",
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };
                    using var tarProc = Process.Start(tarPsi);
                    if (tarProc != null)
                    {
                        await tarProc.WaitForExitAsync(ct);
                    }
                }
                else
                {
                    // Linux 使用 tar.xz
                    var tarPsi = new ProcessStartInfo
                    {
                        FileName = "tar",
                        Arguments = $"-xJf \"{nodeArchivePath}\" -C \"{tempExtract}\"",
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };
                    using var tarProc = Process.Start(tarPsi);
                    if (tarProc != null)
                    {
                        await tarProc.WaitForExitAsync(ct);
                    }
                }

                await Task.Run(() => File.Delete(nodeArchivePath), ct).ConfigureAwait(false);

                // 复制到目标目录（跨盘符不能用 Move）
                await SafeDeleteDirectoryAsync(Node24Dir);
                var extractedDir = Path.Combine(tempExtract, extractedDirName);
                await Task.Run(() => CopyDirectory(extractedDir, Node24Dir), ct).ConfigureAwait(false);
                await SafeDeleteDirectoryAsync(tempExtract);
                _logger.LogInformation("Node.js 24 解压完成");
            }
            else
            {
                Report(progress, 2, totalSteps, "检查 Node.js 24", "Node.js 24 已存在，跳过下载");
            }

            // Step 3: 下载云崽源码
            Report(progress, 3, totalSteps, "下载云崽", "正在下载源码...");
            if (!await _archiveHelper.DownloadAndExtractAsync("TimeRainStarSky/Yunzai", "main", YunzaiDir,
                (downloaded, total) => Report(progress, 3, totalSteps, "下载云崽",
                    $"正在下载... {downloaded / 1024 / 1024:F1} MB / {total / 1024 / 1024:F1} MB",
                    total > 0 ? (double)downloaded / total * 100 : 0), ct))
            {
                ReportError(progress, 3, totalSteps, "下载云崽失败");
                return false;
            }

            // Step 4: 安装 pnpm
            Report(progress, 4, totalSteps, "安装 pnpm", "正在安装 pnpm...");
            var nodeExe = Path.GetFullPath(Path.Combine(Node24Dir, PlatformHelper.IsWindows ? "node.exe" : "bin/node"));
            var npmCli = PlatformHelper.IsWindows
                ? Path.GetFullPath(Path.Combine(Node24Dir, "node_modules", "npm", "bin", "npm-cli.js"))
                : Path.GetFullPath(Path.Combine(Node24Dir, "lib", "node_modules", "npm", "bin", "npm-cli.js"));

            if (!await RunCommandAsync(nodeExe, $"\"{npmCli}\" install -g pnpm --registry=https://registry.npmmirror.com",
                Node24Path, line => Report(progress, 4, totalSteps, "安装 pnpm", line), ct))
            {
                ReportError(progress, 4, totalSteps, "安装 pnpm 失败");
                return false;
            }

            // Step 5: 安装云崽依赖
            Report(progress, 5, totalSteps, "安装依赖", "正在安装云崽依赖...");
            var pnpmCmd = PlatformHelper.IsWindows
                ? Path.GetFullPath(Path.Combine(Node24Dir, "pnpm.cmd"))
                : Path.GetFullPath(Path.Combine(Node24Dir, "bin", "pnpm"));

            if (!await RunCommandAsync(pnpmCmd, "install --registry=https://registry.npmmirror.com",
                YunzaiPath, line => Report(progress, 5, totalSteps, "安装依赖", line), ct))
            {
                ReportError(progress, 5, totalSteps, "安装依赖失败");
                return false;
            }

            var npmCmd = PlatformHelper.IsWindows
                ? Path.GetFullPath(Path.Combine(Node24Dir, "npm.cmd"))
                : Path.GetFullPath(Path.Combine(Node24Dir, "bin", "npm"));

            // Step 6-7: 安装 TRSS-Plugin
            if (!await InstallPluginAsync("TRSS-Plugin", "TimeRainStarSky/TRSS-Plugin", "main", 
                6, totalSteps, progress, npmCmd, ct))
                return false;

            // Step 8-9: 安装 neko-status-plugin
            if (!await InstallPluginAsync("neko-status-plugin", "erzaozi/neko-status-plugin", "main",
                8, totalSteps, progress, npmCmd, ct))
                return false;

            // Step 10-11: 安装 miao-plugin
            if (!await InstallPluginAsync("miao-plugin", "yoimiya-kokomi/miao-plugin", "master",
                10, totalSteps, progress, npmCmd, ct))
                return false;

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
            var redisExe = Path.GetFullPath(Path.Combine(RedisDir, PlatformHelper.IsWindows ? "redis-server.exe" : "src/redis-server"));
            if (File.Exists(redisExe))
            {
                var redisProcs = Process.GetProcessesByName("redis-server");
                if (redisProcs.Length == 0)
                {
                    // 确保数据目录存在，解决 RDB 持久化问题
                    var dataDir = Path.GetFullPath(Path.Combine(RedisDir, "data"));
                    Directory.CreateDirectory(dataDir);

                    if (PlatformHelper.IsWindows)
                    {
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
                    }
                    else
                    {
                        // macOS/Linux
                        Process.Start(new ProcessStartInfo
                        {
                            FileName = redisExe,
                            Arguments = $"--dir \"{dataDir}\"",
                            WorkingDirectory = RedisPath,
                            UseShellExecute = false,
                            CreateNoWindow = true
                        });
                    }
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
            var nodeExe = Path.GetFullPath(Path.Combine(Node24Dir, PlatformHelper.IsWindows ? "node.exe" : "bin/node"));

            if (!File.Exists(nodeExe))
            {
                _logger.LogError("Node.js 24 未安装");
                return;
            }

            if (PlatformHelper.IsWindows)
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = $"/k \"\"{nodeExe}\" . & pause\"",
                    WorkingDirectory = yunzaiPath,
                    UseShellExecute = true
                });
            }
            else
            {
                // macOS/Linux 启动终端
                var terminalCmd = PlatformHelper.IsMacOS ? "open" : "xterm";
                var terminalArgs = PlatformHelper.IsMacOS
                    ? $"-a Terminal \"{yunzaiPath}\" --args \"{nodeExe}\" ."
                    : $"-e \"{nodeExe} . && read -p 'Press enter to exit...'\"";

                Process.Start(new ProcessStartInfo
                {
                    FileName = terminalCmd,
                    Arguments = terminalArgs,
                    WorkingDirectory = yunzaiPath,
                    UseShellExecute = true
                });
            }
            _logger.LogInformation("云崽已启动");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "启动云崽失败");
        }
    }

    private async Task<bool> InstallPluginAsync(string pluginName, string repoPath, string branch,
        int startStep, int totalSteps, IProgress<InstallProgress>? progress, string npmCmd, CancellationToken ct)
    {
        var stepDownload = startStep;
        var stepInstall = startStep + 1;
        var pluginDir = Path.Combine(YunzaiDir, "plugins", pluginName);

        // 尝试使用 Git 克隆（优先级：GitHub > ZIP）
        Report(progress, stepDownload, totalSteps, $"下载 {pluginName}", "正在尝试使用 Git 克隆...");
        
        // 尝试从 GitHub 克隆
        if (_gitCLIHelper.IsGitAvailable())
        {
            _logger.LogInformation("尝试从 GitHub 克隆 {PluginName}", pluginName);
            if (await _gitCLIHelper.CloneFromGitHubAsync(repoPath, pluginDir, branch, ct))
            {
                _logger.LogInformation("从 GitHub 克隆 {PluginName} 成功", pluginName);
                await InstallPluginDependenciesAsync(pluginName, pluginDir, stepInstall, totalSteps, progress, npmCmd, ct);
                return true;
            }
            _logger.LogWarning("从 GitHub 克隆 {PluginName} 失败，尝试下载 ZIP", pluginName);
        }

        // 回退到下载 ZIP
        Report(progress, stepDownload, totalSteps, $"下载 {pluginName}", "正在下载 ZIP 包...");
        if (!await _archiveHelper.DownloadAndExtractAsync(repoPath, branch, pluginDir,
            (downloaded, total) => Report(progress, stepDownload, totalSteps, $"下载 {pluginName}",
                $"正在下载... {downloaded / 1024 / 1024:F1} MB / {total / 1024 / 1024:F1} MB",
                total > 0 ? (double)downloaded / total * 100 : 0), ct))
        {
            ReportError(progress, stepDownload, totalSteps, $"下载 {pluginName} 失败");
            return false;
        }

        await InstallPluginDependenciesAsync(pluginName, pluginDir, stepInstall, totalSteps, progress, npmCmd, ct);
        return true;
    }

    private async Task<bool> InstallPluginDependenciesAsync(string pluginName, string pluginDir,
        int step, int totalSteps, IProgress<InstallProgress>? progress, string npmCmd, CancellationToken ct)
    {
        Report(progress, step, totalSteps, "安装插件依赖", $"正在安装 {pluginName} 依赖...");
        
        if (!await RunCommandAsync(npmCmd, "install --registry=https://registry.npmmirror.com",
            pluginDir, line => Report(progress, step, totalSteps, "安装插件依赖", line), ct))
        {
            ReportError(progress, step, totalSteps, $"安装 {pluginName} 依赖失败");
            return false;
        }

        return true;
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
