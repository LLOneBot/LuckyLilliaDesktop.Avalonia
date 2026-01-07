using System;

namespace LuckyLilliaDesktop.Models;

/// <summary>
/// 日志条目
/// </summary>
public record LogEntry(
    DateTime Timestamp,
    string ProcessName,
    string Level,  // "stdout" or "stderr"
    string Message
);
