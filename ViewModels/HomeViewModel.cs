using Avalonia.Media;
using LuckyLilliaDesktop.Models;
using LuckyLilliaDesktop.Services;
using Microsoft.Extensions.Logging;
using ReactiveUI;
using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Reactive;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace LuckyLilliaDesktop.ViewModels;

public class HomeViewModel : ViewModelBase
{
    private readonly IProcessManager _processManager;
    private readonly IResourceMonitor _resourceMonitor;
    private readonly IConfigManager _configManager;
    private readonly IPmhqClient _pmhqClient;
    private readonly ILogCollector _logCollector;
    private readonly IDownloadService _downloadService;
    private readonly IUpdateChecker _updateChecker;
    private readonly IUpdateStateService _updateStateService;
    private readonly ILogger<HomeViewModel> _logger;
    private CancellationTokenSource? _downloadCts;
    private CancellationTokenSource? _infoPollingCts;

    public Func<string, string, Task<bool>>? ConfirmDialog { get; set; }
    public Func<string, string, string, string, Task<int>>? ChoiceDialog { get; set; }

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

    // QQ 信息
    private string _qqUin = string.Empty;
    public string QQUin
    {
        get => _qqUin;
        set
        {
            var oldValue = _qqUin;
            this.RaiseAndSetIfChanged(ref _qqUin, value);
            UpdateTitle();
            this.RaisePropertyChanged(nameof(HasQQInfo));
            
            if (oldValue != value && !string.IsNullOrEmpty(value))
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

// 订阅 UIN 信息流        _resourceMonitor.UinStream            .ObserveOn(RxApp.MainThreadScheduler)            .Subscribe(OnUinReceived);

        // 订阅进程状态变化
        _processManager.ProcessStatusChanged += OnProcessStatusChanged;

        // 订阅日志流（最近10条）
        _logCollector.LogStream
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(OnLogReceived);

        // 全局启动/停止命令
        var canExecute = this.WhenAnyValue(x => x.IsButtonEnabled);
        GlobalStartStopCommand = ReactiveCommand.Create(() => { _ = GlobalStartStopAsync(); }, canExecute);

        // 查看全部日志命令
        ViewAllLogsCommand = ReactiveCommand.Create(() =>
        {
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
            if (ConfirmDialog != null)
            {
                var confirmed = await ConfirmDialog("确认停止", "确定要停止所有服务吗？");
                if (!confirmed) return;
            }
            await StopAllServicesAsync();
        }
        else
        {
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
            var nodeExists = !string.IsNullOrEmpty(config.NodePath) && File.Exists(config.NodePath);
            var llbotExists = !string.IsNullOrEmpty(config.LLBotPath) && File.Exists(config.LLBotPath);

            // 如果任何文件不存在，尝试下载
            if (!pmhqExists || !nodeExists || !llbotExists)
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

                    // 保存更新后的配置
                    await _configManager.SaveConfigAsync(config);
                    _logger.LogInformation("下载完成，配置已更新");
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

            // 启动 PMHQ
            _logger.LogInformation("正在启动 PMHQ...");
            var pmhqSuccess = await _processManager.StartPmhqAsync(
                config.PmhqPath,
                config.QQPath,
                config.AutoLogin,
                config.Headless);

            if (pmhqSuccess)
            {
                _logger.LogInformation("PMHQ 启动成功");

                // 设置 PmhqClient 端口并开始轮询
                if (_processManager.PmhqPort.HasValue)
                {
                    _pmhqClient.SetPort(_processManager.PmhqPort.Value);
                    StartInfoPolling(); // 端口可用后立即开始轮询
                }

                // 等待一段时间后启动 LLBot
                await Task.Delay(2000);

                // 启动 LLBot
                _logger.LogInformation("正在启动 LLBot...");
                var llbotSuccess = await _processManager.StartLLBotAsync(
                    config.NodePath,
                    config.LLBotScriptPath);

                if (llbotSuccess)
                {
                    _logger.LogInformation("LLBot 启动成功");
                    IsServicesRunning = true;
                    BotStatus = ProcessStatus.Running;
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
            _resourceMonitor.ResetState();
            
            await _processManager.StopAllAsync();

            IsServicesRunning = false;
            BotStatus = ProcessStatus.Stopped;

            QQUin = string.Empty;
            QQNickname = string.Empty;
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
            // 先等待端口可用（PMHQ API 能响应）
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
                    break; // 端口可用，退出等待循环
                }
                
                try { await Task.Delay(1000, ct); } // 等待时 1 秒轮询一次
                catch (OperationCanceledException) { return; }
            }
            
            // 端口可用后，正常轮询获取信息
            while (!ct.IsCancellationRequested)
            {
                try { await Task.Delay(3000, ct); }
                catch (OperationCanceledException) { break; }
                
                var selfInfo = await _pmhqClient.FetchSelfInfoAsync(ct);
                if (selfInfo != null && !string.IsNullOrEmpty(selfInfo.Uin))
                {
                    await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        QQUin = selfInfo.Uin;
                        QQNickname = selfInfo.Nickname;
                    });
                }
            }
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

private void OnUinReceived(SelfInfo selfInfo)
    {
        if (selfInfo != null)
        {
            QQUin = selfInfo.Uin;
            QQNickname = selfInfo.Nickname;
            QQStatus = ProcessStatus.Running;
            _logger.LogInformation("UIN 已更新: {Uin} - {Nickname}", QQUin, QQNickname);
        }
    }

    private void OnLogReceived(LogEntry logEntry)
    {
        RecentLogs.Add(new LogEntryViewModel(logEntry));

        // 只保留最近 10 条
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

    private void OnResourceUpdate(ProcessResourceInfo resource)
    {
        switch (resource.ProcessName.ToLower())
        {
            case "pmhq":
                _pmhqCpu = resource.CpuPercent;
                _pmhqMemory = resource.MemoryMB;
                // Bot = PMHQ + LLBot 合计
                BotCpu = _pmhqCpu + _llbotCpu;
                BotMemory = _pmhqMemory + _llbotMemory;
                break;
            case "llbot":
                _llbotCpu = resource.CpuPercent;
                _llbotMemory = resource.MemoryMB;
                // Bot = PMHQ + LLBot 合计
                BotCpu = _pmhqCpu + _llbotCpu;
                BotMemory = _pmhqMemory + _llbotMemory;
                break;
            case "qq":
                QQCpu = resource.CpuPercent;
                QQMemory = resource.MemoryMB;
                // 如果 QQ 在运行，更新状态
                if (resource.CpuPercent > 0 || resource.MemoryMB > 0)
                {
                    QQStatus = ProcessStatus.Running;
                }
                break;
        }
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
}
