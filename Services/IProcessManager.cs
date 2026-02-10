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
    void ForceKillAll(int? qqPid = null);
    bool StartProcessOutsideJob(string fileName, string? workingDirectory = null);

    ProcessStatus GetProcessStatus(string processName);
    ProcessResourceInfo GetProcessResources(string processName, bool includeCpu = true);

    event EventHandler<ProcessStatus>? ProcessStatusChanged;
}
