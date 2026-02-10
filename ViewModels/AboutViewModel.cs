using LuckyLilliaDesktop.Services;
using Microsoft.Extensions.Logging;
using ReactiveUI;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reactive;
using System.Reactive.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;

namespace LuckyLilliaDesktop.ViewModels;

public class AboutViewModel : ViewModelBase
{
    private readonly ILogger<AboutViewModel> _logger;
    private readonly IConfigManager _configManager;
    private readonly IUpdateChecker _updateChecker;
    private readonly IDownloadService _downloadService;
    private readonly IProcessManager _processManager;
    private readonly IResourceMonitor _resourceMonitor;
    private readonly IUpdateStateService _updateStateService;

    private string? _pendingAppUpdateScript;

    // 对话框委托
    public Func<string, string, Task<bool>>? ConfirmDialog { get; set; }
    
    // 重启服务回调
    public Func<Task>? RestartServicesCallback { get; set; }

    // GitHub 仓库地址
    private const string AppGitHubUrl = "https://github.com/LLOneBot/LuckyLilliaDesktop.Avalonia";
    private const string PmhqGitHubUrl = "https://github.com/linyuchen/PMHQ";
    private const string LLBotGitHubUrl = "https://github.com/LLOneBot/LuckyLilliaBot";
    private const string QQGroupUrl = "https://qm.qq.com/q/4XrMj9iReU";

    // 应用信息
    public static string AppName => "Lucky Lillia Desktop";
    public string AppVersion { get; }

    // PMHQ 版本
    private string _pmhqVersion = "检测中...";
    public string PmhqVersion
    {
        get => _pmhqVersion;
        set => this.RaiseAndSetIfChanged(ref _pmhqVersion, value);
    }

    // LLBot 版本
    private string _llbotVersion = "检测中...";
    public string LLBotVersion
    {
        get => _llbotVersion;
        set => this.RaiseAndSetIfChanged(ref _llbotVersion, value);
    }

    // 更新状态 - App
    private bool _appIsLatest;
    public bool AppIsLatest
    {
        get => _appIsLatest;
        set => this.RaiseAndSetIfChanged(ref _appIsLatest, value);
    }

    private bool _appHasUpdate;
    public bool AppHasUpdate
    {
        get => _appHasUpdate;
        set => this.RaiseAndSetIfChanged(ref _appHasUpdate, value);
    }

    private string _appLatestVersion = string.Empty;
    public string AppLatestVersion
    {
        get => _appLatestVersion;
        set => this.RaiseAndSetIfChanged(ref _appLatestVersion, value);
    }

    private string _appReleaseUrl = string.Empty;
    public string AppReleaseUrl
    {
        get => _appReleaseUrl;
        set => this.RaiseAndSetIfChanged(ref _appReleaseUrl, value);
    }

    // 更新状态 - PMHQ
    private bool _pmhqIsLatest;
    public bool PmhqIsLatest
    {
        get => _pmhqIsLatest;
        set => this.RaiseAndSetIfChanged(ref _pmhqIsLatest, value);
    }

    private bool _pmhqHasUpdate;
    public bool PmhqHasUpdate
    {
        get => _pmhqHasUpdate;
        set => this.RaiseAndSetIfChanged(ref _pmhqHasUpdate, value);
    }

    private string _pmhqLatestVersion = string.Empty;
    public string PmhqLatestVersion
    {
        get => _pmhqLatestVersion;
        set => this.RaiseAndSetIfChanged(ref _pmhqLatestVersion, value);
    }

    private string _pmhqReleaseUrl = string.Empty;
    public string PmhqReleaseUrl
    {
        get => _pmhqReleaseUrl;
        set => this.RaiseAndSetIfChanged(ref _pmhqReleaseUrl, value);
    }

    // 更新状态 - LLBot
    private bool _llbotIsLatest;
    public bool LLBotIsLatest
    {
        get => _llbotIsLatest;
        set => this.RaiseAndSetIfChanged(ref _llbotIsLatest, value);
    }

