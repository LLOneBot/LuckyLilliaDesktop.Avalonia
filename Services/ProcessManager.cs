using LuckyLilliaDesktop.Models;
using LuckyLilliaDesktop.Utils;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace LuckyLilliaDesktop.Services;

/// <summary>
/// 进程管理服务完整实现
/// </summary>
public class ProcessManager : IProcessManager, IDisposable
{
    private readonly ILogger<ProcessManager> _logger;
    private readonly ILogCollector _logCollector;

    private Process? _pmhqProcess;
    private Process? _llbotProcess;
    private ProcessStatus _pmhqStatus = ProcessStatus.Stopped;
    private ProcessStatus _llbotStatus = ProcessStatus.Stopped;

    private CancellationTokenSource? _monitorCts;
    private Task? _pmhqMonitorTask;
    private Task? _llbotMonitorTask;

    /// <summary>
    /// PMHQ 使用的端口号
    /// </summary>
    public int? PmhqPort { get; private set; }

    /// <summary>
    /// 是否有任何进程在运行
    /// </summary>
    public bool IsAnyProcessRunning => 
        _pmhqStatus == ProcessStatus.Running || _llbotStatus == ProcessStatus.Running;

    public event EventHandler<ProcessStatus>? ProcessStatusChanged;

    public ProcessManager(ILogger<ProcessManager> logger, ILogCollector logCollector)
    {
        _logger = logger;
        _logCollector = logCollector;
    }

    public async Task<bool> StartPmhqAsync(string pmhqPath, string qqPath, bool autoLogin, bool headless)
    {
        try
        {
            if (_pmhqStatus == ProcessStatus.Running)
            {
                _logger.LogWarning("PMHQ 已在运行中");
                return false;
            }

            // 验证路径
            if (!File.Exists(pmhqPath))
            {
                _logger.LogError("PMHQ 可执行文件不存在: {Path}", pmhqPath);
                return false;
            }

            _pmhqStatus = ProcessStatus.Starting;
            ProcessStatusChanged?.Invoke(this, _pmhqStatus);

            // 获取工作目录
            var workingDir = Path.GetDirectoryName(pmhqPath) ?? Environment.CurrentDirectory;

            // 动态获取可用端口
            PmhqPort = PortHelper.GetAvailablePort(13000);
            _logger.LogInformation("PMHQ 使用端口: {Port}", PmhqPort);

            // 如果指定了 QQ 路径，更新 PMHQ 配置文件
            if (!string.IsNullOrEmpty(qqPath) && File.Exists(qqPath))
            {
                await UpdatePmhqConfigAsync(workingDir, qqPath);
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = pmhqPath,
                WorkingDirectory = workingDir,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            // 添加端口参数
            startInfo.ArgumentList.Add($"--port={PmhqPort}");

            // 如果启用无头模式
            if (headless)
            {
                startInfo.ArgumentList.Add("--headless");
            }

            _logger.LogInformation("启动 PMHQ: {Path} {Args}", pmhqPath, string.Join(" ", startInfo.ArgumentList));

            _pmhqProcess = Process.Start(startInfo);

            if (_pmhqProcess == null)
            {
                _logger.LogError("无法启动 PMHQ 进程");
                _pmhqStatus = ProcessStatus.Error;
                ProcessStatusChanged?.Invoke(this, _pmhqStatus);
                return false;
            }

            // 等待一小段时间确认进程启动
            await Task.Delay(500);

            // 检查进程是否立即退出
            if (_pmhqProcess.HasExited)
            {
                var exitCode = _pmhqProcess.ExitCode;
                // PMHQ 启动 QQ 后会正常退出（返回码 0），这是预期行为
                // PMHQ 会先启动 QQ 进程，然后自己退出，但 HTTP API 仍在运行（由 QQ 提供）
                if (exitCode == 0)
                {
                    _logger.LogInformation("PMHQ 进程正常退出（这是预期行为），返回码: {ExitCode}", exitCode);
                    _logger.LogInformation("PMHQ 已启动 QQ，HTTP API 由 QQ 进程提供");

                    // 清理进程对象，但保持状态为 Running
                    _pmhqProcess.Dispose();
                    _pmhqProcess = null;

                    _pmhqStatus = ProcessStatus.Running;
                    ProcessStatusChanged?.Invoke(this, _pmhqStatus);
                    return true;
                }
                else
                {
                    _logger.LogError("PMHQ 进程异常退出，返回码: {ExitCode}", exitCode);
                    _pmhqProcess.Dispose();
                    _pmhqProcess = null;
                    _pmhqStatus = ProcessStatus.Error;
                    ProcessStatusChanged?.Invoke(this, _pmhqStatus);
                    return false;
                }
            }

            // 附加日志收集
            _logCollector.AttachProcess("PMHQ", _pmhqProcess);

            _pmhqStatus = ProcessStatus.Running;
            ProcessStatusChanged?.Invoke(this, _pmhqStatus);

            // 启动监控
            _monitorCts = new CancellationTokenSource();
            _pmhqMonitorTask = MonitorProcessAsync(_pmhqProcess, "PMHQ", _monitorCts.Token);

            _logger.LogInformation("PMHQ 启动成功, PID: {Pid}", _pmhqProcess.Id);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "��动 PMHQ 失败");
            _pmhqStatus = ProcessStatus.Error;
            ProcessStatusChanged?.Invoke(this, _pmhqStatus);
            return false;
        }
    }

