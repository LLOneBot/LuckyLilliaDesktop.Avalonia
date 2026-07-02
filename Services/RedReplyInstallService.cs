using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using LuckyLilliaDesktop.Models;
using LuckyLilliaDesktop.Utils;
using Microsoft.Extensions.Logging;

namespace LuckyLilliaDesktop.Services;

public interface IRedReplyInstallService
{
    Task<bool> InstallAsync(IProgress<InstallProgress>? progress = null, CancellationToken ct = default);
    bool IsInstalled { get; }
    string RedReplyPath { get; }
    string EnsureOneBotWebSocketUrl(string? currentUin);
    void StartRedReply(bool openWebUiIfRunning = true, bool openWebUiOnStart = true, string? oneBotWsUrl = null);
}

public class RedReplyInstallService : IRedReplyInstallService
{
    private readonly ILogger<RedReplyInstallService> _logger;
    private readonly IGitHubHelper _gitHubHelper;

    private const string RedReplyDir = "bin/redreply";
    private const int DefaultWebPort = 1207;
    public bool IsInstalled => File.Exists(Path.Combine(RedReplyDir, GetExeFileName()));
    public string RedReplyPath => Path.GetFullPath(RedReplyDir);

    public RedReplyInstallService(ILogger<RedReplyInstallService> logger, IGitHubHelper gitHubHelper)
    {
        _logger = logger;
        _gitHubHelper = gitHubHelper;
    }

