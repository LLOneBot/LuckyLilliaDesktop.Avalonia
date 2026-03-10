using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using LuckyLilliaDesktop.Models;
using LuckyLilliaDesktop.Utils;
using Microsoft.Extensions.Logging;

namespace LuckyLilliaDesktop.Services;

public interface IOpenClawInstallService
{
    Task<bool> InstallAsync(IProgress<InstallProgress>? progress = null, CancellationToken ct = default);
    bool IsInstalled { get; }
    bool IsFirstRun { get; }
    void StartOnboard();
    void StartGateway();
    void EnsureOpenClawConfigured(int wsPort = 3001);

    /// <summary>
    /// 启动 onboard 并监控进程退出，退出码为 0 时触发 onComplete
    /// </summary>
    void StartOnboardAndWatch(Action onComplete);
}

public class OpenClawInstallService : IOpenClawInstallService
{
    private readonly ILogger<OpenClawInstallService> _logger;
    private readonly IGitHubArchiveHelper _archiveHelper;
    private readonly IGitHubHelper _gitHubHelper;

    private const string Node24Dir = "bin/node24";
    private const string NodeVersion = "v24.4.1";

    private static string OpenClawExtDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".openclaw", "extensions", "openclaw_qq");

    private static string OpenClawConfigDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".openclaw");

    public bool IsInstalled
    {
        get
        {
            // 检查 openclaw_qq 扩展是否存在
            var packageJson = Path.Combine(OpenClawExtDir, "package.json");
            return Directory.Exists(OpenClawExtDir) && File.Exists(packageJson);
        }
    }

    public bool IsFirstRun
    {
        get
        {
            var openclawJson = Path.Combine(OpenClawConfigDir, "openclaw.json");
            return !File.Exists(openclawJson);
        }
    }

    public OpenClawInstallService(ILogger<OpenClawInstallService> logger,
        IGitHubArchiveHelper archiveHelper, IGitHubHelper gitHubHelper)
    {
        _logger = logger;
        _archiveHelper = archiveHelper;
        _gitHubHelper = gitHubHelper;
    }

    public async Task<bool> InstallAsync(IProgress<InstallProgress>? progress = null, CancellationToken ct = default)
    {
        const int totalSteps = 4;
        try
        {
            // Step 1: 确保 Node.js 24 已安装
            var nodeExeName = PlatformHelper.IsWindows ? "node.exe" : "node";
            if (!File.Exists(Path.Combine(Node24Dir, nodeExeName)))
            {
                Report(progress, 1, totalSteps, "下载 Node.js 24", "正在下载...");
                if (!await DownloadNodeAsync(progress, 1, totalSteps, ct))
                    return false;
            }
            else
            {
                Report(progress, 1, totalSteps, "检查 Node.js 24", "Node.js 24 已存在，跳过下载");
            }

            // Step 2: 安装 openclaw CLI
            Report(progress, 2, totalSteps, "安装 OpenClaw", "正在安装 openclaw...");
            var npmCmd = PlatformHelper.IsWindows
                ? Path.GetFullPath(Path.Combine(Node24Dir, "npm.cmd"))
                : Path.GetFullPath(Path.Combine(Node24Dir, "bin", "npm"));

            if (!await RunCommandAsync(npmCmd, "install -g openclaw@latest --registry=https://registry.npmmirror.com",
                Path.GetFullPath(Node24Dir),
                line => Report(progress, 2, totalSteps, "安装 OpenClaw", line), ct))
            {
                ReportError(progress, 2, totalSteps, "安装 openclaw 失败");
                return false;
            }
            _logger.LogInformation("openclaw CLI 安装完成");

            // Step 3: 下载 openclaw_qq 扩展
            Report(progress, 3, totalSteps, "下载 openclaw_qq", "正在下载扩展...");
            var extensionsDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".openclaw", "extensions");
            Directory.CreateDirectory(extensionsDir);

            if (!await _archiveHelper.DownloadAndExtractAsync("constansino/openclaw_qq", "main", OpenClawExtDir,
                (downloaded, total) => Report(progress, 3, totalSteps, "下载 openclaw_qq",
                    $"正在下载... {downloaded / 1024 / 1024:F1} MB / {total / 1024 / 1024:F1} MB",
                    total > 0 ? (double)downloaded / total * 100 : 0), ct))
            {
                ReportError(progress, 3, totalSteps, "下载 openclaw_qq 失败");
                return false;
            }

            // Step 4: 在 openclaw_qq 目录执行 npm install
            Report(progress, 4, totalSteps, "安装扩展依赖", "正在安装 openclaw_qq 依赖...");
            if (!await RunCommandAsync(npmCmd, "install --registry=https://registry.npmmirror.com",
                OpenClawExtDir,
                line => Report(progress, 4, totalSteps, "安装扩展依赖", line), ct))
            {
                ReportError(progress, 4, totalSteps, "安装 openclaw_qq 依赖失败");
                return false;
            }

            Report(progress, totalSteps, totalSteps, "完成", "OpenClaw 安装完成", 100, true);
            _logger.LogInformation("OpenClaw 安装完成");
            return true;
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("OpenClaw 安装已取消");
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "OpenClaw 安装失败");
            ReportError(progress, 0, totalSteps, ex.Message);
            return false;
        }
    }

    /// <summary>
    /// 首次启动：弹出终端执行 openclaw onboard --install-daemon，用户交互
    /// </summary>
    public void StartOnboard()
    {
        LaunchOnboardProcess(null);
    }

    /// <summary>
    /// 启动 onboard 并监控进程退出，退出码为 0 时触发 onComplete
    /// </summary>
    public void StartOnboardAndWatch(Action onComplete)
    {
        LaunchOnboardProcess(onComplete);
    }

    private void LaunchOnboardProcess(Action? onComplete)
    {
        try
        {
            var openclawCmd = GetOpenClawCommand();
            Process? process = null;

            if (PlatformHelper.IsWindows)
            {
                // 用 /c 使 cmd 在 onboard 结束后自动退出，以便监听退出码
                process = Process.Start(new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = $"/c \"\"{openclawCmd}\" onboard --install-daemon\"",
                    UseShellExecute = true
                });
            }
            else if (PlatformHelper.IsMacOS)
            {
                // macOS: 通过 osascript 启动 Terminal 运行命令
                // Terminal.app 进程无法直接监控子命令退出，用包装脚本写退出码到临时文件
                if (onComplete != null)
                {
                    var exitFlagFile = Path.Combine(Path.GetTempPath(), $"openclaw_onboard_{Environment.ProcessId}.exit");
                    var script = $"tell application \\\"Terminal\\\" to do script \\\"{openclawCmd} onboard --install-daemon; echo $? > {exitFlagFile}\\\"";
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "osascript",
                        Arguments = $"-e \"{script}\"",
                        UseShellExecute = false,
                        CreateNoWindow = true
                    });
                    // 轮询退出码文件
                    _ = PollExitFlagAsync(exitFlagFile, onComplete);
                }
                else
                {
                    var script = $"tell application \\\"Terminal\\\" to do script \\\"{openclawCmd} onboard --install-daemon\\\"";
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "osascript",
                        Arguments = $"-e \"{script}\"",
                        UseShellExecute = false,
                        CreateNoWindow = true
                    });
                }
            }
            else
            {
                process = Process.Start(new ProcessStartInfo
                {
                    FileName = "xterm",
                    Arguments = $"-e \"{openclawCmd} onboard --install-daemon\"",
                    UseShellExecute = true
                });
            }

            if (process != null && onComplete != null)
            {
                process.EnableRaisingEvents = true;
                process.Exited += (_, _) =>
                {
                    _logger.LogInformation("onboard 进程已退出，退出码: {ExitCode}", process.ExitCode);
                    if (process.ExitCode == 0)
                    {
                        onComplete();
                    }
                    else
                    {
                        _logger.LogWarning("onboard 进程退出码非 0，不触发自动配置");
                    }
                    process.Dispose();
                };
            }

            _logger.LogInformation("OpenClaw onboard 已启动");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "启动 OpenClaw onboard 失败");
        }
    }

    /// <summary>
    /// macOS 下轮询退出码文件，检测 onboard 完成
    /// </summary>
    private async Task PollExitFlagAsync(string exitFlagFile, Action onComplete)
    {
        try
        {
            // 最长等待 30 分钟
            for (var i = 0; i < 360; i++)
            {
                await Task.Delay(5000);
                if (!File.Exists(exitFlagFile)) continue;

                var content = (await File.ReadAllTextAsync(exitFlagFile)).Trim();
                try { File.Delete(exitFlagFile); } catch { }

                if (content == "0")
                {
                    _logger.LogInformation("macOS onboard 完成，退出码: 0");
                    onComplete();
                }
                else
                {
                    _logger.LogWarning("macOS onboard 退出码: {Code}，不触发自动配置", content);
                }
                return;
            }
            _logger.LogWarning("等待 macOS onboard 完成超时");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "轮询 macOS onboard 退出码失败");
        }
    }

    /// <summary>
    /// 非首次启动：openclaw gateway run
    /// </summary>
    public void StartGateway()
    {
        try
        {
            var wsPort = FindLLBotForwardWsPort();
            EnsureOpenClawConfigured(wsPort);

            var openclawCmd = GetOpenClawCommand();

            if (PlatformHelper.IsWindows)
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = $"/k \"\"{openclawCmd}\" gateway run\"",
                    UseShellExecute = true
                });
            }
            else if (PlatformHelper.IsMacOS)
            {
                var script = $"tell application \\\"Terminal\\\" to do script \\\"{openclawCmd} gateway run\\\"";
                Process.Start(new ProcessStartInfo
                {
                    FileName = "osascript",
                    Arguments = $"-e \"{script}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true
                });
            }
            else
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "xterm",
                    Arguments = $"-e \"{openclawCmd} gateway run && read -p 'Press enter to exit...'\"",
                    UseShellExecute = true
                });
            }

            _logger.LogInformation("OpenClaw gateway 已启动");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "启动 OpenClaw gateway 失败");
        }
    }

    /// <summary>
    /// 确保 openclaw.json 中包含 QQ 频道和插件配置
    /// </summary>
    public void EnsureOpenClawConfigured(int wsPort = 3001)
    {
        var configPath = Path.Combine(OpenClawConfigDir, "openclaw.json");
        if (!File.Exists(configPath))
        {
            _logger.LogWarning("openclaw.json 不存在，跳过配置");
            return;
        }

        try
        {
            var json = File.ReadAllText(configPath);
            var node = JsonNode.Parse(json) ?? new JsonObject();
            var root = node.AsObject();
            var changed = false;

            // 确保 channels.qq 存在
            if (!root.ContainsKey("channels"))
            {
                root["channels"] = new JsonObject();
                changed = true;
            }
            var channels = root["channels"]!.AsObject();
            if (!channels.ContainsKey("qq"))
            {
                channels["qq"] = new JsonObject
                {
                    ["wsUrl"] = $"ws://127.0.0.1:{wsPort}",
                    ["accessToken"] = "",
                    ["requireMention"] = true
                };
                changed = true;
                _logger.LogInformation("已添加 openclaw.json channels.qq 配置");
            }

            // 确保 plugins.entries.qq 存在
            if (!root.ContainsKey("plugins"))
            {
                root["plugins"] = new JsonObject();
                changed = true;
            }
            var plugins = root["plugins"]!.AsObject();
            if (!plugins.ContainsKey("entries"))
            {
                plugins["entries"] = new JsonObject();
                changed = true;
            }
            var entries = plugins["entries"]!.AsObject();
            if (!entries.ContainsKey("qq"))
            {
                entries["qq"] = new JsonObject { ["enabled"] = true };
                changed = true;
                _logger.LogInformation("已添加 openclaw.json plugins.entries.qq 配置");
            }

            if (changed)
            {
                File.WriteAllText(configPath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                _logger.LogInformation("openclaw.json 配置已更新");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "配置 openclaw.json 失败");
        }
    }

    /// <summary>
    /// 从 LLBot 配置中查找正向 WS 端口：
    /// 1. 优先找名为 OpenClaw 的 ws → 返回其端口
    /// 2. 其次找已启用的 ws → 返回其端口
    /// 3. 都没有则返回默认 3001
    /// </summary>
    private int FindLLBotForwardWsPort()
    {
        try
        {
            var dataDir = Path.Combine("bin", "llbot", "data");
            if (!Directory.Exists(dataDir)) return 3001;

            // 查找 config_*.json 文件
            var configFiles = Directory.GetFiles(dataDir, "config_*.json");
            if (configFiles.Length == 0) return 3001;

            var json = File.ReadAllText(configFiles[0]);
            var config = JsonSerializer.Deserialize<LLBotConfig>(json);
            if (config == null) return 3001;

            // 1. 查找名为 OpenClaw 的正向 ws
            var openclawWs = config.OB11.Connect.FirstOrDefault(c =>
                c.Type == "ws" && string.Equals(c.Name, "OpenClaw", StringComparison.OrdinalIgnoreCase));
            if (openclawWs != null)
            {
                _logger.LogInformation("找到 OpenClaw WS 配置，端口: {Port}", openclawWs.Port);
                return openclawWs.Port;
            }

            // 2. 查找已启用的正向 ws
            var enabledWs = config.OB11.Connect.FirstOrDefault(c =>
                c.Type == "ws" && c.Enable);
            if (enabledWs != null)
            {
                _logger.LogInformation("复用已启用的正向 WS，端口: {Port}", enabledWs.Port);
                return enabledWs.Port;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "读取 LLBot 配置失败，使用默认端口");
        }

        return 3001;
    }

    private string GetOpenClawCommand()
    {
        // openclaw 通过 npm install -g 安装到 node24 的 bin 目录
        if (PlatformHelper.IsWindows)
            return Path.GetFullPath(Path.Combine(Node24Dir, "openclaw.cmd"));
        else
            return Path.GetFullPath(Path.Combine(Node24Dir, "bin", "openclaw"));
    }

    private async Task<bool> DownloadNodeAsync(IProgress<InstallProgress>? progress, int step, int totalSteps, CancellationToken ct)
    {
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
            var arch = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture ==
                       System.Runtime.InteropServices.Architecture.Arm64 ? "arm64" : "x64";
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

        string[] nodeUrls =
        [
            $"https://npmmirror.com/mirrors/node/{NodeVersion}/{nodeArchive}",
            $"https://cdn.npmmirror.com/binaries/node/{NodeVersion}/{nodeArchive}",
            $"https://nodejs.org/dist/{NodeVersion}/{nodeArchive}"
        ];

        var downloadSuccess = await _gitHubHelper.DownloadWithFallbackAsync(nodeUrls, nodeArchivePath,
            (downloaded, total) => Report(progress, step, totalSteps, "下载 Node.js 24",
                $"正在下载... {downloaded / 1024 / 1024:F1} MB / {total / 1024 / 1024:F1} MB",
                total > 0 ? (double)downloaded / total * 100 : 0), ct);

        if (!downloadSuccess)
        {
            ReportError(progress, step, totalSteps, "下载 Node.js 24 失败");
            return false;
        }

        Report(progress, step, totalSteps, "解压 Node.js 24", "正在解压...");
        var tempExtract = Path.Combine(Path.GetTempPath(), "node24-extract");
        await SafeDeleteDirectoryAsync(tempExtract);
        Directory.CreateDirectory(tempExtract);

        if (PlatformHelper.IsWindows)
        {
            await Task.Run(() => System.IO.Compression.ZipFile.ExtractToDirectory(nodeArchivePath, tempExtract, true), ct)
                .ConfigureAwait(false);
        }
        else if (PlatformHelper.IsMacOS)
        {
            var tarPsi = new ProcessStartInfo
            {
                FileName = "tar",
                Arguments = $"-xzf \"{nodeArchivePath}\" -C \"{tempExtract}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true
            };
            using var tarProc = Process.Start(tarPsi);
            if (tarProc != null)
            {
                await tarProc.WaitForExitAsync(ct);
                if (tarProc.ExitCode != 0)
                {
                    ReportError(progress, step, totalSteps, "解压 Node.js 24 失败");
                    return false;
                }
            }
        }
        else
        {
            var tarPsi = new ProcessStartInfo
            {
                FileName = "tar",
                Arguments = $"-xJf \"{nodeArchivePath}\" -C \"{tempExtract}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true
            };
            using var tarProc = Process.Start(tarPsi);
            if (tarProc != null)
            {
                await tarProc.WaitForExitAsync(ct);
                if (tarProc.ExitCode != 0)
                {
                    ReportError(progress, step, totalSteps, "解压 Node.js 24 失败");
                    return false;
                }
            }
        }

        await Task.Run(() => File.Delete(nodeArchivePath), ct).ConfigureAwait(false);

        await SafeDeleteDirectoryAsync(Node24Dir);
        var extractedDir = Path.Combine(tempExtract, extractedDirName);
        await Task.Run(() => CopyDirectory(extractedDir, Node24Dir), ct).ConfigureAwait(false);
        await SafeDeleteDirectoryAsync(tempExtract);
        _logger.LogInformation("Node.js 24 解压完成");
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