    /// <summary>
    /// 更新 PMHQ 目录下的 pmhq_config.json 配置文件
    /// </summary>
    private async Task UpdatePmhqConfigAsync(string pmhqDir, string qqPath)
    {
        var configPath = Path.Combine(pmhqDir, "pmhq_config.json");
        var absQqPath = Path.GetFullPath(qqPath);

        try
        {
            Dictionary<string, object>? config = null;

            // 读取现有配置
            if (File.Exists(configPath))
            {
                var json = await File.ReadAllTextAsync(configPath);
                config = JsonSerializer.Deserialize<Dictionary<string, object>>(json);
            }

            config ??= new Dictionary<string, object>();
            config["qq_path"] = absQqPath;

            // 写回配置
            var options = new JsonSerializerOptions { WriteIndented = true };
            var newJson = JsonSerializer.Serialize(config, options);
            await File.WriteAllTextAsync(configPath, newJson);

            _logger.LogInformation("已更新 PMHQ 配置文件 qq_path: {QQPath}", absQqPath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "更新 PMHQ 配置文件失败");
        }
    }

    public async Task<bool> StartLLBotAsync(string nodePath, string scriptPath)
    {
        try
        {
            if (_llbotStatus == ProcessStatus.Running)
            {
                _logger.LogWarning("LLBot 已在运行中");
                return false;
            }

            if (!File.Exists(nodePath))
            {
                _logger.LogError("Node.js 可执行文件不存在: {Path}", nodePath);
                return false;
            }

            if (!File.Exists(scriptPath))
            {
                _logger.LogError("LLBot 脚本不存在: {Path}", scriptPath);
                return false;
            }

            _llbotStatus = ProcessStatus.Starting;
            ProcessStatusChanged?.Invoke(this, _llbotStatus);

            var workingDir = Path.GetDirectoryName(scriptPath) ?? Environment.CurrentDirectory;
            var scriptFileName = Path.GetFileName(scriptPath);

            var startInfo = new ProcessStartInfo
            {
                FileName = nodePath,
                WorkingDirectory = workingDir,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            // 添加 Node.js 参数
            startInfo.ArgumentList.Add("--enable-source-maps");
            startInfo.ArgumentList.Add(scriptFileName);

            // 如果 PMHQ 端口已设置，传递给 LLBot
            if (PmhqPort.HasValue)
            {
                startInfo.ArgumentList.Add("--");
                startInfo.ArgumentList.Add($"--pmhq-port={PmhqPort.Value}");
            }

            _logger.LogInformation("启动 LLBot: {Node} {Args}", nodePath, string.Join(" ", startInfo.ArgumentList));

            _llbotProcess = Process.Start(startInfo);

            if (_llbotProcess == null)
            {
                _logger.LogError("无法启动 LLBot 进程");
                _llbotStatus = ProcessStatus.Error;
                ProcessStatusChanged?.Invoke(this, _llbotStatus);
                return false;
            }

            // 等待一小段时间确认进程启动
            await Task.Delay(500);

            // 检查进程是否立即退出
            if (_llbotProcess.HasExited)
            {
                _logger.LogError("LLBot 进程立即退出，返回码: {ExitCode}", _llbotProcess.ExitCode);
                _llbotStatus = ProcessStatus.Error;
                ProcessStatusChanged?.Invoke(this, _llbotStatus);
                return false;
            }

            // 附加日志收集
            _logCollector.AttachProcess("LLBot", _llbotProcess);

            _llbotStatus = ProcessStatus.Running;
            ProcessStatusChanged?.Invoke(this, _llbotStatus);

            // 启动监控
            _monitorCts ??= new CancellationTokenSource();
            _llbotMonitorTask = MonitorProcessAsync(_llbotProcess, "LLBot", _monitorCts.Token);

            _logger.LogInformation("LLBot 启动成功, PID: {Pid}", _llbotProcess.Id);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "启动 LLBot 失败");
            _llbotStatus = ProcessStatus.Error;
            ProcessStatusChanged?.Invoke(this, _llbotStatus);
            return false;
        }
    }

