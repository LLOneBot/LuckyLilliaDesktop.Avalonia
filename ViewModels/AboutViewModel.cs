using LuckyLilliaDesktop.Services;
using Microsoft.Extensions.Logging;
using ReactiveUI;
using System;
using System.Diagnostics;
using System.IO;
using System.Reactive;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;

namespace LuckyLilliaDesktop.ViewModels;

public class AboutViewModel : ViewModelBase
{
    private readonly ILogger<AboutViewModel> _logger;
    private readonly IConfigManager _configManager;

    // GitHub 仓库地址
    private const string AppGitHubUrl = "https://github.com/linyyyang/LuckyLilliaDesktop";
    private const string PmhqGitHubUrl = "https://github.com/pakkipatty/PMHQ";
    private const string LLBotGitHubUrl = "https://github.com/LLBot-dev/LLBot";
    private const string QQGroupUrl = "https://qm.qq.com/q/4XrMj9iReU";

    // 应用信息
    public string AppName => "Lucky Lillia Desktop";
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

    // 命令
    public ReactiveCommand<Unit, Unit> CheckUpdateCommand { get; }
    public ReactiveCommand<Unit, Unit> OpenAppReleaseCommand { get; }
    public ReactiveCommand<Unit, Unit> OpenQQGroupCommand { get; }
    public ReactiveCommand<Unit, Unit> OpenAppGitHubCommand { get; }
    public ReactiveCommand<Unit, Unit> OpenPmhqGitHubCommand { get; }
    public ReactiveCommand<Unit, Unit> OpenLLBotGitHubCommand { get; }

    public AboutViewModel(ILogger<AboutViewModel> logger, IConfigManager configManager)
    {
        _logger = logger;
        _configManager = configManager;

        // 获取应用版本
        var assembly = Assembly.GetExecutingAssembly();
        var version = assembly.GetName().Version;
        AppVersion = version != null
            ? $"{version.Major}.{version.Minor}.{version.Build}"
            : "1.0.0";

        // 检查更新命令
        CheckUpdateCommand = ReactiveCommand.CreateFromTask(CheckUpdatesAsync);

        // 打开链接命令
        OpenAppReleaseCommand = ReactiveCommand.Create(() => OpenUrl(AppReleaseUrl));
        OpenQQGroupCommand = ReactiveCommand.Create(() => OpenUrl(QQGroupUrl));
        OpenAppGitHubCommand = ReactiveCommand.Create(() => OpenUrl(AppGitHubUrl));
        OpenPmhqGitHubCommand = ReactiveCommand.Create(() => OpenUrl(PmhqGitHubUrl));
        OpenLLBotGitHubCommand = ReactiveCommand.Create(() => OpenUrl(LLBotGitHubUrl));

        // 初始化版本检测
        _ = LoadVersionsAsync();
    }

    /// <summary>
    /// 加载本地版本信息
    /// </summary>
    private async Task LoadVersionsAsync()
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

    /// <summary>
    /// 检查更新
    /// </summary>
    private async Task CheckUpdatesAsync()
    {
        if (IsCheckingUpdate) return;

        IsCheckingUpdate = true;
        CheckUpdateButtonText = "检查中...";

        // 清除之前的更新状态
        AppIsLatest = false;
        AppHasUpdate = false;
        PmhqIsLatest = false;
        PmhqHasUpdate = false;
        LLBotIsLatest = false;
        LLBotHasUpdate = false;

        try
        {
            _logger.LogInformation("开始检查更新...");

            // TODO: 实现实际的更新检查逻辑
            // 这里暂时模拟检查结果
            await Task.Delay(1000);

            // 暂时显示为最新版本
            AppIsLatest = true;
            PmhqIsLatest = true;
            LLBotIsLatest = true;

            CheckUpdateButtonText = "检查更新";
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

    /// <summary>
    /// 打开 URL
    /// </summary>
    private void OpenUrl(string url)
    {
        if (string.IsNullOrEmpty(url)) return;

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
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