    public async Task<bool> InstallAsync(IProgress<InstallProgress>? progress = null, CancellationToken ct = default)
    {
        const int totalSteps = 3;
        var tempFile = Path.Combine(Path.GetTempPath(), GetDownloadFileName());

        try
        {
            var downloadFileName = GetDownloadFileName();
            if (string.IsNullOrEmpty(downloadFileName))
            {
                ReportError(progress, 1, totalSteps, "当前平台暂无 redreply 预编译包");
                return false;
            }

            var exePath = Path.Combine(RedReplyDir, GetExeFileName());
            await StopRedReplyIfRunningAsync(Path.GetFullPath(exePath), ct);

            Report(progress, 1, totalSteps, "获取版本信息", "正在获取 redreply 最新版本...");
            var tag = await _gitHubHelper.GetLatestTagAsync("super1207", "redreply", ct);
            if (string.IsNullOrEmpty(tag))
            {
                ReportError(progress, 1, totalSteps, "无法获取 redreply 最新版本信息");
                return false;
            }
            _logger.LogInformation("获取到 redreply 最新版本: {Tag}", tag);

            Report(progress, 2, totalSteps, "下载 redreply", "正在下载...");
            var downloadUrls = _gitHubHelper.GetGitHubUrlsWithProxy(
                $"https://github.com/super1207/redreply/releases/download/{tag}/{downloadFileName}");

            var downloadSuccess = await _gitHubHelper.DownloadWithFallbackAsync(downloadUrls, tempFile,
                (downloaded, total) => Report(progress, 2, totalSteps, "下载 redreply",
                    $"正在下载... {downloaded / 1024 / 1024:F1} MB / {total / 1024 / 1024:F1} MB",
                    total > 0 ? (double)downloaded / total * 100 : 0), ct);

            if (!downloadSuccess)
            {
                ReportError(progress, 2, totalSteps, "下载 redreply 失败");
                return false;
            }

            Report(progress, 3, totalSteps, "安装文件", "正在写入文件...");
            Directory.CreateDirectory(RedReplyDir);
            File.Copy(tempFile, exePath, true);

            if (!PlatformHelper.IsWindows)
                await ChmodExecutableAsync(exePath, ct);

            Report(progress, totalSteps, totalSteps, "完成", "redreply 安装完成", 100, true);
            _logger.LogInformation("redreply 安装完成: {Path}", exePath);
            return true;
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("redreply 安装已取消");
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "redreply 安装失败");
            ReportError(progress, 0, totalSteps, ex.Message);
            return false;
        }
        finally
        {
            try
            {
                if (File.Exists(tempFile))
                    File.Delete(tempFile);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "删除 redreply 临时下载文件失败: {Path}", tempFile);
            }
        }
    }

    private void ConfigureOneBot(string wsUrl, bool openWebUiOnStart)
    {
        var plusDir = Path.Combine(RedReplyDir, "plus_dir");
        Directory.CreateDirectory(plusDir);

        var configPath = Path.Combine(plusDir, "config.json");
        var config = ReadConfig(configPath);

        SetDefault(config, "web_port", DefaultWebPort);
        SetDefault(config, "web_password", "");
        SetDefault(config, "readonly_web_password", "");
        SetDefault(config, "web_host", "127.0.0.1");
        config["not_open_browser"] = !openWebUiOnStart;

        if (config["ws_urls"] is not JsonArray wsUrls)
        {
            wsUrls = new JsonArray();
            config["ws_urls"] = wsUrls;
        }

        RemoveStaleDefaultWsUrl(wsUrls, wsUrl);
        if (!ContainsString(wsUrls, wsUrl))
            ((System.Collections.Generic.IList<JsonNode?>)wsUrls).Add(JsonValue.Create(wsUrl));

        File.WriteAllText(configPath, config.ToJsonString(AppJsonContext.Default.Options));

        _logger.LogInformation("redreply OneBot11 正向 WS 已配置: {Url}", wsUrl);
    }

    public string EnsureOneBotWebSocketUrl(string? currentUin)
    {
        const string fallbackUrl = "ws://127.0.0.1:3001";

        if (string.IsNullOrEmpty(currentUin)) return fallbackUrl;

        var configPath = Path.Combine("bin", "llbot", "data", $"config_{currentUin}.json");
        if (!File.Exists(configPath)) return fallbackUrl;

        try
        {
            var json = File.ReadAllText(configPath);
            var config = JsonSerializer.Deserialize(json, AppJsonContext.Default.LLBotConfig) ?? LLBotConfig.Default;
            var changed = false;

            var redReplyWs = config.OB11.Connect.FirstOrDefault(c =>
                IsOneBotWebSocketServer(c) && IsRedReplyWebSocket(c));
            if (redReplyWs != null)
            {
                changed |= EnsureWebSocketServerEnabled(config, redReplyWs);
                changed |= EnsureWebSocketServerEndpoint(redReplyWs);
                if (changed)
                    SaveLLBotConfig(configPath, config);

                var url = BuildOneBotWebSocketUrl(redReplyWs);
                _logger.LogInformation("复用 redreply WebSocket 服务端配置: {Url}", url);
                return url;
            }

            var enabledWs = config.OB11.Connect.FirstOrDefault(c =>
                IsOneBotWebSocketServer(c) && c.Enable);
            if (enabledWs != null)
            {
                changed |= EnsureWebSocketServerEnabled(config, enabledWs);
                changed |= EnsureWebSocketServerEndpoint(enabledWs);
                if (changed)
                    SaveLLBotConfig(configPath, config);

                var url = BuildOneBotWebSocketUrl(enabledWs);
                _logger.LogInformation("复用已启用的 OneBot11 正向 WebSocket: {Url}", url);
                return url;
            }

            var existingWs = config.OB11.Connect.FirstOrDefault(IsOneBotWebSocketServer);
            if (existingWs != null)
            {
                changed |= EnsureWebSocketServerEnabled(config, existingWs);
                changed |= EnsureWebSocketServerEndpoint(existingWs);
                if (string.IsNullOrWhiteSpace(existingWs.Name))
                {
                    existingWs.Name = "redreply";
                    changed = true;
                }
                if (changed)
                    SaveLLBotConfig(configPath, config);

                var url = BuildOneBotWebSocketUrl(existingWs);
                _logger.LogInformation("启用已有 OneBot11 正向 WebSocket: {Url}", url);
                return url;
            }

            var port = FindAvailablePort(3001);
            var newWs = new OB11Connection
            {
                Type = "ws",
                Name = "redreply",
                Enable = true,
                Host = "127.0.0.1",
                Port = port,
                Token = "",
                MessageFormat = "array",
                ReportSelfMessage = false,
                HeartInterval = 60000
            };
            config.OB11.Connect.Add(newWs);

            if (!config.OB11.Enable)
                config.OB11.Enable = true;

            SaveLLBotConfig(configPath, config);

            var newUrl = BuildOneBotWebSocketUrl(newWs);
            _logger.LogInformation("已新建 redreply OneBot11 正向 WebSocket: {Url}", newUrl);
            return newUrl;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "配置 redreply OneBot11 正向 WebSocket 失败");
            return fallbackUrl;
        }
    }

    public void StartRedReply(bool openWebUiIfRunning = true, bool openWebUiOnStart = true, string? oneBotWsUrl = null)
    {
        try
        {
            var redReplyPath = Path.GetFullPath(RedReplyDir);
            var exePath = Path.Combine(redReplyPath, GetExeFileName());

            if (!File.Exists(exePath))
            {
                _logger.LogError("redreply 未正确安装");
                return;
            }

            if (IsRedReplyRunning(exePath))
            {
                if (openWebUiIfRunning)
                {
                    OpenRedReplyWebUi(redReplyPath);
                    _logger.LogInformation("redreply 已在运行，已打开控制页面");
                }
                else
                {
                    _logger.LogInformation("redreply 已在运行，跳过重复启动");
                }
                return;
            }

            if (!string.IsNullOrWhiteSpace(oneBotWsUrl))
                ConfigureOneBot(oneBotWsUrl, openWebUiOnStart);

            Process.Start(new ProcessStartInfo
            {
                FileName = exePath,
                WorkingDirectory = redReplyPath,
                UseShellExecute = PlatformHelper.IsWindows
            });
            _logger.LogInformation("redreply 已启动");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "启动 redreply 失败");
        }
    }

    private bool IsRedReplyRunning(string exePath)
    {
        var processName = Path.GetFileNameWithoutExtension(exePath);
        var expectedPath = Path.GetFullPath(exePath);
        var hasInaccessibleRedlangProcess = false;

        foreach (var process in Process.GetProcessesByName(processName))
        {
            using (process)
            {
                try
                {
                    var processPath = process.MainModule?.FileName;
                    if (!string.IsNullOrEmpty(processPath) && PathsEqual(processPath, expectedPath))
                        return true;
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
                {
                    hasInaccessibleRedlangProcess = true;
                    _logger.LogDebug(ex, "无法读取 redreply 进程路径，退回到进程名判断");
                }
            }
        }

        return hasInaccessibleRedlangProcess;
    }

    private async Task StopRedReplyIfRunningAsync(string exePath, CancellationToken ct)
    {
        var processes = GetRedReplyProcesses(exePath);
        if (processes.Length == 0) return;

        _logger.LogInformation("检测到 redreply 正在运行，准备结束进程后重新安装");

        foreach (var process in processes)
        {
            using (process)
            {
                try
                {
                    if (process.HasExited) continue;

                    if (process.CloseMainWindow())
                    {
                        try
                        {
                            await process.WaitForExitAsync(ct).WaitAsync(TimeSpan.FromSeconds(5), ct);
                        }
                        catch (TimeoutException)
                        {
                            _logger.LogInformation("redreply 未在正常关闭等待时间内退出，准备强制结束");
                        }
                    }

                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                        await process.WaitForExitAsync(ct);
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "结束 redreply 进程失败: {Pid}", SafeProcessId(process));
                }
            }
        }
    }

    private Process[] GetRedReplyProcesses(string exePath)
    {
        var processName = Path.GetFileNameWithoutExtension(exePath);
        var expectedPath = Path.GetFullPath(exePath);
        var matches = Process.GetProcessesByName(processName)
            .Where(process =>
            {
                try
                {
                    var processPath = process.MainModule?.FileName;
                    if (string.IsNullOrEmpty(processPath))
                        return false;

                    if (PathsEqual(processPath, expectedPath))
                        return true;

                    process.Dispose();
                    return false;
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
                {
                    _logger.LogDebug(ex, "无法读取 redreply 进程路径，不会结束该进程");
                    process.Dispose();
                    return false;
                }
            })
            .ToArray();

        return matches;
    }

    private void OpenRedReplyWebUi(string redReplyPath)
    {
        var url = GetWebUiUrl(redReplyPath);
        Process.Start(new ProcessStartInfo
        {
            FileName = url,
            UseShellExecute = true
        });
    }

    private static string GetWebUiUrl(string redReplyPath)
    {
        var host = "127.0.0.1";
        var port = DefaultWebPort;
        var configPath = Path.Combine(redReplyPath, "plus_dir", "config.json");

        try
        {
            if (File.Exists(configPath))
            {
                var config = JsonNode.Parse(File.ReadAllText(configPath)) as JsonObject;
                if (config != null)
                {
                    host = ReadString(config, "web_host", host);
                    port = ReadInt(config, "web_port", port);
                }
            }
        }
        catch
        {
            host = "127.0.0.1";
            port = DefaultWebPort;
        }

        if (string.IsNullOrWhiteSpace(host) || host == "0.0.0.0" || host == "::")
            host = "127.0.0.1";

        if (port is <= 0 or > 65535)
            port = DefaultWebPort;

        return new UriBuilder("http", host, port).Uri.ToString().TrimEnd('/');
    }

    private static string GetDownloadFileName()
    {
        var arch = RuntimeInformation.ProcessArchitecture;

        if (PlatformHelper.IsWindows)
            return arch == Architecture.X86 ? "redlang_windows_i686.exe" : "redlang_windows_x86_64.exe";

        if (PlatformHelper.IsLinux)
        {
            return arch switch
            {
                Architecture.X86 => "redlang_linux_i686",
                Architecture.Arm64 => "redlang_linux_aarch64",
                _ => "redlang_linux_x86_64"
            };
        }

        return "";
    }

    private static string GetExeFileName() => PlatformHelper.IsWindows ? "redlang.exe" : "redlang";

    private static bool PathsEqual(string left, string right)
    {
        var comparison = PlatformHelper.IsWindows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return string.Equals(NormalizePath(left), NormalizePath(right), comparison);
    }

    private static string NormalizePath(string path) => Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private static int SafeProcessId(Process process)
    {
        try { return process.Id; }
        catch { return -1; }
    }

    private static string ReadString(JsonObject config, string key, string fallback)
    {
        return config[key] is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text)
            ? text
            : fallback;
    }

    private static int ReadInt(JsonObject config, string key, int fallback)
    {
        if (config[key] is not JsonValue value)
            return fallback;

        if (value.TryGetValue<int>(out var intValue))
            return intValue;

        if (value.TryGetValue<long>(out var longValue) && longValue is > 0 and <= int.MaxValue)
            return (int)longValue;

        return value.TryGetValue<string>(out var text) && int.TryParse(text, out var textValue)
            ? textValue
            : fallback;
    }

    private static JsonObject ReadConfig(string configPath)
    {
        if (!File.Exists(configPath))
            return new JsonObject();

        var json = File.ReadAllText(configPath);
        return JsonNode.Parse(json) as JsonObject ?? new JsonObject();
    }

    private static void SetDefault(JsonObject config, string key, JsonNode? value)
    {
        if (!config.ContainsKey(key))
            config[key] = value;
    }

    private static bool ContainsString(JsonArray array, string value)
    {
        foreach (var node in array)
        {
            if (node is JsonValue jsonValue &&
                jsonValue.TryGetValue<string>(out var text) &&
                string.Equals(text, value, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static void RemoveStaleDefaultWsUrl(JsonArray wsUrls, string currentWsUrl)
    {
        RemoveString(wsUrls, "ws://127.0.0.1:6700", currentWsUrl);
        RemoveString(wsUrls, "ws://localhost:6700", currentWsUrl);
    }

    private static void RemoveString(JsonArray array, string value, string keepValue)
    {
        if (string.Equals(value, keepValue, StringComparison.OrdinalIgnoreCase))
            return;

        for (var i = array.Count - 1; i >= 0; i--)
        {
            if (array[i] is JsonValue jsonValue &&
                jsonValue.TryGetValue<string>(out var text) &&
                string.Equals(text, value, StringComparison.OrdinalIgnoreCase))
            {
                array.RemoveAt(i);
            }
        }
    }

    private static bool IsOneBotWebSocketServer(OB11Connection connection)
    {
        return string.Equals(connection.Type, "ws", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsRedReplyWebSocket(OB11Connection connection)
    {
        return string.Equals(connection.Name, "redreply", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(connection.Name, "红色问答", StringComparison.OrdinalIgnoreCase);
    }

    private static bool EnsureWebSocketServerEndpoint(OB11Connection connection)
    {
        var changed = false;

        if (string.IsNullOrWhiteSpace(connection.Host))
        {
            connection.Host = "127.0.0.1";
            changed = true;
        }

        if (connection.Port <= 0 || connection.Port > 65535)
        {
            connection.Port = FindAvailablePort(3001);
            changed = true;
        }

        return changed;
    }

    private static bool EnsureWebSocketServerEnabled(LLBotConfig config, OB11Connection connection)
    {
        var changed = false;

        if (!connection.Enable)
        {
            connection.Enable = true;
            changed = true;
        }

        if (!config.OB11.Enable)
        {
            config.OB11.Enable = true;
            changed = true;
        }

        return changed;
    }

    private static string BuildOneBotWebSocketUrl(OB11Connection connection)
    {
        var host = string.IsNullOrWhiteSpace(connection.Host) ? "127.0.0.1" : connection.Host.Trim();
        if (host == "0.0.0.0" || host == "::" || host == "[::]")
            host = "127.0.0.1";

        var hostPart = host.Contains(':') && !host.StartsWith('[') && !host.EndsWith(']')
            ? $"[{host}]"
            : host;

        var url = $"ws://{hostPart}:{connection.Port}";
        if (!string.IsNullOrWhiteSpace(connection.Token))
            url += $"?access_token={Uri.EscapeDataString(connection.Token.Trim())}";

        return url;
    }

    private static void SaveLLBotConfig(string configPath, LLBotConfig config)
    {
        File.WriteAllText(configPath, JsonSerializer.Serialize(config, AppJsonContext.Default.LLBotConfig));
    }

    private static int FindAvailablePort(int startPort)
    {
        for (var port = startPort; port < startPort + 100; port++)
        {
            try
            {
                using var listener = new TcpListener(System.Net.IPAddress.Loopback, port);
                listener.Start();
                listener.Stop();
                return port;
            }
            catch { }
        }

        return startPort;
    }

    private static async Task ChmodExecutableAsync(string exePath, CancellationToken ct)
    {
        var chmodPsi = new ProcessStartInfo
        {
            FileName = "chmod",
            Arguments = $"+x \"{exePath}\"",
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using var chmodProc = Process.Start(chmodPsi);
        if (chmodProc != null)
            await chmodProc.WaitForExitAsync(ct);
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
}