    public Task StopPmhqAsync()
    {
        // 如果进程已停止，直接返回
        if (_pmhqStatus == ProcessStatus.Stopped)
            return Task.CompletedTask;

        _pmhqStatus = ProcessStatus.Stopping;
        ProcessStatusChanged?.Invoke(this, _pmhqStatus);

        // PMHQ 进程可能已经正常退出（这是预期行为）
        // 如果进程对象存在且未退出，才需要终止
        if (_pmhqProcess != null)
        {
            _logCollector.DetachProcess("PMHQ");

            var process = _pmhqProcess;
            _pmhqProcess = null;
            
            // 在后台杀进程，不阻塞
            _ = Task.Run(() =>
            {
                try
                {
                    if (!process.HasExited)
                    {
                        KillProcessTree(process.Id);
                    }
                }
                catch { }
                finally
                {
                    process.Dispose();
                }
            });
        }

        _pmhqStatus = ProcessStatus.Stopped;
        ProcessStatusChanged?.Invoke(this, _pmhqStatus);
        _logger.LogInformation("PMHQ 已停止");
        
        return Task.CompletedTask;
    }

    public Task StopLLBotAsync()
    {
        if (_llbotProcess == null || _llbotStatus == ProcessStatus.Stopped)
            return Task.CompletedTask;

        _llbotStatus = ProcessStatus.Stopping;
        ProcessStatusChanged?.Invoke(this, _llbotStatus);

        _logCollector.DetachProcess("LLBot");

        var process = _llbotProcess;
        _llbotProcess = null;
        
        // 在后台杀进程，不阻塞
        _ = Task.Run(() =>
        {
            try
            {
                if (!process.HasExited)
                {
                    KillProcessTree(process.Id);
                }
            }
            catch { }
            finally
            {
                process.Dispose();
            }
        });

        _llbotStatus = ProcessStatus.Stopped;
        ProcessStatusChanged?.Invoke(this, _llbotStatus);
        _logger.LogInformation("LLBot 已停止");
        
        return Task.CompletedTask;
    }

