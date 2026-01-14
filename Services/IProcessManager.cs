using LuckyLilliaDesktop.Models;
using System;
using System.Threading.Tasks;

namespace LuckyLilliaDesktop.Services;

public interface IProcessManager
{
    int? PmhqPort { get; }
    bool IsAnyProcessRunning { get; }

    Task<bool> StartPmhqAsync(string pmhqPath, string qqPath, string autoLoginQQ, bool headless);
    Task<bool> StartLLBotAsync(string nodePath, string scriptPath);
    Task StopPmhqAsync();
    Task StopLLBotAsync();
    Task StopAllAsync(int? qqPid = null);

    ProcessStatus GetProcessStatus(string processName);
    ProcessResourceInfo GetProcessResources(string processName);

    event EventHandler<ProcessStatus>? ProcessStatusChanged;
}
