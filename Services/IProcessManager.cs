using LuckyLilliaDesktop.Models;
using System;
using System.Threading.Tasks;

namespace LuckyLilliaDesktop.Services;

/// <summary>
/// 进程管理服务接口
/// </summary>
public interface IProcessManager
{
    /// <summary>
    /// PMHQ 使用的端口号
    /// </summary>
    int? PmhqPort { get; }

    Task<bool> StartPmhqAsync(string pmhqPath, string qqPath, bool autoLogin, bool headless);
    Task<bool> StartLLBotAsync(string nodePath, string scriptPath);
    Task StopPmhqAsync();
    Task StopLLBotAsync();
    Task StopAllAsync();

    ProcessStatus GetProcessStatus(string processName);
    ProcessResourceInfo GetProcessResources(string processName);

    event EventHandler<ProcessStatus>? ProcessStatusChanged;
}
