using LuckyLilliaDesktop.Models;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reactive.Subjects;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace LuckyLilliaDesktop.Services;

public interface IResourceMonitor
{
    IObservable<ProcessResourceInfo> ResourceStream { get; }
    IObservable<SelfInfo> UinStream { get; }
    string? CurrentUin { get; }
    IObservable<string> QQVersionStream { get; }
    IObservable<double> AvailableMemoryStream { get; }
    Task StartMonitoringAsync(CancellationToken ct = default);
    void StopMonitoring();
    void ResetState();
}

public class ResourceMonitor : IResourceMonitor, IDisposable
{
    private readonly ILogger<ResourceMonitor> _logger;
    private readonly IProcessManager _processManager;
    private readonly IPmhqClient _pmhqClient;
    private readonly Subject<ProcessResourceInfo> _resourceSubject = new();
    private readonly Subject<SelfInfo> _uinSubject = new();
    private readonly Subject<string> _qqVersionSubject = new();
    private readonly Subject<double> _availableMemorySubject = new();
    private CancellationTokenSource? _monitorCts;
    private Task? _monitorTask;
    private string? _lastUin;
    private string? _cachedQQVersion;
    private int? _qqPid;

    // 缓存性能计数器实例名，避免重复查找
    private static readonly ConcurrentDictionary<int, string> _instanceNameCache = new();

    public IObservable<ProcessResourceInfo> ResourceStream => _resourceSubject;
    public IObservable<SelfInfo> UinStream => _uinSubject;
    public string? CurrentUin => _lastUin;
    public IObservable<string> QQVersionStream => _qqVersionSubject;
    public IObservable<double> AvailableMemoryStream => _availableMemorySubject;

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    private static double GetPrivateWorkingSetMB(Process process)
    {
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

            if (!string.IsNullOrEmpty(instanceName))
            {
                using var counter = new PerformanceCounter("Process", "Working Set - Private", instanceName, true);
                return counter.RawValue / 1024.0 / 1024.0;
            }
        }
        catch { }

