using Avalonia.Media;
using Avalonia.Threading;
using LuckyLilliaDesktop.Models;
using LuckyLilliaDesktop.Services;
using Microsoft.Extensions.Logging;
using ReactiveUI;
using Serilog;
using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Net.Http;
using System.Reactive;
using System.Reactive.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace LuckyLilliaDesktop.ViewModels;

public class HomeViewModel : ViewModelBase
{
    private readonly IProcessManager _processManager;
    private readonly IResourceMonitor _resourceMonitor;
    private readonly ISelfInfoService _selfInfoService;
    private readonly IConfigManager _configManager;
    private readonly IPmhqClient _pmhqClient;
    private readonly ILogCollector _logCollector;
    private readonly IDownloadService _downloadService;
    private readonly IUpdateChecker _updateChecker;
    private readonly IUpdateStateService _updateStateService;
    private readonly ILogger<HomeViewModel> _logger;
    private CancellationTokenSource? _downloadCts;
    private CancellationTokenSource? _infoPollingCts;
    private CancellationTokenSource? _sseCts;

    public Func<string, string, Task<bool>>? ConfirmDialog { get; set; }
    public Func<string, string, string, string, Task<int>>? ChoiceDialog { get; set; }
    public Func<int, Task<string?>>? ShowLoginDialog { get; set; }
    public Func<int, bool, Task<string?>>? ShowLoginDialogWithHeadless { get; set; }
    public Func<string, string, Task>? ShowAlertDialog { get; set; }

    // 标题
    private string _title = "控制面板";
    public string Title
    {
        get => _title;
        set => this.RaiseAndSetIfChanged(ref _title, value);
    }

    // 错误消息
    private string _errorMessage = string.Empty;
    public string ErrorMessage
    {
        get => _errorMessage;
        set
        {
            this.RaiseAndSetIfChanged(ref _errorMessage, value);
            this.RaisePropertyChanged(nameof(HasError));
        }
    }

    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

    // Bot 状态（整合 PMHQ + LLBot）
    private ProcessStatus _botStatus = ProcessStatus.Stopped;
    public ProcessStatus BotStatus
    {
        get => _botStatus;
        set
        {
            this.RaiseAndSetIfChanged(ref _botStatus, value);
            this.RaisePropertyChanged(nameof(BotStatusText));
            UpdateButtonState();
        }
    }

    public string BotStatusText => BotStatus switch
    {
        ProcessStatus.Running => "运行中",
        ProcessStatus.Starting => "启动中",
        ProcessStatus.Stopping => "停止中",
        ProcessStatus.Stopped => "未启动",
        _ => "未知"
    };

    // Bot 资源占用（PMHQ + LLBot 合计）
    private double _botCpu;
    public double BotCpu
    {
        get => _botCpu;
        set => this.RaiseAndSetIfChanged(ref _botCpu, value);
    }

    private double _botMemory;
    public double BotMemory
    {
        get => _botMemory;
        set => this.RaiseAndSetIfChanged(ref _botMemory, value);
    }

    private double _availableMemory;
    public double AvailableMemory
    {
        get => _availableMemory;
        set => this.RaiseAndSetIfChanged(ref _availableMemory, value);
    }

    // QQ 状态
    private ProcessStatus _qqStatus = ProcessStatus.Stopped;
    public ProcessStatus QQStatus
    {
        get => _qqStatus;
        set
        {
            this.RaiseAndSetIfChanged(ref _qqStatus, value);
            this.RaisePropertyChanged(nameof(QQStatusText));
        }
    }

    public string QQStatusText => QQStatus switch
    {
        ProcessStatus.Running => "运行中",
        ProcessStatus.Starting => "启动中",
        ProcessStatus.Stopping => "停止中",
        ProcessStatus.Stopped => "未运行",
        _ => "未知"
    };

    // QQ 资源占用
    private double _qqCpu;
    public double QQCpu
    {
        get => _qqCpu;
        set => this.RaiseAndSetIfChanged(ref _qqCpu, value);
    }

    private double _qqMemory;
    public double QQMemory
    {
        get => _qqMemory;
        set => this.RaiseAndSetIfChanged(ref _qqMemory, value);
    }

    // QQ 版本
    private string _qqVersion = string.Empty;
    public string QQVersion
    {
        get => _qqVersion;
        set => this.RaiseAndSetIfChanged(ref _qqVersion, value);
    }

    // QQ 信息
    private string _qqUin = string.Empty;
    public string QQUin
    {
        get => _qqUin;
        set
        {
            var oldValue = _qqUin;
            if (oldValue == value) return;
            
            this.RaiseAndSetIfChanged(ref _qqUin, value);
            UpdateTitle();
            this.RaisePropertyChanged(nameof(HasQQInfo));

            if (!string.IsNullOrEmpty(value))
            {
                _ = LoadAvatarAsync(value);
            }

            UpdateTrayMenu();
        }
    }

    private string _qqNickname = string.Empty;
    public string QQNickname
    {
        get => _qqNickname;
        set
        {
            if (_qqNickname == value) return;
            
            this.RaiseAndSetIfChanged(ref _qqNickname, value);
            UpdateTitle();
            this.RaisePropertyChanged(nameof(HasQQInfo));
            UpdateTrayMenu();
        }
    }

    private void UpdateTrayMenu()
    {
        if (Avalonia.Application.Current is App app)
        {
            app.UpdateTrayMenuText(QQNickname, QQUin);
        }
    }