    public Task StopAllAsync()
    {
        _monitorCts?.Cancel();

        // 先停止 LLBot 和 PMHQ（同步，不等待）
        _ = StopLLBotAsync();
        _ = StopPmhqAsync();

        // 尝试终止 QQ 进程（fire-and-forget，不阻塞）
        if (PmhqPort.HasValue)
        {
            var port = PmhqPort.Value;
            _ = Task.Run(async () =>
            {
                try
                {
                    using var cts = new CancellationTokenSource(200);
                    using var client = new HttpClient { Timeout = TimeSpan.FromMilliseconds(200) };
                    var payload = new { type = "call", data = new { func = "getProcessInfo", args = Array.Empty<object>() } };
                    var response = await client.PostAsJsonAsync($"http://127.0.0.1:{port}", payload, cts.Token);
                    if (response.IsSuccessStatusCode)
                    {
                        var json = await response.Content.ReadFromJsonAsync<JsonElement>(cts.Token);
                        if (json.TryGetProperty("data", out var dataElem))
                        {
                            var dataStr = dataElem.ValueKind == JsonValueKind.String 
                                ? dataElem.GetString() 
                                : dataElem.GetRawText();
                            if (!string.IsNullOrEmpty(dataStr))
                            {
                                var data = JsonSerializer.Deserialize<JsonElement>(dataStr);
                                if (data.TryGetProperty("result", out var result) && 
                                    result.TryGetProperty("pid", out var pidElem))
                                {
                                    var qqPid = pidElem.GetInt32();
                                    if (qqPid > 0)
                                    {
                                        _logger.LogInformation("正在终止 QQ 进程, PID: {Pid}", qqPid);
                                        // 使用 taskkill 更快地杀进程树
                                        KillProcessTree(qqPid);
                                        _logger.LogInformation("QQ 进程已终止");
                                    }
                                }
                            }
                        }
                    }
                }
                catch { }
            });
        }

        PmhqPort = null;
        return Task.CompletedTask;
    }

    /// <summary>
    /// 使用 taskkill 快速杀死进程树
    /// </summary>
    private void KillProcessTree(int pid)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "taskkill",
                Arguments = $"/F /T /PID {pid}",
                CreateNoWindow = true,
                UseShellExecute = false
            };
            Process.Start(psi);
        }
        catch
        {
            // 如果 taskkill 失败，回退到 Process.Kill
            try
            {
                using var process = Process.GetProcessById(pid);
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch { }
        }
    }

    public ProcessStatus GetProcessStatus(string processName)
    {
        return processName.ToLower() switch
        {
            "pmhq" => _pmhqStatus,
            "llbot" => _llbotStatus,
            _ => ProcessStatus.Stopped
        };
    }

    public ProcessResourceInfo GetProcessResources(string processName)
    {
        var process = processName.ToLower() switch
        {
            "pmhq" => _pmhqProcess,
            "llbot" => _llbotProcess,
            _ => null
        };

        if (process == null || process.HasExited)
        {
            return new ProcessResourceInfo(processName, 0, 0);
        }

        try
        {
            var cpuPercent = GetCpuUsage(process);
            var memoryMB = process.WorkingSet64 / 1024.0 / 1024.0;
            return new ProcessResourceInfo(processName, cpuPercent, memoryMB);
        }
        catch
        {
            return new ProcessResourceInfo(processName, 0, 0);
        }
    }

    private double GetCpuUsage(Process process)
    {
        try
        {
            var startTime = DateTime.UtcNow;
            var startCpuUsage = process.TotalProcessorTime;

            Thread.Sleep(100);

            var endTime = DateTime.UtcNow;
            var endCpuUsage = process.TotalProcessorTime;

            var cpuUsedMs = (endCpuUsage - startCpuUsage).TotalMilliseconds;
            var totalMsPassed = (endTime - startTime).TotalMilliseconds;
            var cpuUsageTotal = cpuUsedMs / (Environment.ProcessorCount * totalMsPassed);

            return cpuUsageTotal * 100;
        }
        catch
        {
            return 0;
        }
    }

    private async Task MonitorProcessAsync(Process process, string name, CancellationToken ct)
    {
        try
        {
            await process.WaitForExitAsync(ct);

            _logger.LogWarning("{Name} 进程已退出, ExitCode: {ExitCode}", name, process.ExitCode);

            if (name == "PMHQ")
            {
                _pmhqStatus = ProcessStatus.Stopped;
                ProcessStatusChanged?.Invoke(this, _pmhqStatus);
            }
            else if (name == "LLBot")
            {
                _llbotStatus = ProcessStatus.Stopped;
                ProcessStatusChanged?.Invoke(this, _llbotStatus);
            }
        }
        catch (OperationCanceledException)
        {
            // 正常取消
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "监控 {Name} 进程时出错", name);
        }
    }

    public void Dispose()
    {
        _monitorCts?.Cancel();
        _monitorCts?.Dispose();

        _pmhqProcess?.Dispose();
        _llbotProcess?.Dispose();
    }
}
