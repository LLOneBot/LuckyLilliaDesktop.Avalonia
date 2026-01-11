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
            if (LogEntry.ProcessName == "LLBot")
            {
                return $"{prefix} {LogEntry.Message}";
            }
            var timestamp = LogEntry.Timestamp.ToString("HH:mm:ss");
            return $"{prefix} {timestamp} [{LogEntry.ProcessName}] {LogEntry.Message}";
        }
    }

    public bool IsError => LogEntry.Level == "stderr";

    public LogEntryViewModel(LogEntry logEntry)
    {
        LogEntry = logEntry;
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
}
