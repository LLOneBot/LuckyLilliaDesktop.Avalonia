using LuckyLilliaDesktop.Models;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reactive.Subjects;
using System.Threading;
using System.Threading.Tasks;

namespace LuckyLilliaDesktop.Services;

/// <summary>
/// 日志收集服务接口
/// </summary>
public interface ILogCollector
{
    IObservable<LogEntry> LogStream { get; }
    IEnumerable<LogEntry> GetRecentLogs(int count);
    void AttachProcess(string processName, Process process);
    void DetachProcess(string processName);
    void ClearLogs();
    int GetLogCount();
}

/// <summary>
/// 日志收集服务完整实现
/// </summary>
public class LogCollector : ILogCollector, IDisposable
{
    private const int MaxLogEntries = 1000;

    private readonly ILogger<LogCollector> _logger;
    private readonly Subject<LogEntry> _logSubject = new();
    private readonly ConcurrentQueue<LogEntry> _logQueue = new();
    private readonly Dictionary<string, CancellationTokenSource> _processCancellations = new();

    public IObservable<LogEntry> LogStream => _logSubject;

    public LogCollector(ILogger<LogCollector> logger)
    {
        _logger = logger;
    }

    public void AttachProcess(string processName, Process process)
    {
        if (_processCancellations.ContainsKey(processName))
        {
            _logger.LogWarning("进程 {Name} 已附加，先分离", processName);
            DetachProcess(processName);
        }

        var cts = new CancellationTokenSource();
        _processCancellations[processName] = cts;

        // 启动 stdout 读取线程
        Task.Run(() => ReadStreamAsync(processName, process.StandardOutput, "stdout", cts.Token), cts.Token);

        // 启动 stderr 读取线程
        Task.Run(() => ReadStreamAsync(processName, process.StandardError, "stderr", cts.Token), cts.Token);

        _logger.LogInformation("已附加进程 {Name} 的日志收集", processName);
    }

    public void DetachProcess(string processName)
    {
        if (_processCancellations.TryGetValue(processName, out var cts))
        {
            cts.Cancel();
            cts.Dispose();
            _processCancellations.Remove(processName);
            _logger.LogInformation("已分离进程 {Name} 的日志收集", processName);
        }
    }

    private async Task ReadStreamAsync(string processName, StreamReader reader, string level, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(ct);
                if (line == null)
                    break;

                if (string.IsNullOrWhiteSpace(line))
                    continue;

                var entry = new LogEntry(DateTime.Now, processName, level, line);

                // 添加到队列
                _logQueue.Enqueue(entry);

                // 限制队列大小
                while (_logQueue.Count > MaxLogEntries)
                {
                    _logQueue.TryDequeue(out _);
                }

                // 推送到观察者
                _logSubject.OnNext(entry);

                // 同时记录到应用日志
                if (level == "stderr")
                {
                    _logger.LogError("[{Process}] {Message}", processName, line);
                }
                else
                {
                    _logger.LogInformation("[{Process}] {Message}", processName, line);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 正常取消
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "读取 {Process} {Level} 流时出错", processName, level);
        }
    }

    public IEnumerable<LogEntry> GetRecentLogs(int count)
    {
        return _logQueue
            .TakeLast(count)
            .ToList();
    }

    public int GetLogCount()
    {
        return _logQueue.Count;
    }

    public void ClearLogs()
    {
        _logQueue.Clear();
        _logger.LogInformation("日志已清空");
    }

    public void Dispose()
    {
        foreach (var cts in _processCancellations.Values)
        {
            cts.Cancel();
            cts.Dispose();
        }
        _processCancellations.Clear();
        _logSubject.Dispose();
    }
}
