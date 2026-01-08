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
    IObservable<SelfInfo> UinStream { get; }
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

    public IObservable<ProcessResourceInfo> ResourceStream => _resourceSubject;
    public IObservable<SelfInfo> UinStream => _uinSubject;
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
            _logger.LogWarning("资源监控已在运行");
            return Task.CompletedTask;
        }

        _monitorCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _monitorTask = MonitorLoopAsync(_monitorCts.Token);

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
    }

    private async Task MonitorLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));

        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                // 监控 PMHQ
                var pmhqStatus = _processManager.GetProcessStatus("PMHQ");
                if (pmhqStatus == ProcessStatus.Running)
                {
                    var pmhqResources = _processManager.GetProcessResources("PMHQ");
                    _resourceSubject.OnNext(pmhqResources);
                }

                // 监控 LLBot
                var llbotStatus = _processManager.GetProcessStatus("LLBot");
                if (llbotStatus == ProcessStatus.Running)
                {
                    var llbotResources = _processManager.GetProcessResources("LLBot");
                    _resourceSubject.OnNext(llbotResources);
                }

                // 获取系统可用内存
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

                // 尝试获取 UIN 和 QQ PID
                await TryFetchSelfInfoAsync();
                await TryFetchQQPidAsync();
                await TryFetchQQVersionAsync();

                // 监控 QQ 进程
                await MonitorQQProcessAsync();
            }
        }
        catch (OperationCanceledException)
        {
            // 正常取消
        }
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

    private async Task MonitorQQProcessAsync()
    {
        if (!_qqPid.HasValue)
            return;

        try
        {
            var qqProcess = Process.GetProcessById(_qqPid.Value);
            if (qqProcess != null && !qqProcess.HasExited)
            {
                var cpuPercent = 0.0; // CPU 计算复杂，暂时跳过
                var memoryMB = qqProcess.WorkingSet64 / 1024.0 / 1024.0;
                var qqResources = new ProcessResourceInfo("QQ", cpuPercent, memoryMB);
                _resourceSubject.OnNext(qqResources);
            }
        }
        catch (ArgumentException)
        {
            // 进程不存在
            _qqPid = null;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "监控 QQ 进程失败");
        }

        await Task.CompletedTask;
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