        // fallback
        try
        {
            process.Refresh();
            return process.WorkingSet64 / 1024.0 / 1024.0;
        }
        catch { return 0; }
    }

    private static string? FindInstanceName(Process process)
    {
        try
        {
            var name = process.ProcessName;
            var pid = process.Id;

            // 先尝试直接用进程名
            try
            {
                using var counter = new PerformanceCounter("Process", "ID Process", name, true);
                if ((int)counter.RawValue == pid)
                    return name;
            }
            catch { }

            // 尝试带序号的实例名 (qq#1, qq#2, ...)
            for (int i = 1; i <= 50; i++)
            {
                var instanceName = $"{name}#{i}";
                try
                {
                    using var counter = new PerformanceCounter("Process", "ID Process", instanceName, true);
                    if ((int)counter.RawValue == pid)
                        return instanceName;
                }
                catch
                {
                    // 实例不存在，继续尝试下一个
                }
            }
        }
        catch { }
        return null;
    }

    public ResourceMonitor(ILogger<ResourceMonitor> logger, IProcessManager processManager, IPmhqClient pmhqClient)
    {
        _logger = logger;
        _processManager = processManager;
        _pmhqClient = pmhqClient;
    }

    public Task StartMonitoringAsync(CancellationToken ct = default)
    {
        if (_monitorTask != null && !_monitorTask.IsCompleted)
        {
            _logger.LogDebug("资源监控已在运行");
            return Task.CompletedTask;
        }

        _monitorCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _monitorTask = Task.Run(() => MonitorLoopAsync(_monitorCts.Token), _monitorCts.Token);

        _logger.LogInformation("资源监控已启动");
        return Task.CompletedTask;
    }

    public void StopMonitoring()
    {
        _monitorCts?.Cancel();
        ResetState();
        _logger.LogInformation("资源监控已停止");
    }

    public void ResetState()
    {
        _lastUin = null;
        _cachedQQVersion = null;
        _qqPid = null;
        _instanceNameCache.Clear();
        _logger.LogDebug("资源监控状态已重置");
    }


    private async Task MonitorLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));

        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                // 在后台线程执行所有监控操作
                try
                {
                    var currentProcess = Process.GetCurrentProcess();
                    var managerMemory = GetPrivateWorkingSetMB(currentProcess);
                    var managerResources = new ProcessResourceInfo("Manager", 0.0, managerMemory);
                    _resourceSubject.OnNext(managerResources);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "监控管理器进程失败");
                }

                var pmhqStatus = _processManager.GetProcessStatus("PMHQ");
                if (pmhqStatus == ProcessStatus.Running)
                {
                    var pmhqResources = _processManager.GetProcessResources("PMHQ");
                    _resourceSubject.OnNext(pmhqResources);
                }

                var llbotStatus = _processManager.GetProcessStatus("LLBot");
                if (llbotStatus == ProcessStatus.Running)
                {
                    var llbotResources = _processManager.GetProcessResources("LLBot");
                    _resourceSubject.OnNext(llbotResources);
                }

                await MonitorNodeProcessAsync();

                try
                {
                    var memStatus = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
                    if (GlobalMemoryStatusEx(ref memStatus))
                    {
                        var availableMB = memStatus.ullAvailPhys / 1024.0 / 1024.0;
                        _availableMemorySubject.OnNext(availableMB);
                    }
                }
                catch { }

                await TryFetchSelfInfoAsync();
                await TryFetchQQPidAsync();
                await TryFetchQQVersionAsync();
                await MonitorQQProcessAsync();
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogError(ex, "资源监控循环出错");
        }
    }

    private async Task TryFetchSelfInfoAsync()
    {
        if (!_pmhqClient.HasPort || !string.IsNullOrEmpty(_lastUin))
            return;

        var pmhqStatus = _processManager.GetProcessStatus("PMHQ");
        if (pmhqStatus != ProcessStatus.Running)
            return;

        try
        {
            var selfInfo = await _pmhqClient.FetchSelfInfoAsync();
            if (selfInfo != null && !string.IsNullOrEmpty(selfInfo.Uin))
            {
                _lastUin = selfInfo.Uin;
                _logger.LogInformation("获取到 UIN: {Uin}, 昵称: {Nickname}", selfInfo.Uin, selfInfo.Nickname);
                _uinSubject.OnNext(selfInfo);
            }
        }
        catch { }
    }

    private async Task TryFetchQQPidAsync()
    {
        if (!_pmhqClient.HasPort || _qqPid.HasValue)
            return;

        try
        {
            var pid = await _pmhqClient.FetchQQPidAsync();
            if (pid.HasValue && pid.Value > 0)
            {
                _qqPid = pid.Value;
                _logger.LogInformation("获取到 QQ PID: {Pid}", pid.Value);
            }
        }
        catch { }
    }

    private async Task TryFetchQQVersionAsync()
    {
        if (!_pmhqClient.HasPort || !string.IsNullOrEmpty(_cachedQQVersion) || !_qqPid.HasValue)
            return;

        try
        {
            var deviceInfo = await _pmhqClient.FetchDeviceInfoAsync();
            if (deviceInfo != null && !string.IsNullOrEmpty(deviceInfo.BuildVer))
            {
                _cachedQQVersion = deviceInfo.BuildVer;
                _logger.LogInformation("获取到 QQ 版本: {Version}", _cachedQQVersion);
                _qqVersionSubject.OnNext(_cachedQQVersion);
            }
        }
        catch { }
    }

    private async Task MonitorNodeProcessAsync()
    {
        try
        {
            var processes = Process.GetProcessesByName("node");
            foreach (var process in processes)
            {
                try
                {
                    var commandLine = process.StartInfo.Arguments;
                    if (commandLine.Contains("llbot.js") || process.ProcessName.Contains("node"))
                    {
                        var nodeMemory = GetPrivateWorkingSetMB(process);
                        var nodeResources = new ProcessResourceInfo("Node", 0.0, nodeMemory);
                        _resourceSubject.OnNext(nodeResources);
                        break;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "检查 Node.js 进程失败: {ProcessId}", process.Id);
                }
                finally
                {
                    process.Dispose();
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "监控 Node.js 进程失败");
        }

        await Task.CompletedTask;
    }

    private async Task MonitorQQProcessAsync()
    {
        if (!_qqPid.HasValue)
            return;

        try
        {
            var qqProcess = Process.GetProcessById(_qqPid.Value);
            if (qqProcess != null && !qqProcess.HasExited)
            {
                var memoryMB = GetProcessTreeMemory(_qqPid.Value);
                var qqResources = new ProcessResourceInfo("QQ", 0.0, memoryMB);
                _resourceSubject.OnNext(qqResources);
            }
        }
        catch (ArgumentException)
        {
            _qqPid = null;
            var qqStoppedResources = new ProcessResourceInfo("QQ", 0.0, 0.0);
            _resourceSubject.OnNext(qqStoppedResources);
            _logger.LogDebug("QQ 进程已退出");
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "监控 QQ 进程失败");
        }

        await Task.CompletedTask;
    }

    private static double GetProcessTreeMemory(int parentPid)
    {
        double totalMemory = 0;
        var pidsToCheck = new List<int> { parentPid };
        var checkedPids = new HashSet<int>();

        // 一次性获取所有进程的父子关系
        var parentMap = new Dictionary<int, int>();
        try
        {
            using var searcher = new System.Management.ManagementObjectSearcher("SELECT ProcessId, ParentProcessId FROM Win32_Process");
            foreach (var obj in searcher.Get())
            {
                var pid = Convert.ToInt32(obj["ProcessId"]);
                var ppid = Convert.ToInt32(obj["ParentProcessId"]);
                parentMap[pid] = ppid;
            }
        }
        catch { }

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
                totalMemory += GetPrivateWorkingSetMB(proc);
                proc.Dispose();
            }
            catch { }

            // 查找子进程
            foreach (var kvp in parentMap)
            {
                if (kvp.Value == currentPid && !checkedPids.Contains(kvp.Key))
                {
                    pidsToCheck.Add(kvp.Key);
                }
            }
        }

        return totalMemory;
    }

    public void Dispose()
    {
        StopMonitoring();
        _monitorCts?.Dispose();
        _resourceSubject.Dispose();
        _uinSubject.Dispose();
        _qqVersionSubject.Dispose();
        _availableMemorySubject.Dispose();
    }
}
