using LuckyLilliaDesktop.Models;
using Microsoft.Extensions.Logging;
using System;
using System.Diagnostics;
using System.Reactive.Subjects;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace LuckyLilliaDesktop.Services;

public interface IResourceMonitor
{
    IObservable<ProcessResourceInfo> ResourceStream { get; }
    IObservable<string> QQVersionStream { get; }
    IObservable<double> AvailableMemoryStream { get; }
    IObservable<int?> QQPidStream { get; }
    int? QQPid { get; }
    Task StartMonitoringAsync(CancellationToken ct = default);
    void StopMonitoring();
    void ResetState();
    void PauseMonitoring();
    void ResumeMonitoring();
}

public class ResourceMonitor : IResourceMonitor, IDisposable
{
    private readonly ILogger<ResourceMonitor> _logger;
    private readonly IProcessManager _processManager;
    private readonly IPmhqClient _pmhqClient;
    private readonly Subject<ProcessResourceInfo> _resourceSubject = new();
    private readonly Subject<string> _qqVersionSubject = new();
    private readonly Subject<double> _availableMemorySubject = new();
    private readonly Subject<int?> _qqPidSubject = new();
    private CancellationTokenSource? _monitorCts;
    private Task? _monitorTask;
    private string? _cachedQQVersion;
    private int? _qqPid;
    private int _tickCount;
    private bool _isPaused;

    public IObservable<ProcessResourceInfo> ResourceStream => _resourceSubject;
    public IObservable<string> QQVersionStream => _qqVersionSubject;
    public IObservable<double> AvailableMemoryStream => _availableMemorySubject;
    public IObservable<int?> QQPidStream => _qqPidSubject;
    public int? QQPid => _qqPid;

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

    private static double GetWorkingSetMB(Process process)
    {
        try
        {
            process.Refresh();
            return process.WorkingSet64 / 1024.0 / 1024.0;
        }
        catch { return 0; }
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
        _cachedQQVersion = null;
        _qqPid = null;
        _logger.LogDebug("资源监控状态已重置");
    }

    public void PauseMonitoring()
    {
        _isPaused = true;
        _logger.LogDebug("资源监控已暂停");
    }

    public void ResumeMonitoring()
    {
        _isPaused = false;
        _logger.LogDebug("资源监控已恢复");
    }


    private async Task MonitorLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));

        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                // 如果暂停，跳过本次监控
                if (_isPaused)
                {
                    continue;
                }
                
                _tickCount++;
                // CPU 监控每 5 秒执行一次
                var shouldMonitorCpu = _tickCount % 5 == 0;

                // 在后台线程执行所有监控操作
                try
                {
                    var currentProcess = Process.GetCurrentProcess();
                    var managerMemory = GetWorkingSetMB(currentProcess);
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
                    var pmhqResources = _processManager.GetProcessResources("PMHQ", shouldMonitorCpu);
                    _resourceSubject.OnNext(pmhqResources);
                }

                var llbotStatus = _processManager.GetProcessStatus("LLBot");
                if (llbotStatus == ProcessStatus.Running)
                {
                    var llbotResources = _processManager.GetProcessResources("LLBot", shouldMonitorCpu);
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
                _qqPidSubject.OnNext(pid.Value);
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
                        var nodeMemory = GetWorkingSetMB(process);
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
            _qqPidSubject.OnNext(null);
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
        try
        {
            var proc = Process.GetProcessById(parentPid);
            var memory = GetWorkingSetMB(proc);
            proc.Dispose();
            return memory;
        }
        catch { return 0; }
    }

    public void Dispose()
    {
        _monitorCts?.Cancel();
        _monitorCts?.Dispose();
        _resourceSubject.Dispose();
        _qqVersionSubject.Dispose();
        _availableMemorySubject.Dispose();
        _qqPidSubject.Dispose();
        GC.SuppressFinalize(this);
    }
}