    private bool _llbotHasUpdate;
    public bool LLBotHasUpdate
    {
        get => _llbotHasUpdate;
        set => this.RaiseAndSetIfChanged(ref _llbotHasUpdate, value);
    }

    private string _llbotLatestVersion = string.Empty;
    public string LLBotLatestVersion
    {
        get => _llbotLatestVersion;
        set => this.RaiseAndSetIfChanged(ref _llbotLatestVersion, value);
    }

    private string _llbotReleaseUrl = string.Empty;
    public string LLBotReleaseUrl
    {
        get => _llbotReleaseUrl;
        set => this.RaiseAndSetIfChanged(ref _llbotReleaseUrl, value);
    }

    // 检查更新状态
    private bool _isCheckingUpdate;
    public bool IsCheckingUpdate
    {
        get => _isCheckingUpdate;
        set => this.RaiseAndSetIfChanged(ref _isCheckingUpdate, value);
    }

    private string _checkUpdateButtonText = "检查更新";
    public string CheckUpdateButtonText
    {
        get => _checkUpdateButtonText;
        set => this.RaiseAndSetIfChanged(ref _checkUpdateButtonText, value);
    }

    private bool _hasAnyUpdate;
    public bool HasAnyUpdate
    {
        get => _hasAnyUpdate;
        set => this.RaiseAndSetIfChanged(ref _hasAnyUpdate, value);
    }

    // 下载更新状态
    private bool _isDownloadingUpdate;
    public bool IsDownloadingUpdate
    {
        get => _isDownloadingUpdate;
        set => this.RaiseAndSetIfChanged(ref _isDownloadingUpdate, value);
    }

    private string _downloadStatus = string.Empty;
    public string DownloadStatus
    {
        get => _downloadStatus;
        set => this.RaiseAndSetIfChanged(ref _downloadStatus, value);
    }

    private double _downloadProgress;
    public double DownloadProgress
    {
        get => _downloadProgress;
        set => this.RaiseAndSetIfChanged(ref _downloadProgress, value);
    }

    // 命令
    public ReactiveCommand<Unit, Unit> CheckOrUpdateCommand { get; }
    public ReactiveCommand<Unit, Unit> OpenAppReleaseCommand { get; }
    public ReactiveCommand<Unit, Unit> OpenPmhqReleaseCommand { get; }
    public ReactiveCommand<Unit, Unit> OpenLLBotReleaseCommand { get; }
    public ReactiveCommand<Unit, Unit> OpenDocsCommand { get; }
    public ReactiveCommand<Unit, Unit> OpenQQGroupCommand { get; }
    public ReactiveCommand<Unit, Unit> OpenAppGitHubCommand { get; }
    public ReactiveCommand<Unit, Unit> OpenPmhqGitHubCommand { get; }
    public ReactiveCommand<Unit, Unit> OpenLLBotGitHubCommand { get; }

    public AboutViewModel(
        ILogger<AboutViewModel> logger,
        IConfigManager configManager,
        IUpdateChecker updateChecker,
        IDownloadService downloadService,
        IProcessManager processManager,
        IResourceMonitor resourceMonitor,
        IUpdateStateService updateStateService)
    {
        _logger = logger;
        _configManager = configManager;
        _updateChecker = updateChecker;
        _downloadService = downloadService;
        _processManager = processManager;
        _resourceMonitor = resourceMonitor;
        _updateStateService = updateStateService;

        // 获取应用版本
        var assembly = Assembly.GetExecutingAssembly();
        var version = assembly.GetName().Version;
        AppVersion = version != null
            ? $"{version.Major}.{version.Minor}.{version.Build}"
            : "1.0.0";

        // 检查更新/立即更新命令
        CheckOrUpdateCommand = ReactiveCommand.CreateFromTask(CheckOrUpdateAsync);

        // 打开链接命令
        OpenAppReleaseCommand = ReactiveCommand.Create(() => OpenUrl(AppReleaseUrl));
        OpenPmhqReleaseCommand = ReactiveCommand.Create(() => OpenUrl(PmhqReleaseUrl));
        OpenLLBotReleaseCommand = ReactiveCommand.Create(() => OpenUrl(LLBotReleaseUrl));
        OpenDocsCommand = ReactiveCommand.Create(() => OpenUrl("https://luckylillia.com"));
        OpenQQGroupCommand = ReactiveCommand.Create(() => OpenUrl(QQGroupUrl));
        OpenAppGitHubCommand = ReactiveCommand.Create(() => OpenUrl(AppGitHubUrl));
        OpenPmhqGitHubCommand = ReactiveCommand.Create(() => OpenUrl(PmhqGitHubUrl));
        OpenLLBotGitHubCommand = ReactiveCommand.Create(() => OpenUrl(LLBotGitHubUrl));

        // 订阅共享更新状态
        _updateStateService.StateChanged
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(OnUpdateStateChanged);

        // 初始化版本检测
        _ = LoadVersionsAsync();
    }