    public bool HasQQInfo => !string.IsNullOrEmpty(QQUin);

    private Avalonia.Media.Imaging.Bitmap? _avatarBitmap;
    public Avalonia.Media.Imaging.Bitmap? AvatarBitmap
    {
        get => _avatarBitmap;
        set => this.RaiseAndSetIfChanged(ref _avatarBitmap, value);
    }

    private async Task LoadAvatarAsync(string uin)
    {
        if (string.IsNullOrEmpty(uin)) return;

        try
        {
            var url = $"https://q1.qlogo.cn/g?b=qq&nk={uin}&s=640";
            using var httpClient = new System.Net.Http.HttpClient();
            var bytes = await httpClient.GetByteArrayAsync(url);
            using var stream = new MemoryStream(bytes);
            AvatarBitmap = new Avalonia.Media.Imaging.Bitmap(stream);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "加载头像失败");
        }
    }

    // 最近日志
    public ObservableCollection<LogEntryViewModel> RecentLogs { get; } = new();

    private bool _hasRecentLogs;
    public bool HasRecentLogs
    {
        get => _hasRecentLogs;
        set => this.RaiseAndSetIfChanged(ref _hasRecentLogs, value);
    }

    // 更新状态
    private bool _hasUpdate;
    public bool HasUpdate
    {
        get => _hasUpdate;
        set => this.RaiseAndSetIfChanged(ref _hasUpdate, value);
    }

    private string _updateBannerText = "发现新版本！";
    public string UpdateBannerText
    {
        get => _updateBannerText;
        set => this.RaiseAndSetIfChanged(ref _updateBannerText, value);
    }

    // 启动按钮状态
    private bool _isServicesRunning;
    public bool IsServicesRunning
    {
        get => _isServicesRunning;
        set
        {
            this.RaiseAndSetIfChanged(ref _isServicesRunning, value);
            UpdateButtonState();
        }
    }

    private string _startButtonText = "启动";
    public string StartButtonText
    {
        get => _startButtonText;
        set => this.RaiseAndSetIfChanged(ref _startButtonText, value);
    }

    private string _startButtonIcon = "M8 5v14l11-7z"; // Play icon
    public string StartButtonIcon
    {
        get => _startButtonIcon;
        set => this.RaiseAndSetIfChanged(ref _startButtonIcon, value);
    }

    private IBrush _startButtonBackground = new SolidColorBrush(Color.Parse("#10B981"));
    public IBrush StartButtonBackground
    {
        get => _startButtonBackground;
        set => this.RaiseAndSetIfChanged(ref _startButtonBackground, value);
    }

    private bool _isButtonEnabled = true;
    public bool IsButtonEnabled
    {
        get => _isButtonEnabled;
        set => this.RaiseAndSetIfChanged(ref _isButtonEnabled, value);
    }

    // 导航回调
    public Action? NavigateToLogs { get; set; }
    public Action? NavigateToAbout { get; set; }
    public Func<Task>? OnDownloadCompleted { get; set; }

    /// <summary>
    /// 启动所有服务（供外部调用）
    /// </summary>
    public async Task StartServicesAsync() => await StartAllServicesAsync();

    // 下载状态
    private bool _isDownloading;
    public bool IsDownloading
    {
        get => _isDownloading;
        set => this.RaiseAndSetIfChanged(ref _isDownloading, value);
    }

    private string _downloadStatus = "";
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

    private string _downloadingItem = "";
    public string DownloadingItem
    {
        get => _downloadingItem;
        set => this.RaiseAndSetIfChanged(ref _downloadingItem, value);
    }

    // 命令
    public ReactiveCommand<Unit, Unit> GlobalStartStopCommand { get; }
    public ReactiveCommand<Unit, Unit> ViewAllLogsCommand { get; }
    public ReactiveCommand<Unit, Unit> UpdateCommand { get; }
    public ReactiveCommand<Unit, Unit> CancelDownloadCommand { get; }

    public HomeViewModel(
        IProcessManager processManager,
        IResourceMonitor resourceMonitor,
        ISelfInfoService selfInfoService,
        IConfigManager configManager,
        IPmhqClient pmhqClient,
        ILogCollector logCollector,
        IDownloadService downloadService,
        IUpdateChecker updateChecker,
        IUpdateStateService updateStateService,
        ILogger<HomeViewModel> logger)
    {
        _processManager = processManager;
        _resourceMonitor = resourceMonitor;
        _selfInfoService = selfInfoService;
        _configManager = configManager;
        _pmhqClient = pmhqClient;
        _logCollector = logCollector;
        _downloadService = downloadService;
        _updateChecker = updateChecker;
        _updateStateService = updateStateService;
        _logger = logger;

        // 订阅资源监控流
        _resourceMonitor.ResourceStream
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(OnResourceUpdate);

        // 订阅可用内存流
        _resourceMonitor.AvailableMemoryStream
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(mem => AvailableMemory = mem);

        _selfInfoService.UinStream
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(uin =>
            {
                QQUin = uin ?? string.Empty;
                QQStatus = string.IsNullOrEmpty(uin) ? ProcessStatus.Stopped : ProcessStatus.Running;
                if (!string.IsNullOrEmpty(uin))
                    _logger.LogInformation("UIN 已更新: {Uin}", uin);
            });

        _selfInfoService.NicknameStream
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(nickname =>
            {
                QQNickname = nickname ?? string.Empty;
                if (!string.IsNullOrEmpty(nickname))
                    _logger.LogInformation("昵称已更新: {Nickname}", nickname);
            });

        _resourceMonitor.QQVersionStream
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(version => QQVersion = version);

        // 订阅进程状态变化
        _processManager.ProcessStatusChanged += OnProcessStatusChanged;

        // 订阅更新状态变化
        _updateStateService.StateChanged
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(OnUpdateStateChanged);

        // 订阅日志流（最近10条），批量处理避免 UI 卡顿
        _logCollector.LogStream
            .Buffer(TimeSpan.FromMilliseconds(100))
            .Where(batch => batch.Count > 0)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(OnLogBatchReceived);

        // 全局启动/停止命令
        var canExecute = this.WhenAnyValue(x => x.IsButtonEnabled);
        GlobalStartStopCommand = ReactiveCommand.Create(() => { _ = GlobalStartStopAsync(); }, canExecute);

        // 查看全部日志命令
        ViewAllLogsCommand = ReactiveCommand.Create(() =>
        {
            _logger.LogDebug("导航到日志页面");
            NavigateToLogs?.Invoke();
        });

        // 更新命令 - 导航到关于页面
        UpdateCommand = ReactiveCommand.Create(() =>
        {
            _logger.LogInformation("用户点击更新按钮，导航到关于页面");
            NavigateToAbout?.Invoke();
        });

        // 取消下载命令
        CancelDownloadCommand = ReactiveCommand.Create(() =>
        {
            _logger.LogInformation("用户取消下载");
            _downloadCts?.Cancel();
            IsDownloading = false;
            DownloadStatus = "下载已取消";
        });

        // 初始化时启动资源监控
        _ = _resourceMonitor.StartMonitoringAsync();

        // 加载最近日志
        LoadRecentLogs();

        // 检查更新
        _ = CheckForUpdatesAsync();

        // 检查是否需要自动启动 Bot
        _ = CheckAutoStartBotAsync();
    }

    private async Task CheckAutoStartBotAsync()
    {
        try
        {
            var config = await _configManager.LoadConfigAsync();
            if (config.AutoStartBot)
            {
                _logger.LogInformation("配置启用了自动启动 Bot，正在启动...");
                await Task.Delay(1000);
                await StartAllServicesAsync();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "检查自动启动配置失败");
        }
    }

    private async Task ExecuteStartupCommandAsync(AppConfig config)
    {
        if (!config.StartupCommandEnabled || string.IsNullOrWhiteSpace(config.StartupCommand))
            return;

        try
        {
            _logger.LogInformation("执行启动后命令: {Command}", config.StartupCommand);
            await Task.Run(() =>
            {
                var process = new System.Diagnostics.Process
                {
                    StartInfo = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "cmd.exe",
                        Arguments = $"/c start cmd /c \"{config.StartupCommand} & pause\"",
                        UseShellExecute = true,
                        CreateNoWindow = false
                    }
                };
                process.Start();
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "执行启动后命令失败");
        }
    }

    /// <summary>
    /// 检查组件更新
    /// </summary>
    private async Task CheckForUpdatesAsync()
    {
        try
        {
            _logger.LogInformation("开始检查组件更新...");

            var config = await _configManager.LoadConfigAsync();
            var appVersion = GetAppVersion();
            var pmhqVersion = DetectPmhqVersion(config.PmhqPath);
            var llbotVersion = DetectLLBotVersion(config.LLBotPath);

            var updateNames = new System.Collections.Generic.List<string>();
            var state = new UpdateState { IsChecked = true };

            // 检查应用更新
            var appUpdate = await _updateChecker.CheckAppUpdateAsync(appVersion);
            if (appUpdate.HasUpdate)
            {
                updateNames.Add("管理器");
                state.AppHasUpdate = true;
                state.AppLatestVersion = appUpdate.LatestVersion;
                state.AppReleaseUrl = appUpdate.ReleaseUrl;
                _logger.LogInformation("发现管理器新版本: {Version}", appUpdate.LatestVersion);
            }

            // 检查 PMHQ 更新
            if (!string.IsNullOrEmpty(pmhqVersion))
            {
                var pmhqUpdate = await _updateChecker.CheckPmhqUpdateAsync(pmhqVersion);
                if (pmhqUpdate.HasUpdate)
                {
                    updateNames.Add("PMHQ");
                    state.PmhqHasUpdate = true;
                    state.PmhqLatestVersion = pmhqUpdate.LatestVersion;
                    state.PmhqReleaseUrl = pmhqUpdate.ReleaseUrl;
                    _logger.LogInformation("发现 PMHQ 新版本: {Version}", pmhqUpdate.LatestVersion);
                }
            }

            // 检查 LLBot 更新
            if (!string.IsNullOrEmpty(llbotVersion))
            {
                var llbotUpdate = await _updateChecker.CheckLLBotUpdateAsync(llbotVersion);
                if (llbotUpdate.HasUpdate)
                {
                    updateNames.Add("LLBot");
                    state.LLBotHasUpdate = true;
                    state.LLBotLatestVersion = llbotUpdate.LatestVersion;
                    state.LLBotReleaseUrl = llbotUpdate.ReleaseUrl;
                    _logger.LogInformation("发现 LLBot 新版本: {Version}", llbotUpdate.LatestVersion);
                }
            }

            // 发布更新状态
            _updateStateService.UpdateState(state);

            // 更新 UI
            if (updateNames.Count > 0)
            {
                HasUpdate = true;
                UpdateBannerText = $"发现新版本: {string.Join(", ", updateNames)}";
                _logger.LogInformation("发现更新: {Updates}", string.Join(", ", updateNames));
            }
            else
            {
                HasUpdate = false;
                _logger.LogInformation("所有组件已是最新版本");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "检查更新失败");
        }
    }

    private string GetAppVersion()
    {
        var assembly = System.Reflection.Assembly.GetExecutingAssembly();
        var version = assembly.GetName().Version;
        return version != null
            ? $"{version.Major}.{version.Minor}.{version.Build}"
            : "1.0.0";
    }

    private string? DetectPmhqVersion(string? pmhqPath)
    {
        if (string.IsNullOrEmpty(pmhqPath)) return null;
        try
        {
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
        catch { }
        return null;
    }

    private string? DetectLLBotVersion(string? llbotPath)
    {
        if (string.IsNullOrEmpty(llbotPath)) return null;
        try
        {
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
        catch { }
        return null;
    }

    private void UpdateTitle()
    {
        // 标题固定为"控制面板"，不随登录状态改变
    }

    private void UpdateButtonState()
    {
        if (BotStatus == ProcessStatus.Starting)
        {
            StartButtonText = "启动中";
            StartButtonIcon = "M12 2C6.48 2 2 6.48 2 12s4.48 10 10 10 10-4.48 10-10S17.52 2 12 2zm-2 15l-5-5 1.41-1.41L10 14.17l7.59-7.59L19 8l-9 9z";
            StartButtonBackground = new SolidColorBrush(Color.Parse("#F97316"));
            IsButtonEnabled = false;
        }
        else if (BotStatus == ProcessStatus.Stopping)
        {
            StartButtonText = "停止中";
            StartButtonIcon = "M12 2C6.48 2 2 6.48 2 12s4.48 10 10 10 10-4.48 10-10S17.52 2 12 2zm-2 15l-5-5 1.41-1.41L10 14.17l7.59-7.59L19 8l-9 9z";
            StartButtonBackground = new SolidColorBrush(Color.Parse("#F97316"));
            IsButtonEnabled = false;
        }
        else if (IsServicesRunning && BotStatus == ProcessStatus.Running)
        {
            StartButtonText = "停止";
            StartButtonIcon = "M6 6h12v12H6z";
            StartButtonBackground = new SolidColorBrush(Color.Parse("#EF4444"));
            IsButtonEnabled = true;
        }
        else if (BotStatus == ProcessStatus.Running)
        {
            // Running 但 IsServicesRunning 还没设置，保持启动中状态
            StartButtonText = "启动中";
            StartButtonIcon = "M12 2C6.48 2 2 6.48 2 12s4.48 10 10 10 10-4.48 10-10S17.52 2 12 2zm-2 15l-5-5 1.41-1.41L10 14.17l7.59-7.59L19 8l-9 9z";
            StartButtonBackground = new SolidColorBrush(Color.Parse("#F97316"));
            IsButtonEnabled = false;
        }
        else
        {
            StartButtonText = "启动";
            StartButtonIcon = "M8 5v14l11-7z";
            StartButtonBackground = new SolidColorBrush(Color.Parse("#10B981"));
            IsButtonEnabled = true;
        }
    }

    private async Task GlobalStartStopAsync()
    {
        if (IsServicesRunning || BotStatus == ProcessStatus.Running)
        {
            _logger.LogInformation("用户请求停止服务");
            if (ConfirmDialog != null)
            {
                var confirmed = await ConfirmDialog("确认停止", "确定要停止所有服务吗？");
                if (!confirmed)
                {
                    _logger.LogDebug("用户取消停止操作");
                    return;
                }
            }
            await StopAllServicesAsync();
        }
        else
        {
            _logger.LogInformation("用户请求启动服务");
            await StartAllServicesAsync();
        }
    }

    private async Task StartAllServicesAsync()
    {
        try
        {
            ErrorMessage = string.Empty;
            _logger.LogInformation("启动所有服务...");
            BotStatus = ProcessStatus.Starting;

            var config = await _configManager.LoadConfigAsync();

            // 如果 QQ 路径为空，尝试自动检测
            if (string.IsNullOrEmpty(config.QQPath))
            {
                var detectedPath = Utils.QQPathHelper.GetQQPathFromRegistry();
                if (!string.IsNullOrEmpty(detectedPath))
                {
                    config.QQPath = detectedPath;
                    _logger.LogInformation("自动检测到 QQ 路径: {Path}", detectedPath);
                }
                else
                {
                    // 未检测到 QQ，询问用户
                    if (ChoiceDialog != null)
                    {
                        var choice = await ChoiceDialog(
                            "未检测到 QQ",
                            "未检测到已安装的 QQ，请选择操作：",
                            "自动安装 QQ",
                            "手动选择 QQ 路径");

                        if (choice == 0)
                        {
                            // 自动安装 QQ
                            _downloadCts = new CancellationTokenSource();
                            IsDownloading = true;
                            DownloadingItem = "QQ";

                            try
                            {
                                var progress = new Progress<DownloadProgress>(p =>
                                {
                                    DownloadProgress = p.Percentage;
                                    DownloadStatus = p.Status;
                                });

                                var success = await _downloadService.DownloadQQAsync(progress, _downloadCts.Token);
                                if (success)
                                {
                                    config.QQPath = Utils.QQPathHelper.GetQQPathFromRegistry() ?? string.Empty;
                                    _logger.LogInformation("QQ 安装完成");
                                }
                                else
                                {
                                    ErrorMessage = "QQ 安装失败";
                                    BotStatus = ProcessStatus.Stopped;
                                    return;
                                }
                            }
                            finally
                            {
                                IsDownloading = false;
                                DownloadingItem = "";
                                _downloadCts = null;
                            }
                        }
                        else if (choice == 1)
                        {
                            // 用户选择手动选择，提示去配置页面
                            ErrorMessage = "请在系统配置中设置 QQ 路径";
                            BotStatus = ProcessStatus.Stopped;
                            return;
                        }
                        else
                        {
                            // 用户取消
                            BotStatus = ProcessStatus.Stopped;
                            return;
                        }
                    }
                    else
                    {
                        ErrorMessage = "未检测到 QQ，请在系统配置中设置 QQ 路径";
                        BotStatus = ProcessStatus.Stopped;
                        return;
                    }
                }
            }

            _logger.LogInformation("配置加载完成: PmhqPath={PmhqPath}, NodePath={NodePath}, LLBotPath={LLBotPath}, QQPath={QQPath}",
                config.PmhqPath, config.NodePath, config.LLBotPath, config.QQPath);

            // 检查并下载缺失的文件
            var pmhqExists = !string.IsNullOrEmpty(config.PmhqPath) && File.Exists(config.PmhqPath);

            // 检查 Node.js：优先使用 PATH 中的 Node.js（版本 >= 22）
            var nodeExists = false;
            var systemNode = Utils.NodeHelper.FindNodeInPath();
            if (!string.IsNullOrEmpty(systemNode))
            {
                if (await Utils.NodeHelper.CheckNodeVersionValidAsync(systemNode, 22, _logger))
                {
                    _logger.LogInformation("在系统PATH中找到Node.js (版本>=22): {Path}", systemNode);
                    config.NodePath = systemNode;
                    nodeExists = true;
                }
                else
                {
                    _logger.LogWarning("系统PATH中的Node.js版本低于22: {Path}", systemNode);
                }
            }

            // 如果 PATH 中没有合适的 Node.js，检查本地 bin/llbot/node.exe
            if (!nodeExists)
            {
                var localNodePath = Utils.Constants.DefaultPaths.NodeExe;
                if (File.Exists(localNodePath))
                {
                    if (await Utils.NodeHelper.CheckNodeVersionValidAsync(localNodePath, 22, _logger))
                    {
                        _logger.LogInformation("在本地目录找到Node.js: {Path}", localNodePath);
                        config.NodePath = localNodePath;
                        nodeExists = true;
                    }
                    else
                    {
                        _logger.LogWarning("本地Node.js版本低于22: {Path}", localNodePath);
                    }
                }
            }

            var llbotExists = !string.IsNullOrEmpty(config.LLBotPath) && File.Exists(config.LLBotPath);

            // 检查 FFmpeg 和 FFprobe
            var ffmpegExists = Utils.FFmpegHelper.CheckFFmpegExists();
            var ffprobeExists = Utils.FFmpegHelper.CheckFFprobeExists();

            if (ffmpegExists)
            {
                var systemFFmpeg = Utils.FFmpegHelper.FindFFmpegInPath();
                if (!string.IsNullOrEmpty(systemFFmpeg))
                {
                    _logger.LogInformation("在系统PATH中找到FFmpeg: {Path}", systemFFmpeg);
                }
                else
                {
                    _logger.LogInformation("在本地目录找到FFmpeg: {Path}", Utils.Constants.DefaultPaths.FFmpegExe);
                }
            }
            _logger.LogInformation("FFmpeg可用: {Available}", ffmpegExists);
            _logger.LogInformation("FFprobe可用: {Available}", ffprobeExists);

            // 如果任何文件不存在，尝试下载
            if (!pmhqExists || !nodeExists || !llbotExists || !ffmpegExists || !ffprobeExists)
            {
                _logger.LogInformation("检测到缺失文件，开始下载...");

                _downloadCts = new CancellationTokenSource();
                IsDownloading = true;

                try
                {
                    var progress = new Progress<DownloadProgress>(p =>
                    {
                        DownloadProgress = p.Percentage;
                        DownloadStatus = p.Status;
                    });

                    // 下载 PMHQ
                    if (!pmhqExists)
                    {
                        DownloadingItem = "PMHQ";
                        _logger.LogInformation("下载 PMHQ...");
                        var success = await _downloadService.DownloadPmhqAsync(progress, _downloadCts.Token);
                        if (!success)
                        {
                            ErrorMessage = "PMHQ 下载失败";
                            BotStatus = ProcessStatus.Stopped;
                            return;
                        }
                        // 更新配置路径
                        config.PmhqPath = Utils.Constants.DefaultPaths.PmhqExe;
                    }

                    // 下载 Node.js
                    if (!nodeExists)
                    {
                        DownloadingItem = "Node.js";
                        _logger.LogInformation("下载 Node.js...");
                        var success = await _downloadService.DownloadNodeAsync(progress, _downloadCts.Token);
                        if (!success)
                        {
                            ErrorMessage = "Node.js 下载失败";
                            BotStatus = ProcessStatus.Stopped;
                            return;
                        }
                        config.NodePath = Utils.Constants.DefaultPaths.NodeExe;
                    }

                    // 下载 LLBot
                    if (!llbotExists)
                    {
                        DownloadingItem = "LLBot";
                        _logger.LogInformation("下载 LLBot...");
                        var success = await _downloadService.DownloadLLBotAsync(progress, _downloadCts.Token);
                        if (!success)
                        {
                            ErrorMessage = "LLBot 下载失败";
                            BotStatus = ProcessStatus.Stopped;
                            return;
                        }
                        config.LLBotPath = Utils.Constants.DefaultPaths.LLBotScript;
                    }

                    // 下载 FFmpeg
                    if (!ffmpegExists || !ffprobeExists)
                    {
                        DownloadingItem = "FFmpeg";
                        _logger.LogInformation("下载 FFmpeg...");
                        var success = await _downloadService.DownloadFFmpegAsync(progress, _downloadCts.Token);
                        if (!success)
                        {
                            ErrorMessage = "FFmpeg 下载失败";
                            BotStatus = ProcessStatus.Stopped;
                            return;
                        }
                    }

                    // 保存更新后的配置
                    await _configManager.SaveConfigAsync(config);
                    _logger.LogInformation("下载完成，配置已更新");

                    // 通知版本信息刷新
                    if (OnDownloadCompleted != null)
                    {
                        await OnDownloadCompleted();
                    }
                }
                finally
                {
                    IsDownloading = false;
                    DownloadingItem = "";
                    _downloadCts = null;
                }
            }

            // 再次验证文件是否存在
            if (!File.Exists(config.PmhqPath))
            {
                ErrorMessage = $"PMHQ 文件不存在: {config.PmhqPath}";
                BotStatus = ProcessStatus.Stopped;
                return;
            }

            if (!File.Exists(config.NodePath))
            {
                ErrorMessage = $"Node.js 文件不存在: {config.NodePath}";
                BotStatus = ProcessStatus.Stopped;
                return;
            }

            if (!File.Exists(config.LLBotPath))
            {
                ErrorMessage = $"LLBot 脚本不存在: {config.LLBotPath}";
                BotStatus = ProcessStatus.Stopped;
                return;
            }

            // 验证 FFmpeg 文件
            if (!Utils.FFmpegHelper.CheckFFmpegExists())
            {
                ErrorMessage = "FFmpeg 不可用";
                BotStatus = ProcessStatus.Stopped;
                return;
            }

            if (!Utils.FFmpegHelper.CheckFFprobeExists())
            {
                ErrorMessage = "FFprobe 不可用";
                BotStatus = ProcessStatus.Stopped;
                return;
            }

            // 启动 PMHQ
            _logger.LogInformation("正在启动 PMHQ...");
            var pmhqSuccess = await _processManager.StartPmhqAsync(
                config.PmhqPath,
                config.QQPath,
                config.AutoLoginQQ,
                config.Headless);

            if (pmhqSuccess)
            {
                _logger.LogInformation("PMHQ 启动成功");

                // 设置 PmhqClient 端口并开始轮询
                if (_processManager.PmhqPort.HasValue)
                {
                    _pmhqClient.SetPort(_processManager.PmhqPort.Value);

                    // 如果设置了自动登录QQ号，启动 SSE 监听以捕获登录失败事件
                    if (!string.IsNullOrEmpty(config.AutoLoginQQ) && config.Headless)
                    {
                        StartSSEListener(_processManager.PmhqPort.Value);
                    }
                }

                // 无头模式且没有自动登录QQ号时，显示登录对话框
                if (config.Headless && string.IsNullOrEmpty(config.AutoLoginQQ))
                {
                    _logger.LogInformation("无头模式，显示登录对话框");
                    if (ShowLoginDialogWithHeadless != null && _processManager.PmhqPort.HasValue)
                    {
                        var loggedInUin = await ShowLoginDialogWithHeadless(_processManager.PmhqPort.Value, true);
                        if (string.IsNullOrEmpty(loggedInUin))
                        {
                            _logger.LogWarning("用户取消登录");
                            await _processManager.StopPmhqAsync();
                            BotStatus = ProcessStatus.Stopped;
                            return;
                        }
                        _logger.LogInformation("登录成功: {Uin}", loggedInUin);
                    }
                }

                StartInfoPolling();

                // 等待一段时间后启动 LLBot
                await Task.Delay(2000);

                // 启动 LLBot
                _logger.LogInformation("正在启动 LLBot...");
                var llbotSuccess = await _processManager.StartLLBotAsync(
                    config.NodePath,
                    config.LLBotPath);

                if (llbotSuccess)
                {
                    _logger.LogInformation("LLBot 启动成功");
                    IsServicesRunning = true;
                    BotStatus = ProcessStatus.Running;

                    // 执行启动后命令
                    await ExecuteStartupCommandAsync(config);
                }
                else
                {
                    ErrorMessage = "LLBot 启动失败，请检查日志";
                    _logger.LogError("LLBot 启动失败");
                    BotStatus = ProcessStatus.Stopped;
                }
            }
            else
            {
                ErrorMessage = "PMHQ 启动失败，请检查日志";
                _logger.LogError("PMHQ 启动失败");
                BotStatus = ProcessStatus.Stopped;
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = $"启动服务时出错: {ex.Message}";
            _logger.LogError(ex, "启动服务时出错");
            BotStatus = ProcessStatus.Stopped;
        }
    }

    private async Task StopAllServicesAsync()
    {
        try
        {
            _logger.LogInformation("停止所有服务...");
            BotStatus = ProcessStatus.Stopping;

            _infoPollingCts?.Cancel();
            _pmhqClient.CancelAll();
            _pmhqClient.ClearPort();
            
            // 在 ResetState 之前获取 QQ PID
            var qqPid = _resourceMonitor.QQPid;
            _resourceMonitor.ResetState();

            await _processManager.StopAllAsync(qqPid);

            IsServicesRunning = false;
            BotStatus = ProcessStatus.Stopped;
            QQStatus = ProcessStatus.Stopped;

            QQUin = string.Empty;
            QQNickname = string.Empty;
            QQVersion = string.Empty;
            AvatarBitmap = null;

            _logger.LogInformation("所有服务已停止");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "停止服务时出错");
            BotStatus = ProcessStatus.Stopped;
        }
    }

    private void StartInfoPolling()
    {
        _infoPollingCts?.Cancel();
        _infoPollingCts = new CancellationTokenSource();
        var ct = _infoPollingCts.Token;

        _ = Task.Run(async () =>
        {
            _logger.LogInformation("等待 PMHQ API 可用...");
            while (!ct.IsCancellationRequested)
            {
                var selfInfo = await _pmhqClient.FetchSelfInfoAsync(ct);
                if (selfInfo != null)
                {
                    _logger.LogInformation("PMHQ API 已可用");
                    if (!string.IsNullOrEmpty(selfInfo.Uin))
                    {
                        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                        {
                            QQUin = selfInfo.Uin;
                            QQNickname = selfInfo.Nickname;
                        });
                    }
                    break;
                }

                try { await Task.Delay(1000, ct); }
                catch (OperationCanceledException) { return; }
            }

            // 获取 QQ 版本（只获取一次）
            string? cachedVersion = null;
            _logger.LogInformation("开始获取 QQ 版本...");
            while (!ct.IsCancellationRequested && string.IsNullOrEmpty(cachedVersion))
            {
                try
                {
                    _logger.LogDebug("正在调用 FetchDeviceInfoAsync...");
                    var deviceInfo = await _pmhqClient.FetchDeviceInfoAsync(ct);
                    _logger.LogDebug("FetchDeviceInfoAsync 返回: {DeviceInfo}", deviceInfo != null ? $"BuildVer={deviceInfo.BuildVer}" : "null");

                    if (deviceInfo != null && !string.IsNullOrEmpty(deviceInfo.BuildVer))
                    {
                        cachedVersion = deviceInfo.BuildVer;
                        _logger.LogInformation("成功获取 QQ 版本: {Version}", cachedVersion);
                        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                        {
                            QQVersion = cachedVersion;
                            _logger.LogInformation("QQVersion 属性已设置为: {Version}", QQVersion);
                        });
                        break;
                    }
                    else
                    {
                        _logger.LogDebug("未获取到 QQ 版本，1秒后重试");
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "获取 QQ 版本时出错");
                }

                try { await Task.Delay(1000, ct); }
                catch (OperationCanceledException) { return; }
            }

            // 继续轮询 SelfInfo 已由 SelfInfoService 处理
        }, ct);
    }

    private void LoadRecentLogs()
    {
        var logs = _logCollector.GetRecentLogs(10);
        foreach (var log in logs)
        {
            RecentLogs.Add(new LogEntryViewModel(log));
        }
        HasRecentLogs = RecentLogs.Count > 0;
    }

    private void OnUpdateStateChanged(UpdateState state)
    {
        HasUpdate = state.HasAnyUpdate;
        if (state.HasAnyUpdate)
        {
            var names = new System.Collections.Generic.List<string>();
            if (state.AppHasUpdate) names.Add("管理器");
            if (state.PmhqHasUpdate) names.Add("PMHQ");
            if (state.LLBotHasUpdate) names.Add("LLBot");
            UpdateBannerText = $"发现新版本: {string.Join(", ", names)}";
        }
    }

    private void OnLogBatchReceived(System.Collections.Generic.IList<LogEntry> batch)
    {
        foreach (var logEntry in batch)
        {
            RecentLogs.Add(new LogEntryViewModel(logEntry));
        }

        while (RecentLogs.Count > 10)
        {
            RecentLogs.RemoveAt(0);
        }

        HasRecentLogs = RecentLogs.Count > 0;
    }

    private double _pmhqCpu;
    private double _pmhqMemory;
    private double _llbotCpu;
    private double _llbotMemory;
    private double _managerCpu;
    private double _managerMemory;
    private double _nodeCpu;
    private double _nodeMemory;

    private void OnResourceUpdate(ProcessResourceInfo resource)
    {
        switch (resource.ProcessName.ToLower())
        {
            case "pmhq":
                _pmhqCpu = resource.CpuPercent;
                _pmhqMemory = resource.MemoryMB;
                UpdateBotResources();
                break;
            case "llbot":
                _llbotCpu = resource.CpuPercent;
                _llbotMemory = resource.MemoryMB;
                UpdateBotResources();
                break;
            case "manager":
            case "luckylilliadesktop":
                _managerCpu = resource.CpuPercent;
                _managerMemory = resource.MemoryMB;
                UpdateBotResources();
                break;
            case "node":
                _nodeCpu = resource.CpuPercent;
                _nodeMemory = resource.MemoryMB;
                UpdateBotResources();
                break;
            case "qq":
                QQCpu = resource.CpuPercent;
                QQMemory = resource.MemoryMB;
                if (resource.CpuPercent > 0 || resource.MemoryMB > 0)
                {
                    QQStatus = ProcessStatus.Running;
                }
                else
                {
                    QQStatus = ProcessStatus.Stopped;
                }
                break;
        }
    }

    private void UpdateBotResources()
    {
        // Bot 占用 = 管理器 + PMHQ + Node.js + LLBot
        BotCpu = _managerCpu + _pmhqCpu + _nodeCpu + _llbotCpu;
        BotMemory = _managerMemory + _pmhqMemory + _nodeMemory + _llbotMemory;
    }

    private void OnProcessStatusChanged(object? sender, ProcessStatus status)
    {
        var pmhqStatus = _processManager.GetProcessStatus("PMHQ");
        var llbotStatus = _processManager.GetProcessStatus("LLBot");

        // Bot 状态 = PMHQ 和 LLBot 综合状态
        if (pmhqStatus == ProcessStatus.Running || llbotStatus == ProcessStatus.Running)
        {
            BotStatus = ProcessStatus.Running;
            IsServicesRunning = true;
        }
        else if (pmhqStatus == ProcessStatus.Starting || llbotStatus == ProcessStatus.Starting)
        {
            BotStatus = ProcessStatus.Starting;
        }
        else if (pmhqStatus == ProcessStatus.Stopping || llbotStatus == ProcessStatus.Stopping)
        {
            BotStatus = ProcessStatus.Stopping;
        }
        else
        {
            BotStatus = ProcessStatus.Stopped;
            IsServicesRunning = false;
        }
    }

    private void StartSSEListener(int port)
    {
        _sseCts?.Cancel();
        _sseCts = new CancellationTokenSource();
        var ct = _sseCts.Token;

        _ = Task.Run(async () =>
        {
            var url = $"http://127.0.0.1:{port}";
            Log.Information("[SSE] 开始监听: {Url}", url);

            while (!ct.IsCancellationRequested)
            {
                try
                {
                    using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
                    Log.Information("[SSE] 连接中...");

                    using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
                    Log.Information("[SSE] 已连接, StatusCode={StatusCode}", response.StatusCode);

                    await using var stream = await response.Content.ReadAsStreamAsync(ct);
                    using var reader = new StreamReader(stream, Encoding.UTF8);

                    var buffer = new StringBuilder();

                    while (!ct.IsCancellationRequested)
                    {
                        var chunk = new char[4096];
                        var read = await reader.ReadAsync(chunk, 0, chunk.Length);
                        if (read == 0) break;

                        buffer.Append(chunk, 0, read);
                        var content = buffer.ToString();

                        while (content.Contains("\n\n"))
                        {
                            var idx = content.IndexOf("\n\n", StringComparison.Ordinal);
                            var message = content[..idx];
                            content = content[(idx + 2)..];
                            buffer.Clear();
                            buffer.Append(content);

                            ProcessSSEMessage(message);
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "[SSE] 连接异常");
                    if (!ct.IsCancellationRequested)
                        await Task.Delay(1000, ct);
                }
            }
            Log.Information("[SSE] 监听结束");
        }, ct);
    }

    private void ProcessSSEMessage(string message)
    {
        foreach (var line in message.Split('\n'))
        {
            var trimmed = line.Trim();
            if (!trimmed.StartsWith("data:")) continue;

            var json = trimmed[5..].Trim();
            if (string.IsNullOrEmpty(json)) continue;

            Log.Information("[SSE] 收到: {Json}", json);

            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (root.TryGetProperty("type", out var typeElem) &&
                    typeElem.GetString() == "nodeIKernelLoginListener" &&
                    root.TryGetProperty("data", out var dataElem) &&
                    dataElem.TryGetProperty("sub_type", out var subType))
                {
                    var subTypeStr = subType.GetString();
                    Log.Information("[SSE] 事件: {SubType}", subTypeStr);

                    if (subTypeStr == "onQuickLoginFailed" &&
                        dataElem.TryGetProperty("data", out var failData))
                    {
                        var errMsg = "登录失败";
                        if (failData.TryGetProperty("loginErrorInfo", out var errorInfo) &&
                            errorInfo.TryGetProperty("errMsg", out var errMsgElem))
                        {
                            errMsg = errMsgElem.GetString() ?? "登录失败";
                        }

                        Log.Warning("[SSE] 自动登录失败: {ErrMsg}", errMsg);
                        _sseCts?.Cancel();

                        Dispatcher.UIThread.Post(async () =>
                        {
                            await HandleAutoLoginFailedAsync(errMsg);
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[SSE] 解析失败");
            }
        }
    }

    private async Task HandleAutoLoginFailedAsync(string errMsg)
    {
        // 显示错误提示
        if (ShowAlertDialog != null)
        {
            await ShowAlertDialog("登录失败", errMsg);
        }

        // 弹出登录对话框
        if (ShowLoginDialogWithHeadless != null && _processManager.PmhqPort.HasValue)
        {
            var loggedInUin = await ShowLoginDialogWithHeadless(_processManager.PmhqPort.Value, true);
            if (string.IsNullOrEmpty(loggedInUin))
            {
                _logger.LogWarning("用户取消登录");
                await StopAllServicesAsync();
            }
            else
            {
                _logger.LogInformation("登录成功: {Uin}", loggedInUin);
            }
        }
    }

    private void StopSSEListener()
    {
        _sseCts?.Cancel();
        _sseCts = null;
    }
}
