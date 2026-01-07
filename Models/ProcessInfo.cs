namespace LuckyLilliaDesktop.Models;

/// <summary>
/// 进程状态枚举
/// </summary>
public enum ProcessStatus
{
    Stopped,
    Starting,
    Running,
    Stopping,
    Error
}

/// <summary>
/// 进程信息
/// </summary>
public class ProcessInfo
{
    public string Name { get; set; } = string.Empty;
    public ProcessStatus Status { get; set; }
    public int? Pid { get; set; }
    public double CpuPercent { get; set; }
    public double MemoryMB { get; set; }
    public string Version { get; set; } = string.Empty;
}

/// <summary>
/// 进程资源信息
/// </summary>
public record ProcessResourceInfo(
    string ProcessName,
    double CpuPercent,
    double MemoryMB
);