    private void OnUpdateStateChanged(UpdateState state)
    {
        if (!state.IsChecked) return;

        AppHasUpdate = state.AppHasUpdate;
        AppLatestVersion = state.AppLatestVersion;
        AppReleaseUrl = state.AppReleaseUrl;
        AppIsLatest = !state.AppHasUpdate;

        PmhqHasUpdate = state.PmhqHasUpdate;
        PmhqLatestVersion = state.PmhqLatestVersion;
        PmhqReleaseUrl = state.PmhqReleaseUrl;
        PmhqIsLatest = !state.PmhqHasUpdate;

        LLBotHasUpdate = state.LLBotHasUpdate;
        LLBotLatestVersion = state.LLBotLatestVersion;
        LLBotReleaseUrl = state.LLBotReleaseUrl;
        LLBotIsLatest = !state.LLBotHasUpdate;

        HasAnyUpdate = state.HasAnyUpdate;
        CheckUpdateButtonText = HasAnyUpdate ? "立即更新" : "检查更新";
    }

    /// <summary>
    /// 加载本地版本信息
    /// </summary>
    public async Task LoadVersionsAsync()
    {
        try
        {
            var config = await _configManager.LoadConfigAsync();

            // 检测 PMHQ 版本
            var pmhqPath = config.PmhqPath;
            if (!string.IsNullOrEmpty(pmhqPath))
            {
                var pmhqVersion = DetectPmhqVersion(pmhqPath);
                PmhqVersion = pmhqVersion ?? "未知";
            }
            else
            {
                PmhqVersion = "未配置";
            }

            // 检测 LLBot 版本
            var llbotPath = config.LLBotPath;
            if (!string.IsNullOrEmpty(llbotPath))
            {
                var llbotVersion = DetectLLBotVersion(llbotPath);
                LLBotVersion = llbotVersion ?? "未知";
            }
            else
            {
                LLBotVersion = "未配置";
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "加载版本信息失败");
            PmhqVersion = "检测失败";
            LLBotVersion = "检测失败";
        }
    }

    /// <summary>
    /// 检测 PMHQ 版本
    /// </summary>
    private string? DetectPmhqVersion(string pmhqPath)
    {
        try
        {
            // 尝试从 package.json 读取版本
            var pmhqDir = Path.GetDirectoryName(pmhqPath);
            if (string.IsNullOrEmpty(pmhqDir)) return null;

            var packageJsonPath = Path.Combine(pmhqDir, "package.json");
            if (File.Exists(packageJsonPath))
            {
                var json = File.ReadAllText(packageJsonPath);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("version", out var versionElement))
                {
                    return versionElement.GetString();
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "检测 PMHQ 版本失败");
        }
        return null;
    }

    /// <summary>
    /// 检测 LLBot 版本
    /// </summary>
    private string? DetectLLBotVersion(string llbotPath)
    {
        try
        {
            // 尝试从 package.json 读取版本
            var llbotDir = Path.GetDirectoryName(llbotPath);
            if (string.IsNullOrEmpty(llbotDir)) return null;

            var packageJsonPath = Path.Combine(llbotDir, "package.json");
            if (File.Exists(packageJsonPath))
            {
                var json = File.ReadAllText(packageJsonPath);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("version", out var versionElement))
                {
                    return versionElement.GetString();
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "检测 LLBot 版本失败");
        }
        return null;
    }

