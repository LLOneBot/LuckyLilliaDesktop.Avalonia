using LuckyLilliaDesktop.Models;
using LuckyLilliaDesktop.Services;
using Microsoft.Extensions.Logging;
using ReactiveUI;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;

namespace LuckyLilliaDesktop.ViewModels;

public class LogEntryViewModel : ViewModelBase
{
    public LogEntry LogEntry { get; }

    public string FormattedText
    {
        get
        {
            var prefix = LogEntry.Level == "stderr" ? "ERR" : "   ";
            string text;
            if (LogEntry.ProcessName == "LLBot")
            {
                text = $"{prefix} {LogEntry.Message}";
            }
            else
            {
                var timestamp = LogEntry.Timestamp.ToString("HH:mm:ss");
                text = $"{prefix} {timestamp} [{LogEntry.ProcessName}] {LogEntry.Message}";
            }
            return SanitizeText(text);
        }
    }

    public bool IsError => LogEntry.Level == "stderr";

    public LogEntryViewModel(LogEntry logEntry)
    {
        LogEntry = logEntry;
    }

    private static string SanitizeText(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;

        var sb = new System.Text.StringBuilder(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            var c = text[i];
            
            // 保留常用字符范围
            // - ASCII 可打印字符 (0x20-0x7E)
            // - 常用标点和符号
            // - 中日韩字符 (CJK)
            // - 常用拉丁扩展
            // - Emoji (通过代理对)
            
            // 控制字符（保留 tab, newline, cr）
            if (c < 0x20 && c != '\t' && c != '\n' && c != '\r')
                continue;
            
            // 代理对（emoji等），保留完整的代理对
            if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                sb.Append(c);
                sb.Append(text[++i]);
                continue;
            }
            
            // 单独的代理字符（不完整），跳过
            if (char.IsSurrogate(c))
                continue;
            
            // 私用区字符，跳过
            if (c >= 0xE000 && c <= 0xF8FF)
                continue;
            
            // 其他字符保留，让字体回退机制处理
            sb.Append(c);
        }
        return sb.ToString();
    }
}

public class LogViewModel : ViewModelBase, IDisposable
{
    private readonly ILogCollector _logCollector;
    private readonly ILogger<LogViewModel> _logger;
    private readonly IDisposable _logSubscription;

    public ObservableCollection<LogEntryViewModel> LogEntries { get; } = new();
    public ObservableCollection<LogEntryViewModel> SelectedLogEntries { get; } = new();

    public event Action? ScrollToBottomRequested;
    public event Action? ClearSelectionRequested;

    private bool _autoScroll = true;
    public bool AutoScroll
    {
        get => _autoScroll;
        set => this.RaiseAndSetIfChanged(ref _autoScroll, value);
    }

    private bool _hasSelection;
    public bool HasSelection
    {
        get => _hasSelection;
        set => this.RaiseAndSetIfChanged(ref _hasSelection, value);
    }

    public ReactiveCommand<Unit, Unit> ClearLogsCommand { get; }
    public ReactiveCommand<Unit, Unit> CopySelectedCommand { get; }
    public ReactiveCommand<Unit, Unit> ClearSelectionCommand { get; }

    public LogViewModel(ILogCollector logCollector, ILogger<LogViewModel> logger)
    {
        _logCollector = logCollector;
        _logger = logger;

        _logSubscription = _logCollector.LogStream
            .Buffer(TimeSpan.FromMilliseconds(200))
            .Where(batch => batch.Count > 0)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(OnLogBatchReceived);

        SelectedLogEntries.CollectionChanged += (_, _) =>
        {
            HasSelection = SelectedLogEntries.Count > 0;
        };

        ClearLogsCommand = ReactiveCommand.Create(() =>
        {
            LogEntries.Clear();
            _logCollector.ClearLogs();
            _logger.LogInformation("日志已清空");
        });

        CopySelectedCommand = ReactiveCommand.CreateFromTask(async () =>
        {
            if (SelectedLogEntries.Count == 0) return;

            var text = string.Join(Environment.NewLine,
                SelectedLogEntries.Select(e => e.FormattedText));

            var clipboard = Avalonia.Application.Current?.ApplicationLifetime
                is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
                ? desktop.MainWindow?.Clipboard
                : null;

            if (clipboard != null)
            {
                await clipboard.SetTextAsync(text);
                _logger.LogInformation("已复制 {Count} 条日志", SelectedLogEntries.Count);
            }

            SelectedLogEntries.Clear();
            ClearSelectionRequested?.Invoke();
        });

        ClearSelectionCommand = ReactiveCommand.Create(() =>
        {
            SelectedLogEntries.Clear();
            ClearSelectionRequested?.Invoke();
        });

        LoadRecentLogs();
    }

    private void LoadRecentLogs()
    {
        var recentLogs = _logCollector.GetRecentLogs(100);
        foreach (var log in recentLogs)
        {
            LogEntries.Add(new LogEntryViewModel(log));
        }
    }

    private void OnLogBatchReceived(System.Collections.Generic.IList<LogEntry> batch)
    {
        if (_isUIUpdatesPaused) return;
        
        foreach (var logEntry in batch)
        {
            LogEntries.Add(new LogEntryViewModel(logEntry));
        }

        const int maxLogs = 500;
        while (LogEntries.Count > maxLogs)
        {
            LogEntries.RemoveAt(0);
        }

        if (AutoScroll && !HasSelection)
        {
            ScrollToBottomRequested?.Invoke();
        }
    }

    public void Dispose()
    {
        _logSubscription.Dispose();
    }

    private bool _isUIUpdatesPaused;

    public void PauseUIUpdates()
    {
        if (_isUIUpdatesPaused) return;
        _isUIUpdatesPaused = true;
    }

    public void ResumeUIUpdates()
    {
        if (!_isUIUpdatesPaused) return;
        _isUIUpdatesPaused = false;
        
        // 恢复时重新加载最近的日志
        RefreshRecentLogs();
    }
    
    private void RefreshRecentLogs()
    {
        LogEntries.Clear();
        var recentLogs = _logCollector.GetRecentLogs(500);
        foreach (var log in recentLogs)
        {
            LogEntries.Add(new LogEntryViewModel(log));
        }
        
        if (AutoScroll)
        {
            ScrollToBottomRequested?.Invoke();
        }
    }
}
