using LuckyLilliaDesktop.Models;
using LuckyLilliaDesktop.Utils;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
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
    private static readonly ConcurrentDictionary<int, string> _instanceNameCache = new();
    private static readonly ConcurrentDictionary<int, (DateTime Time, TimeSpan CpuTime)> _cpuSampleCache = new();
    private static readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true };

    // Job Object 句柄，用于自动清理子进程
    private static IntPtr _jobHandle = IntPtr.Zero;

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

        // 初始化 Job Object（仅在 Windows 上）
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            InitializeJobObject();
        }
    }

    /// <summary>
    /// 初始化 Job Object，确保主进程被杀死时自动清理所有子进程
    /// </summary>
    private void InitializeJobObject()
    {
        try
        {
            // 创建 Job Object
            _jobHandle = CreateJobObject(IntPtr.Zero, null);
            if (_jobHandle == IntPtr.Zero)
            {
                _logger.LogWarning("创建 Job Object 失败");
                return;
            }

            // 设置 Job Object 属性：当 Job 关闭时杀死所有进程
            var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
            {
                BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION
                {
                    LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE | JOB_OBJECT_LIMIT_BREAKAWAY_OK
                }
            };

            int length = Marshal.SizeOf(typeof(JOBOBJECT_EXTENDED_LIMIT_INFORMATION));
            IntPtr extendedInfoPtr = Marshal.AllocHGlobal(length);
            try
            {
                Marshal.StructureToPtr(info, extendedInfoPtr, false);

                if (!SetInformationJobObject(_jobHandle, JobObjectExtendedLimitInformation, extendedInfoPtr, (uint)length))
                {
                    _logger.LogWarning("设置 Job Object 信息失败");
                    CloseHandle(_jobHandle);
                    _jobHandle = IntPtr.Zero;
                    return;
                }
            }
            finally
            {
                Marshal.FreeHGlobal(extendedInfoPtr);
            }

            // 将当前进程加入 Job Object
            var currentProcess = Process.GetCurrentProcess();
            if (!AssignProcessToJobObject(_jobHandle, currentProcess.Handle))
            {
                _logger.LogWarning("将当前进程加入 Job Object 失败");
                CloseHandle(_jobHandle);
                _jobHandle = IntPtr.Zero;
                return;
            }

            _logger.LogInformation("Job Object 初始化成功，子进程将在主进程退出时自动清理");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "初始化 Job Object 失败");
        }
    }

    public async Task<bool> StartPmhqAsync(string pmhqPath, string qqPath, string autoLoginQQ, bool headless)
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

            // 如果指定了自动登录QQ号，添加 --qq 参数
            if (!string.IsNullOrEmpty(autoLoginQQ))
            {
                startInfo.ArgumentList.Add($"--qq={autoLoginQQ}");
            }

            // 如果启用无头模式
            if (headless)
            {
                startInfo.ArgumentList.Add("--headless");
            }

            var fullCommand = $"{pmhqPath} {string.Join(" ", startInfo.ArgumentList)}";
            _logger.LogInformation("启动 PMHQ 完整命令: {Command}", fullCommand);
            _logger.LogInformation("工作目录: {WorkingDir}", workingDir);

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
            var newJson = JsonSerializer.Serialize(config, _jsonOptions);
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

            startInfo.Environment["NODE_SKIP_PLATFORM_CHECK"] = "1";

            // 添加 Node.js 参数
            startInfo.ArgumentList.Add("--enable-source-maps");
            startInfo.ArgumentList.Add(scriptFileName);

            // 如果 PMHQ 端口已设置，传递给 LLBot
            if (PmhqPort.HasValue)
            {
                startInfo.ArgumentList.Add("--");
                startInfo.ArgumentList.Add($"--pmhq-port={PmhqPort.Value}");
            }

            var fullCommand = $"{nodePath} {string.Join(" ", startInfo.ArgumentList)}";
            _logger.LogInformation("启动 LLBot 完整命令: {Command}", fullCommand);
            _logger.LogInformation("工作目录: {WorkingDir}", workingDir);

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

    public async Task StopPmhqAsync()
    {
        if (_pmhqStatus == ProcessStatus.Stopped)
            return;

        _pmhqStatus = ProcessStatus.Stopping;
        ProcessStatusChanged?.Invoke(this, _pmhqStatus);

        if (_pmhqProcess != null)
        {
            _logCollector.DetachProcess("PMHQ");

            var pid = _pmhqProcess.Id;
            var process = _pmhqProcess;
            _pmhqProcess = null;

            try
            {
                if (!process.HasExited)
                {
                    await KillProcessTreeAsync(pid);
                }
            }
            catch { }
            finally
            {
                process.Dispose();
            }
        }

        _pmhqStatus = ProcessStatus.Stopped;
        ProcessStatusChanged?.Invoke(this, _pmhqStatus);
        _logger.LogInformation("PMHQ 已停止");
    }

    public async Task StopLLBotAsync()
    {
        if (_llbotProcess == null || _llbotStatus == ProcessStatus.Stopped)
            return;

        _llbotStatus = ProcessStatus.Stopping;
        ProcessStatusChanged?.Invoke(this, _llbotStatus);

        _logCollector.DetachProcess("LLBot");

        var pid = _llbotProcess.Id;
        var process = _llbotProcess;
        _llbotProcess = null;

        try
        {
            if (!process.HasExited)
            {
                await KillProcessTreeAsync(pid);
            }
        }
        catch { }
        finally
        {
            process.Dispose();
        }

        _llbotStatus = ProcessStatus.Stopped;
        ProcessStatusChanged?.Invoke(this, _llbotStatus);
        _logger.LogInformation("LLBot 已停止");
    }

    public async Task StopAllAsync(int? qqPid = null)
    {
        _monitorCts?.Cancel();

        _logger.LogInformation("停止所有进程...");

        // 先停止 LLBot 和 PMHQ（已包含等待逻辑）
        await StopLLBotAsync();
        await StopPmhqAsync();

        // 最后终止 QQ 进程
        if (qqPid.HasValue && qqPid.Value > 0)
        {
            _logger.LogInformation("正在终止 QQ 进程, PID: {Pid}", qqPid.Value);
            await KillProcessTreeAsync(qqPid.Value);
            _logger.LogInformation("QQ 进程已终止");
        }

        _logger.LogInformation("所有进程已停止");
    }

    /// <summary>
    /// 等待进程完全退出
    /// </summary>
    private static async Task WaitForProcessExitAsync(int pid, int timeoutMs = 10000)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            try
            {
                Process.GetProcessById(pid);
                await Task.Delay(200);
            }
            catch (ArgumentException)
            {
                // 进程已退出
                return;
            }
        }
    }

    /// <summary>
    /// 异步杀死进程树并等待完全退出
    /// </summary>
    private static async Task KillProcessTreeAsync(int pid, int timeoutMs = 10000)
    {
        try
        {
            if (PlatformHelper.IsWindows)
            {
                // 使用 taskkill 强制终止进程树
                var psi = new ProcessStartInfo
                {
                    FileName = "taskkill",
                    Arguments = $"/F /T /PID {pid}",
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };

                using var killProcess = Process.Start(psi);
                if (killProcess != null)
                {
                    // 等待 taskkill 命令完成
                    await killProcess.WaitForExitAsync();
                }
            }
            else
            {
                // 在 macOS/Linux 上使用 kill -TERM
                var psi = new ProcessStartInfo
                {
                    FileName = "kill",
                    Arguments = $"-TERM {pid}",
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };

                using var killProcess = Process.Start(psi);
                if (killProcess != null)
                {
                    await killProcess.WaitForExitAsync();
                }
            }

            // 等待目标进程完全退出
            await WaitForProcessExitAsync(pid, timeoutMs);
        }
        catch
        {
            // 如果命令失败，尝试使用 Process.Kill
            try
            {
                using var process = Process.GetProcessById(pid);
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync();
                }
            }
            catch { }
        }
    }

    /// <summary>
    /// 快速强制终止所有进程（用于应用退出）
    /// </summary>
    public void ForceKillAll(int? qqPid = null)
    {
        _logger.LogInformation("强制终止所有进程...");

        // 终止 LLBot
        if (_llbotProcess != null && !_llbotProcess.HasExited)
        {
            var llbotPid = _llbotProcess.Id;
            _logger.LogInformation("强制终止 LLBot 进程, PID: {Pid}", llbotPid);
            KillProcessTree(llbotPid);
        }

        // 终止 PMHQ
        if (_pmhqProcess != null && !_pmhqProcess.HasExited)
        {
            var pmhqPid = _pmhqProcess.Id;
            _logger.LogInformation("强制终止 PMHQ 进程, PID: {Pid}", pmhqPid);
            KillProcessTree(pmhqPid);
        }

        // 终止 QQ
        if (qqPid.HasValue && qqPid.Value > 0)
        {
            _logger.LogInformation("强制终止 QQ 进程, PID: {Pid}", qqPid.Value);
            KillProcessTree(qqPid.Value);
        }

        _logger.LogInformation("所有进程已强制终止");
    }

    /// <summary>
    /// 同步杀死进程树（用于 ForceKillAll）
    /// </summary>
    private static void KillProcessTree(int pid)
    {
        try
        {
            if (PlatformHelper.IsWindows)
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "taskkill",
                    Arguments = $"/F /T /PID {pid}",
                    CreateNoWindow = true,
                    UseShellExecute = false
                };
                using var killProcess = Process.Start(psi);
                killProcess?.WaitForExit(5000);
            }
            else
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "kill",
                    Arguments = $"-TERM {pid}",
                    CreateNoWindow = true,
                    UseShellExecute = false
                };
                using var killProcess = Process.Start(psi);
                killProcess?.WaitForExit(5000);
            }
        }
        catch
        {
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

    public ProcessResourceInfo GetProcessResources(string processName, bool includeCpu = true)
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
            var cpuPercent = includeCpu ? GetCpuUsage(process) : 0;
            var memoryMB = GetMemoryMB(process);
            return new ProcessResourceInfo(processName, cpuPercent, memoryMB);
        }
        catch
        {
            return new ProcessResourceInfo(processName, 0, 0);
        }
    }

    private static double GetMemoryMB(Process process)
    {
        try
        {
            // 获取进程及其所有子进程的内存总和
            var totalMemory = GetProcessTreeMemory(process.Id);
            if (totalMemory > 0)
                return totalMemory;
        }
        catch { }

        // fallback: 只获取当前进程
        try
        {
            var pid = process.Id;
            if (!_instanceNameCache.TryGetValue(pid, out var instanceName))
            {
                instanceName = FindInstanceName(process);
                if (instanceName != null)
                {
                    _instanceNameCache[pid] = instanceName;
                }
            }

            if (!string.IsNullOrEmpty(instanceName) && PlatformHelper.IsWindows)
            {
                using var counter = new PerformanceCounter("Process", "Working Set - Private", instanceName, true);
                return counter.RawValue / 1024.0 / 1024.0;
            }
        }
        catch { }

        try
        {
            process.Refresh();
            return process.WorkingSet64 / 1024.0 / 1024.0;
        }
        catch { return 0; }
    }

    private static double GetProcessTreeMemory(int parentPid)
    {
        double totalMemory = 0;
        var pidsToCheck = new List<int> { parentPid };
        var checkedPids = new HashSet<int>();

        while (pidsToCheck.Count > 0)
        {
            var currentPid = pidsToCheck[0];
            pidsToCheck.RemoveAt(0);

            if (checkedPids.Contains(currentPid))
                continue;
            checkedPids.Add(currentPid);

            try
            {
                var proc = Process.GetProcessById(currentPid);
                totalMemory += GetSingleProcessMemory(proc);

                // 查找子进程
                foreach (var p in Process.GetProcesses())
                {
                    try
                    {
                        if (GetParentProcessId(p.Id) == currentPid && !checkedPids.Contains(p.Id))
                        {
                            pidsToCheck.Add(p.Id);
                        }
                    }
                    catch { }
                    finally { p.Dispose(); }
                }
            }
            catch { }
        }

        return totalMemory;
    }

    private static double GetSingleProcessMemory(Process process)
    {
        try
        {
            var pid = process.Id;
            if (!_instanceNameCache.TryGetValue(pid, out var instanceName))
            {
                instanceName = FindInstanceName(process);
                if (instanceName != null)
                    _instanceNameCache[pid] = instanceName;
            }

            if (!string.IsNullOrEmpty(instanceName) && PlatformHelper.IsWindows)
            {
                using var counter = new PerformanceCounter("Process", "Working Set - Private", instanceName, true);
                return counter.RawValue / 1024.0 / 1024.0;
            }
        }
        catch { }

        try
        {
            process.Refresh();
            return process.WorkingSet64 / 1024.0 / 1024.0;
        }
        catch { return 0; }
    }

    #region Windows API

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(IntPtr processHandle, int processInformationClass, ref PROCESS_BASIC_INFORMATION processInformation, int processInformationLength, out int returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(IntPtr hJob, int JobObjectInfoClass, IntPtr lpJobObjectInfo, uint cbJobObjectInfoLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcess(
        string? lpApplicationName,
        string lpCommandLine,
        IntPtr lpProcessAttributes,
        IntPtr lpThreadAttributes,
        bool bInheritHandles,
        uint dwCreationFlags,
        IntPtr lpEnvironment,
        string? lpCurrentDirectory,
        ref STARTUPINFO lpStartupInfo,
        out PROCESS_INFORMATION lpProcessInformation);

    private const uint CREATE_BREAKAWAY_FROM_JOB = 0x01000000;
    private const uint CREATE_NEW_CONSOLE = 0x00000010;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public int dwX, dwY, dwXSize, dwYSize;
        public int dwXCountChars, dwYCountChars;
        public int dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_BASIC_INFORMATION
    {
        public IntPtr Reserved1;
        public IntPtr PebBaseAddress;
        public IntPtr Reserved2_0;
        public IntPtr Reserved2_1;
        public IntPtr UniqueProcessId;
        public IntPtr InheritedFromUniqueProcessId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    private const int JobObjectExtendedLimitInformation = 9;
    private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;
    private const uint JOB_OBJECT_LIMIT_BREAKAWAY_OK = 0x0800;

    #endregion

    private static int GetParentProcessId(int pid)
    {
        // NtQueryInformationProcess is Windows-only
        if (!PlatformHelper.IsWindows)
            return 0;

        try
        {
            var process = Process.GetProcessById(pid);
            var pbi = new PROCESS_BASIC_INFORMATION();
            int returnLength;
            var status = NtQueryInformationProcess(process.Handle, 0, ref pbi, Marshal.SizeOf(pbi), out returnLength);
            if (status == 0)
                return pbi.InheritedFromUniqueProcessId.ToInt32();
        }
        catch { }
        return 0;
    }

    private static string? FindInstanceName(Process process)
    {
        // PerformanceCounter is Windows-only
        if (!PlatformHelper.IsWindows)
            return null;

        try
        {
            var name = process.ProcessName;
            var pid = process.Id;

            try
            {
                using var counter = new PerformanceCounter("Process", "ID Process", name, true);
                if ((int)counter.RawValue == pid)
                    return name;
            }
            catch { }

            for (int i = 1; i <= 20; i++)
            {
                var instanceName = $"{name}#{i}";
                try
                {
                    using var counter = new PerformanceCounter("Process", "ID Process", instanceName, true);
                    if ((int)counter.RawValue == pid)
                        return instanceName;
                }
                catch { break; }
            }
        }
        catch { }
        return null;
    }

    private static double GetCpuUsage(Process process)
    {
        try
        {
            process.Refresh();
            var now = DateTime.UtcNow;
            var currentCpuTime = process.TotalProcessorTime;
            var pid = process.Id;

            if (_cpuSampleCache.TryGetValue(pid, out var lastSample))
            {
                var elapsedMs = (now - lastSample.Time).TotalMilliseconds;
                // 至少间隔 50ms 才计算，避免除数过小
                if (elapsedMs >= 50)
                {
                    var cpuUsedMs = (currentCpuTime - lastSample.CpuTime).TotalMilliseconds;
                    _cpuSampleCache[pid] = (now, currentCpuTime);
                    return cpuUsedMs / (Environment.ProcessorCount * elapsedMs) * 100;
                }
                // 间隔太短，返回上次计算的值（通过不更新缓存实现）
                return 0;
            }

            // 首次采样，记录并返回 0
            _cpuSampleCache[pid] = (now, currentCpuTime);
            return 0;
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

    /// <summary>
    /// 启动一个脱离 Job Object 的进程，使其不会随主进程退出而被终止（仅 Windows）
    /// </summary>
    public bool StartProcessOutsideJob(string fileName, string? workingDirectory = null)
    {
        if (!PlatformHelper.IsWindows)
        {
            _logger.LogWarning("StartProcessOutsideJob 仅支持 Windows 平台");
            return false;
        }

        var si = new STARTUPINFO { cb = Marshal.SizeOf<STARTUPINFO>() };
        var cmdLine = $"cmd.exe /c \"{fileName}\"";

        var created = CreateProcess(
            null,
            cmdLine,
            IntPtr.Zero,
            IntPtr.Zero,
            false,
            CREATE_BREAKAWAY_FROM_JOB | CREATE_NEW_CONSOLE,
            IntPtr.Zero,
            workingDirectory,
            ref si,
            out var pi);

        if (!created)
        {
            _logger.LogError("CreateProcess 失败, error={Error}", Marshal.GetLastWin32Error());
            return false;
        }

        CloseHandle(pi.hThread);
        CloseHandle(pi.hProcess);
        _logger.LogInformation("已启动脱离 Job 的进程: PID={Pid}, cmd={Cmd}", pi.dwProcessId, cmdLine);
        return true;
    }

    public void Dispose()
    {
        _monitorCts?.Cancel();
        _monitorCts?.Dispose();

        _pmhqProcess?.Dispose();
        _llbotProcess?.Dispose();

        // 清理 Job Object
        if (_jobHandle != IntPtr.Zero)
        {
            CloseHandle(_jobHandle);
            _jobHandle = IntPtr.Zero;
        }

        GC.SuppressFinalize(this);
    }
}