    private async Task CheckOrUpdateAsync()
    {
        if (IsCheckingUpdate || IsDownloadingUpdate) return;

        if (HasAnyUpdate)
        {
            await DownloadAllUpdatesAsync();
        }
        else
        {
            await CheckUpdatesAsync();
        }
    }

    private async Task CheckUpdatesAsync()
    {
        IsCheckingUpdate = true;
        CheckUpdateButtonText = "检查中...";

        // 清除之前的更新状态
        AppIsLatest = false;
        AppHasUpdate = false;
        PmhqIsLatest = false;
        PmhqHasUpdate = false;
        LLBotIsLatest = false;
        LLBotHasUpdate = false;
        HasAnyUpdate = false;

        try
        {
            _logger.LogInformation("开始检查更新...");

            // 检查应用更新
            var appUpdate = await _updateChecker.CheckAppUpdateAsync(AppVersion);
            if (appUpdate.HasUpdate)
            {
                AppHasUpdate = true;
                AppLatestVersion = appUpdate.LatestVersion;
                AppReleaseUrl = appUpdate.ReleaseUrl;
                _logger.LogInformation("发现应用新版本: {Version}", appUpdate.LatestVersion);
            }
            else
            {
                AppIsLatest = true;
            }

            // 检查 PMHQ 更新
            if (PmhqVersion != "未配置" && PmhqVersion != "未知" && PmhqVersion != "检测中...")
            {
                var pmhqUpdate = await _updateChecker.CheckPmhqUpdateAsync(PmhqVersion);
                if (pmhqUpdate.HasUpdate)
                {
                    PmhqHasUpdate = true;
                    PmhqLatestVersion = pmhqUpdate.LatestVersion;
                    PmhqReleaseUrl = pmhqUpdate.ReleaseUrl;
                    _logger.LogInformation("发现 PMHQ 新版本: {Version}", pmhqUpdate.LatestVersion);
                }
                else
                {
                    PmhqIsLatest = true;
                }
            }
            else
            {
                PmhqIsLatest = true;
            }

            // 检查 LLBot 更新
            if (LLBotVersion != "未配置" && LLBotVersion != "未知" && LLBotVersion != "检测中...")
            {
                var llbotUpdate = await _updateChecker.CheckLLBotUpdateAsync(LLBotVersion);
                if (llbotUpdate.HasUpdate)
                {
                    LLBotHasUpdate = true;
                    LLBotLatestVersion = llbotUpdate.LatestVersion;
                    LLBotReleaseUrl = llbotUpdate.ReleaseUrl;
                    _logger.LogInformation("发现 LLBot 新版本: {Version}", llbotUpdate.LatestVersion);
                }
                else
                {
                    LLBotIsLatest = true;
                }
            }
            else
            {
                LLBotIsLatest = true;
            }

            // 更新按钮状态
            HasAnyUpdate = AppHasUpdate || PmhqHasUpdate || LLBotHasUpdate;
            CheckUpdateButtonText = HasAnyUpdate ? "立即更新" : "检查更新";

            _logger.LogInformation("更新检查完成");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "检查更新失败");
            CheckUpdateButtonText = "检查更新";
        }
        finally
        {
            IsCheckingUpdate = false;
        }
    }

    private async Task DownloadAllUpdatesAsync()
    {
        if (IsDownloadingUpdate) return;

        var wasRunning = _processManager.IsAnyProcessRunning;

        // 如果服务正在运行，弹出确认对话框
        if (wasRunning && ConfirmDialog != null)
        {
            var confirmed = await ConfirmDialog("确认更新", "服务正在运行中，更新需要先停止服务。更新完成后将自动重启服务。\n\n确定要继续吗？");
            if (!confirmed)
            {
                _logger.LogInformation("用户取消更新");
                return;
            }
        }

        IsDownloadingUpdate = true;
        DownloadProgress = 0;

        // 收集需要更新的组件，按顺序：LLBot → PMHQ → 管理器
        var updates = new List<string>();
        if (LLBotHasUpdate) updates.Add("LLBot");
        if (PmhqHasUpdate) updates.Add("PMHQ");
        if (AppHasUpdate) updates.Add("管理器");

        try
        {
            // 如果有进程在运行，先停止
            if (wasRunning)
            {
                DownloadStatus = "正在停止所有进程...";
                _logger.LogInformation("更新前停止所有进程...");
                await _processManager.StopAllAsync(_resourceMonitor.QQPid);
                await Task.Delay(1000);
            }

            var progress = new Progress<DownloadProgress>(p =>
            {
                DownloadProgress = p.Percentage;
                DownloadStatus = p.Status;
            });

            // 依次更新各组件
            foreach (var component in updates)
            {
                DownloadStatus = $"正在更新: {component}";
                DownloadProgress = 0;

                bool success;
                switch (component)
                {
                    case "LLBot":
                        success = await _downloadService.DownloadLLBotAsync(progress);
                        if (success)
                        {
                            LLBotHasUpdate = false;
                            LLBotIsLatest = true;
                            _updateStateService.ClearUpdate("LLBot");
                        }
                        break;

                    case "PMHQ":
                        success = await _downloadService.DownloadPmhqAsync(progress);
                        if (success)
                        {
                            PmhqHasUpdate = false;
                            PmhqIsLatest = true;
                            _updateStateService.ClearUpdate("PMHQ");
                        }
                        break;

                    case "管理器":
                        var result = await _downloadService.DownloadAppUpdateAsync(progress);
                        if (result.Success)
                        {
                            _pendingAppUpdateScript = result.UpdateScriptPath;
                            AppHasUpdate = false;
                            AppIsLatest = true;
                            _updateStateService.ClearUpdate("管理器");
                        }
                        break;
                }
            }

            DownloadStatus = "更新完成！";
            DownloadProgress = 100;

            // 重新加载版本信息
            await LoadVersionsAsync();

            // 重置按钮状态
            HasAnyUpdate = false;
            CheckUpdateButtonText = "检查更新";

            // 如果有管理器更新，提示重启
            if (!string.IsNullOrEmpty(_pendingAppUpdateScript))
            {
                await Task.Delay(500);
                LaunchAppUpdate();
            }
            else if (wasRunning && RestartServicesCallback != null)
            {
                // 更新前服务在运行，自动重启
                _logger.LogInformation("更新完成，自动重启服务...");
                DownloadStatus = "正在重启服务...";
                await Task.Delay(500);
                await RestartServicesCallback();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "下载更新失败");
            DownloadStatus = $"更新失败: {ex.Message}";
        }
        finally
        {
            IsDownloadingUpdate = false;
        }
    }

    private void LaunchAppUpdate()
    {
        if (string.IsNullOrEmpty(_pendingAppUpdateScript)) return;

        _logger.LogInformation("启动应用更新脚本: {Script}", _pendingAppUpdateScript);

        try
        {
            // 必须脱离 Job Object，否则主进程退出时脚本也会被杀掉
            var workDir = Path.GetDirectoryName(_pendingAppUpdateScript) ?? "";
            if (!_processManager.StartProcessOutsideJob(_pendingAppUpdateScript, workDir))
            {
                _logger.LogError("通过 Job Breakaway 启动更新脚本失败，回退到 ShellExecute");
                Process.Start(new ProcessStartInfo
                {
                    FileName = _pendingAppUpdateScript,
                    UseShellExecute = true,
                    WorkingDirectory = workDir
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "启动更新脚本失败");
            return;
        }

        _pendingAppUpdateScript = null;

        if (Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.Shutdown();
        }
    }

    /// <summary>
    /// 打开 URL
    /// </summary>
    private void OpenUrl(string url)
    {
        _logger.LogInformation("尝试打开链接: {Url}", url);
        if (string.IsNullOrEmpty(url))
        {
            _logger.LogWarning("链接为空，无法打开");
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
            _logger.LogInformation("已打开链接: {Url}", url);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "打开链接失败: {Url}", url);
        }
    }

    /// <summary>
    /// 刷新页面
    /// </summary>
    public async Task RefreshAsync()
    {
        await LoadVersionsAsync();
    }
}
