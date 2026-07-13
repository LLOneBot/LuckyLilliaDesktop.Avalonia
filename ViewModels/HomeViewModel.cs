using Avalonia.Media;
using Avalonia.Threading;
using LuckyLilliaDesktop.Models;
using LuckyLilliaDesktop.Services;
using LuckyLilliaDesktop.Utils;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ReactiveUI;
using Serilog;
using System;
using System.Collections.Generic;
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
    private static readonly HttpClient HttpClient = new();
    private readonly IProcessManager _processManager;
    private readonly IResourceMonitor _resourceMonitor;
    private readonly ISelfInfoService _selfInfoService;
    private readonly IConfigManager _configManager;
    private readonly IPmhqClient _pmhqClient;
    private readonly IAuthTokenValidator _authTokenValidator;
    private readonly ILLBotIpcClient _llbotIpc;
    private readonly ILogCollector _logCollector;
    private readonly IDownloadService _downloadService;
    private readonly IUpdateChecker _updateChecker;
    private readonly IUpdateStateService _updateStateService;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<HomeViewModel> _logger;
    private CancellationTokenSource? _downloadCts;
    private CancellationTokenSource? _infoPollingCts;

    public Func<string, string, Task<bool>>? ConfirmDialog { get; set; }
    public Func<string, string, string, string, Task<int>>? ChoiceDialog { get; set; }
    public Func<string, string, Task>? ShowAlertDialog { get; set; }
    public Func<string, Func<Task>, Task>? ShowLoadingDialog { get; set; }
    public Func<Task<string?>>? ShowAuthTokenDialog { get; set; }
    public Func<Task<string?>>? ShowQRLoginDialog { get; set; }
    // 无头登录框: 传入 (session 账号列表, 启动 LLBot 的回调) -> 返回登录成功的 uin (取消/失败返回 null)
    public Func<List<LoginAccount>, Func<string?, Task<bool>>, Task<string?>>? ShowHeadlessLoginDialog { get; set; }

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
    private DateTime? _botStartTime;
    public ProcessStatus BotStatus
    {
        get => _botStatus;
        set
        {
            var wasRunning = _botStatus == ProcessStatus.Running;
            this.RaiseAndSetIfChanged(ref _botStatus, value);
            this.RaisePropertyChanged(nameof(BotStatusText));
            _botStartTime = value switch
            {
                // 记录/清除运行起点, 用于"已运行时长"
                ProcessStatus.Running when !wasRunning => DateTime.Now,
                ProcessStatus.Stopped => null,
                _ => _botStartTime
            };
            UpdateUptime();
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

    // 无头模式: 不启动 QQ, 控制面板第二张卡从"QQ 占用"换成"LLBot 信息"(版本 + 运行时长)
    private bool _isHeadless;
    public bool IsHeadless
    {
        get => _isHeadless;
        set => this.RaiseAndSetIfChanged(ref _isHeadless, value);
    }

    // 进程卡片恒 2 列: 无头 [Bot占用 + LLBot信息], 非无头 [Bot占用 + QQ占用]
    public int ProcessCardColumns => 2;

    // LLBot 版本号 (读 package.json), 无头模式 LLBot 信息卡显示
    private string _llbotVersion = string.Empty;
    public string LLBotVersion
    {
        get => _llbotVersion;
        set => this.RaiseAndSetIfChanged(ref _llbotVersion, value);
    }

    // 已运行时长 (运行中才有值), 无头模式 LLBot 信息卡显示
    private string _uptime = string.Empty;
    public string Uptime
    {
        get => _uptime;
        set => this.RaiseAndSetIfChanged(ref _uptime, value);
    }

    // Node.js 版本 (读 node --version), 无头模式 LLBot 信息卡显示
    private string _nodeVersion = "—";
    public string NodeVersion
    {
        get => _nodeVersion;
        set => this.RaiseAndSetIfChanged(ref _nodeVersion, value);
    }

    // 启动时刻 (绝对时间 HH:mm), 无头模式 LLBot 信息卡显示
    private string _startedAt = "—";
    public string StartedAt
    {
        get => _startedAt;
        set => this.RaiseAndSetIfChanged(ref _startedAt, value);
    }

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
        set
        {
            this.RaiseAndSetIfChanged(ref _botMemory, value);
            this.RaisePropertyChanged(nameof(TotalMemory));
        }
    }

    private double _availableMemory;
    public double AvailableMemory
    {
        get => _availableMemory;
        set
        {
            this.RaiseAndSetIfChanged(ref _availableMemory, value);
            this.RaisePropertyChanged(nameof(TotalMemory));
        }
    }

    // 总内存 = Bot占用 + QQ占用 + 可用内存
    public double TotalMemory => BotMemory + QQMemory + AvailableMemory;

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
        set
        {
            this.RaiseAndSetIfChanged(ref _qqMemory, value);
            this.RaisePropertyChanged(nameof(TotalMemory));
        }
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

    public Avalonia.Media.Imaging.Bitmap? AvatarBitmap
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    private async Task LoadAvatarAsync(string uin)
    {
        if (!AccountInfoHelper.IsValidQQUin(uin)) return;

        try
        {
            var url = $"https://q1.qlogo.cn/g?b=qq&nk={uin}&s=640";
            var bytes = await HttpClient.GetByteArrayAsync(url);
            using var stream = new MemoryStream(bytes);
            var avatar = PicHelper.DecodeToWidth(stream, 56, 1);
            if (QQUin == uin)
                AvatarBitmap = avatar;
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
        IAuthTokenValidator authTokenValidator,
        ILLBotIpcClient llbotIpc,
        ILogCollector logCollector,
        IDownloadService downloadService,
        IUpdateChecker updateChecker,
        IUpdateStateService updateStateService,
        IServiceProvider serviceProvider,
        ILogger<HomeViewModel> logger)
    {
        _processManager = processManager;
        _resourceMonitor = resourceMonitor;
        _selfInfoService = selfInfoService;
        _configManager = configManager;
        _pmhqClient = pmhqClient;
        _authTokenValidator = authTokenValidator;
        _llbotIpc = llbotIpc;
        _logCollector = logCollector;
        _downloadService = downloadService;
        _updateChecker = updateChecker;
        _updateStateService = updateStateService;
        _serviceProvider = serviceProvider;
        _logger = logger;

        // 订阅资源监控流
        _resourceMonitor.ResourceStream
            .ObserveOnUiThread()
            .Subscribe(OnResourceUpdate);

        // 订阅可用内存流 (顺便每次刷新已运行时长)
        _resourceMonitor.AvailableMemoryStream
            .ObserveOnUiThread()
            .Subscribe(mem =>
            {
                AvailableMemory = mem;
                UpdateUptime();
            });

        _selfInfoService.UinStream
            .ObserveOnUiThread()
            .Subscribe(uin =>
            {
                if (!string.IsNullOrEmpty(uin) && !AccountInfoHelper.IsValidQQUin(uin))
                {
                    _logger.LogWarning("忽略无效 UIN: {Uin}", uin);
                    return;
                }

                QQUin = uin ?? string.Empty;
                QQStatus = string.IsNullOrEmpty(uin) ? ProcessStatus.Stopped : ProcessStatus.Running;
                if (!string.IsNullOrEmpty(uin))
                    _logger.LogInformation("UIN 已更新: {Uin}", uin);
            });

        _selfInfoService.NicknameStream
            .ObserveOnUiThread()
            .Subscribe(nickname =>
            {
                QQNickname = nickname ?? string.Empty;
                if (!string.IsNullOrEmpty(nickname))
                    _logger.LogInformation("昵称已更新: {Nickname}", nickname);
            });

        _resourceMonitor.QQVersionStream
            .ObserveOnUiThread()
            .Subscribe(version => QQVersion = version);

        // uin/昵称统一经 SelfInfoService (数据源已切到 LLBot IPC), 上面 UinStream/NicknameStream
        // 订阅即覆盖有头 / 无头两种模式, 这里不再单独订阅 _llbotIpc.SelfInfoStream.

        // 订阅进程状态变化
        _processManager.ProcessStatusChanged += OnProcessStatusChanged;

        // 订阅更新状态变化
        _updateStateService.StateChanged
            .ObserveOnUiThread()
            .Subscribe(OnUpdateStateChanged);

        // 订阅日志流（最近10条），批量处理避免 UI 卡顿
        _logCollector.LogStream
            .Buffer(TimeSpan.FromMilliseconds(100))
            .Where(batch => batch.Count > 0)
            .ObserveOnUiThread()
            .Subscribe(OnLogBatchReceived);

        // 全局启动/停止命令；按钮启用状态由 IsButtonEnabled 绑定控制，避免 ReactiveCommand 的 CanExecuteChanged 跨线程触发 Avalonia 控件访问。
        GlobalStartStopCommand = ReactiveCommand.Create(() =>
        {
            if (!IsButtonEnabled) return;
            _ = GlobalStartStopAsync();
        }, outputScheduler: AvaloniaUiScheduler.Instance);

        // 查看全部日志命令
        ViewAllLogsCommand = ReactiveCommand.Create(() =>
        {
            _logger.LogDebug("导航到日志页面");
            NavigateToLogs?.Invoke();
        }, outputScheduler: AvaloniaUiScheduler.Instance);

        // 更新命令 - 导航到关于页面
        UpdateCommand = ReactiveCommand.Create(() =>
        {
            _logger.LogInformation("用户点击更新按钮，导航到关于页面");
            NavigateToAbout?.Invoke();
        }, outputScheduler: AvaloniaUiScheduler.Instance);

        // 取消下载命令
        CancelDownloadCommand = ReactiveCommand.Create(() =>
        {
            _logger.LogInformation("用户取消下载");
            _downloadCts?.Cancel();
            IsDownloading = false;
            DownloadStatus = "下载已取消";
        }, outputScheduler: AvaloniaUiScheduler.Instance);

        // 初始化时启动资源监控
        _ = _resourceMonitor.StartMonitoringAsync();

        // 加载最近日志
        LoadRecentLogs();

        // 检查更新
        _ = CheckForUpdatesAsync();

        // 检查是否需要自动启动 Bot
        _ = CheckAutoStartBotAsync();

        // 读取 headless 配置, 决定控制面板是否显示 QQ 占用卡片
        _ = InitializeFromConfigAsync();

        // 配置保存后即时同步 headless 状态 (控制面板 QQ 卡片显隐随配置变化)
        _configManager.ConfigSaved += config =>
            Dispatcher.UIThread.Post(() => IsHeadless = config.Headless);
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

    private async Task InitializeFromConfigAsync()
    {
        try
        {
            var config = await _configManager.LoadConfigAsync();
            var llbotVer = Utils.VersionDetector.DetectLLBotVersion(config.LLBotPath, _logger) ?? "";
            var nodeVer = await Utils.NodeHelper.GetNodeVersionAsync(config.NodePath, _logger);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                IsHeadless = config.Headless;
                LLBotVersion = llbotVer;
                NodeVersion = nodeVer.HasValue ? $"v{nodeVer.Value}" : "—";
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "读取配置失败");
        }
    }

    // 已运行时长: 运行中按 _botStartTime 计算, 未运行显示"未运行"
    private void UpdateUptime()
    {
        if (_botStartTime == null)
        {
            Uptime = "未运行";
            StartedAt = "未启动";
            return;
        }
        Uptime = FormatUptime(DateTime.Now - _botStartTime.Value);
        StartedAt = _botStartTime.Value.ToString("HH:mm");
    }

    private static string FormatUptime(TimeSpan span)
    {
        if (span.TotalDays >= 1) return $"{(int)span.TotalDays}天{span.Hours}小时";
        if (span.TotalHours >= 1) return $"{(int)span.TotalHours}小时{span.Minutes}分";
        if (span.TotalMinutes >= 1) return $"{(int)span.TotalMinutes}分{span.Seconds}秒";
        return $"{span.Seconds}秒";
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

    private async Task StartAutoFrameworksAsync(AppConfig config)
    {
        if (config.AutoStartFrameworks.Count == 0) return;

        try
        {
            _logger.LogInformation("自动启动框架: {Frameworks}", string.Join(", ", config.AutoStartFrameworks));
            await Task.Delay(3000); // 等待 LLBot 初始化

            foreach (var framework in config.AutoStartFrameworks)
            {
                try
                {
                    switch (framework)
                    {
                        case "koishi":
                            _serviceProvider.GetRequiredService<IKoishiInstallService>().StartKoishi();
                            break;
                        case "astrbot":
                            _serviceProvider.GetRequiredService<IAstrBotInstallService>().StartAstrBot();
                            break;
                        case "zhenxun":
                            _serviceProvider.GetRequiredService<IZhenxunInstallService>().StartZhenxun();
                            break;
                        case "ddbot":
                            _serviceProvider.GetRequiredService<IDDBotInstallService>().StartDDBot();
                            break;
                        case "yunzai":
                            _serviceProvider.GetRequiredService<IYunzaiInstallService>().StartYunzai();
                            break;
                        case "zbp":
                            _serviceProvider.GetRequiredService<IZeroBotPluginInstallService>().StartZeroBotPlugin();
                            break;
                        case "openclaw":
                            var openClaw = _serviceProvider.GetRequiredService<IOpenClawInstallService>();
                            if (!openClaw.IsInstalled)
                            {
                                _logger.LogWarning("OpenClaw 未安装，跳过自动启动");
                                continue;
                            }
                            // 检查 openclaw 命令是否存在
                            var openclawCmd = Utils.PlatformHelper.IsWindows
                                ? Path.GetFullPath(Path.Combine("bin/node24", "openclaw.cmd"))
                                : Path.GetFullPath(Path.Combine("bin/node24", "bin", "openclaw"));
                            if (!File.Exists(openclawCmd))
                            {
                                _logger.LogWarning("OpenClaw 命令不存在: {Path}，跳过自动启动", openclawCmd);
                                continue;
                            }
                            if (openClaw.IsFirstRun)
                                openClaw.StartOnboard();
                            else
                                openClaw.StartGateway();
                            break;
                        case "redreply":
                            var redReply = _serviceProvider.GetRequiredService<IRedReplyInstallService>();
                            var redReplyWsUrl = redReply.EnsureOneBotWebSocketUrl(_selfInfoService.CurrentUin);
                            redReply.StartRedReply(openWebUiIfRunning: false, openWebUiOnStart: false, oneBotWsUrl: redReplyWsUrl);
                            break;
                    }
                    _logger.LogInformation("已自动启动框架: {Framework}", framework);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "自动启动框架失败: {Framework}", framework);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "自动启动框架时出错");
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
            // 无头模式不启动 PMHQ, 不检测它的版本/更新
            var pmhqVersion = config.Headless ? null : await Utils.VersionDetector.DetectPmhqVersionAsync(config.PmhqPath, _logger);
            var llbotVersion = Utils.VersionDetector.DetectLLBotVersion(config.LLBotPath, _logger);

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

    // 启动门槛: PMHQ / LLBot 主版本必须 >= 8.
    private const int MinMajorVersion = 8;

    // 取主版本号 (major). 兼容 "8.0.1" / "v8.0.1" / "8.0.0-beta". 解析不出返回 null.
    private static int? ParseMajorVersion(string? version)
    {
        if (string.IsNullOrWhiteSpace(version)) return null;
        var seg = version.Trim().TrimStart('v', 'V').Split('.', '-', '+')[0];
        return int.TryParse(seg, out var major) ? major : null;
    }

    // LLBot (两种模式) + PMHQ (仅有头) 主版本必须 >= 8. 通过返回 null, 否则返回展示给用户的错误消息.
    // 读不到版本也拦 (安装异常 -> 让用户重装/更新). PMHQ 以 `pmhq --version` 为主, 见 Utils.VersionDetector.
    private async Task<string?> CheckVersionRequirementAsync(AppConfig config)
    {
        var llbotVer = Utils.VersionDetector.DetectLLBotVersion(config.LLBotPath, _logger);
        var llbotMajor = ParseMajorVersion(llbotVer);
        if (llbotMajor == null)
            return $"无法确认 LLBot 版本, 请更新到 {MinMajorVersion}.0 或以上后再启动";
        if (llbotMajor < MinMajorVersion)
            return $"LLBot 版本过低 (当前 {llbotVer}), 需要 {MinMajorVersion}.0 或以上, 请更新后再启动";

        if (!config.Headless)
        {
            var pmhqVer = await Utils.VersionDetector.DetectPmhqVersionAsync(config.PmhqPath, _logger);
            var pmhqMajor = ParseMajorVersion(pmhqVer);
            if (pmhqMajor == null)
                return $"无法确认 PMHQ 版本, 请更新到 {MinMajorVersion}.0 或以上后再启动";
            if (pmhqMajor < MinMajorVersion)
                return $"PMHQ 版本过低 (当前 {pmhqVer}), 需要 {MinMajorVersion}.0 或以上, 请更新后再启动";
        }
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
            IsHeadless = config.Headless;

            // 无头模式: 不启动 PMHQ / QQ, 直接启动 LLBot (LLBot 内部自行连 QQ)
            if (config.Headless)
            {
                await StartHeadlessServicesAsync(config);
                return;
            }

            // 如果 QQ 路径为空，尝试自动检测
            if (string.IsNullOrEmpty(config.QQPath))
            {
                var detectedPath = Utils.QQPathHelper.GetDefaultQQPath();

                if (!string.IsNullOrEmpty(detectedPath))
                {
                    config.QQPath = detectedPath;
                    _logger.LogInformation("自动检测到 QQ 路径: {Path}", detectedPath);
                }
                else
                {
                    // macOS: 直接下载，不弹窗
                    if (Utils.PlatformHelper.IsMacOS)
                    {
                        _logger.LogInformation("macOS 未检测到 QQ，开始自动下载...");
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
                                config.QQPath = Utils.QQPathHelper.GetMacOSQQPath() ?? string.Empty;
                                _logger.LogInformation("QQ 下载完成: {Path}", config.QQPath);
                            }
                            else
                            {
                                ErrorMessage = "QQ 下载失败";
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
                    // Windows: 弹窗询问用户
                    else if (ChoiceDialog != null)
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
                                    // Windows 安装完成后尝试从注册表重新检测
                                    if (Utils.PlatformHelper.IsWindows)
                                    {
                                        config.QQPath = Utils.QQPathHelper.GetQQPathFromRegistry() ?? string.Empty;
                                    }
                                    else
                                    {
                                        config.QQPath = Utils.QQPathHelper.GetDefaultQQPath() ?? string.Empty;
                                    }
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
            else if (!File.Exists(config.QQPath))
            {
                _logger.LogWarning("QQ 路径无效: {Path}", config.QQPath);

                // macOS: 自动下载 QQ
                if (Utils.PlatformHelper.IsMacOS)
                {
                    _logger.LogInformation("macOS QQ 路径无效，开始自动下载...");
                    config.QQPath = ""; // 清空无效路径
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
                            config.QQPath = Utils.QQPathHelper.GetMacOSQQPath() ?? string.Empty;
                            _logger.LogInformation("QQ 下载完成: {Path}", config.QQPath);
                        }
                        else
                        {
                            ErrorMessage = "QQ 下载失败";
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
                else
                {
                    // Windows/Linux: 提示用户重新设置
                    ErrorMessage = $"QQ 路径无效: {config.QQPath}，请在系统配置中重新设置";
                    BotStatus = ProcessStatus.Stopped;
                    return;
                }
            }

            _logger.LogInformation("配置加载完成: PmhqPath={PmhqPath}, NodePath={NodePath}, LLBotPath={LLBotPath}, QQPath={QQPath}",
                config.PmhqPath, config.NodePath, config.LLBotPath, config.QQPath);

            // 检查并下载缺失的文件
            var pmhqExists = !string.IsNullOrEmpty(config.PmhqPath) && File.Exists(config.PmhqPath);

            var nodeExists = await TryUseNodeAsync(config.NodePath, "配置中的Node.js路径", "配置中的Node.js版本低于24，将重新检测");
            if (!nodeExists && !string.IsNullOrEmpty(config.NodePath))
            {
                _logger.LogWarning("配置中的Node.js路径无效，将重新检测: {Path}", config.NodePath);
                config.NodePath = string.Empty;
            }

            if (!nodeExists)
            {
                var systemNode = NodeHelper.FindNodeInPath();
                nodeExists = await TryUseNodeAsync(systemNode, "在系统PATH中找到Node.js (版本>=24)", "系统PATH中的Node.js版本低于24");
                if (nodeExists) config.NodePath = systemNode!;
            }

            if (!nodeExists)
            {
                var localNodePath = Constants.DefaultPaths.NodeExe;
                nodeExists = await TryUseNodeAsync(localNodePath, "在本地目录找到Node.js", "本地Node.js版本低于24");
                if (nodeExists) config.NodePath = localNodePath;
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

            async Task<bool> TryUseNodeAsync(string? path, string successMessage, string invalidVersionMessage)
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path))
                    return false;

                if (!await NodeHelper.CheckNodeVersionValidAsync(path, 24, _logger))
                {
                    _logger.LogWarning("{Message}: {Path}", invalidVersionMessage, path);
                    return false;
                }

                _logger.LogInformation("{Message}: {Path}", successMessage, path);
                return true;
            }

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

            // 版本门槛: 有头模式查 PMHQ + LLBot 主版本, 任一 < 8 (或读不到) 拦截并要求更新
            var versionError = await CheckVersionRequirementAsync(config);
            if (versionError != null)
            {
                ErrorMessage = versionError;
                BotStatus = ProcessStatus.Stopped;
                return;
            }

            // auth_token: 读出内容, 既供 LLBot 读取又作为 --auth-token 传给 PMHQ
            var authToken = await EnsureAuthTokenAsync(config);
            if (authToken == null)
            {
                return;
            }

            // QQ 由 PMHQ 负责启动: QQ 路径已写入 pmhq_config.json 的 qq_path (见 StartPmhqAsync -> UpdatePmhqConfigAsync),
            // PMHQ find_existing_qq_pid 找不到已运行的 QQ 时会按 qq_path 自行拉起. Desktop 不再单独启动 QQ.
            // auth-token 必传给 PMHQ (缺则 PMHQ 启动即退).
            _logger.LogInformation("正在启动 PMHQ (由其负责启动 QQ)...");
            var pmhqSuccess = await _processManager.StartPmhqAsync(
                config.PmhqPath,
                config.QQPath,
                config.AutoLoginQQ,
                authToken,
                config.Debug,
                config.HttpProxy);

            if (pmhqSuccess)
            {
                _logger.LogInformation("PMHQ 启动成功");

                // 先启动 LLBot，确保 WebSocket 服务就绪，避免错过 QQ 事件
                _logger.LogInformation("正在启动 LLBot...");
                // Windows 上启动 IPC 管道, Desktop 从 LLBot 拿 uin/昵称 (PMHQ 不再提供查询 QQ 信息的 API)
                string? llbotIpcPipe = null;
                if (Utils.PlatformHelper.IsWindows)
                {
                    try { llbotIpcPipe = await _llbotIpc.StartAsync(); }
                    catch (Exception ex) { _logger.LogWarning(ex, "启动 LLBot IPC 客户端失败"); }
                }
                var llbotSuccess = await _processManager.StartLLBotAsync(
                    config.NodePath,
                    config.LLBotPath,
                    llbotIpcPipe,
                    httpProxy: config.HttpProxy);

                if (llbotSuccess)
                {
                    _logger.LogInformation("LLBot 启动成功");
                    IsServicesRunning = true;
                }
                else
                {
                    ErrorMessage = "LLBot 启动失败，请检查日志";
                    _logger.LogError("LLBot 启动失败");
                    BotStatus = ProcessStatus.Stopped;
                    return;
                }

                // 设置 PmhqClient 端口并开始轮询
                if (_processManager.PmhqPort.HasValue)
                {
                    _pmhqClient.SetPort(_processManager.PmhqPort.Value);
                }

                // 等待 PMHQ API 可用
                _logger.LogInformation("等待 PMHQ API 可用，检查 QQ 进程...");
                var apiReady = false;
                for (int i = 0; i < 10; i++)
                {
                    await Task.Delay(1000);
                    var qqPid = await _pmhqClient.FetchQQPidAsync();
                    _logger.LogInformation("第 {Count} 次检查 QQ PID: {Pid}", i + 1, qqPid);
                    
                    if (qqPid.HasValue && qqPid.Value > 0)
                    {
                        _logger.LogInformation("PMHQ API 已可用，检测到 QQ 进程 PID: {Pid}", qqPid.Value);
                        apiReady = true;
                        break;
                    }
                }
                
                _logger.LogInformation("QQ 进程检测完成，结果: {ApiReady}", apiReady);

                if (!apiReady)
                {
                    _logger.LogWarning("10秒后未检测到QQ进程");
                    ErrorMessage = "QQ 启动失败，请检查 PMHQ 和 QQ 配置";
                    BotStatus = ProcessStatus.Stopped;
                    return;
                }

                StartInfoPolling();

                BotStatus = ProcessStatus.Running;

                // 执行启动后命令
                await ExecuteStartupCommandAsync(config);

                // 自动启动配置的框架
                await StartAutoFrameworksAsync(config);
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

    // 无头模式: 不启动 PMHQ/QQ, 只启动 LLBot. LLBot 自行处理 QQ 协议.
    // 仍需要 Node.js + LLBot 脚本 + FFmpeg + auth_token, 但跳过 QQ 路径/PMHQ 下载与启动.
    private async Task StartHeadlessServicesAsync(AppConfig config)
    {
        _logger.LogInformation("无头模式: 跳过 PMHQ, 直接启动 LLBot");

        // 检查 / 解析 Node.js 路径(允许使用 PATH 或本地目录, 版本需 >= 24)
        if (!await ResolveNodePathAsync(config))
        {
            return;
        }

        var llbotExists = !string.IsNullOrEmpty(config.LLBotPath) && File.Exists(config.LLBotPath);
        var ffmpegExists = Utils.FFmpegHelper.CheckFFmpegExists();
        var ffprobeExists = Utils.FFmpegHelper.CheckFFprobeExists();

        if (!llbotExists || !ffmpegExists || !ffprobeExists)
        {
            _downloadCts = new CancellationTokenSource();
            IsDownloading = true;

            try
            {
                var progress = new Progress<DownloadProgress>(p =>
                {
                    DownloadProgress = p.Percentage;
                    DownloadStatus = p.Status;
                });

                if (!llbotExists)
                {
                    DownloadingItem = "LLBot";
                    _logger.LogInformation("下载 LLBot...");
                    if (!await _downloadService.DownloadLLBotAsync(progress, _downloadCts.Token))
                    {
                        ErrorMessage = "LLBot 下载失败";
                        BotStatus = ProcessStatus.Stopped;
                        return;
                    }
                    config.LLBotPath = Utils.Constants.DefaultPaths.LLBotScript;
                }

                if (!ffmpegExists || !ffprobeExists)
                {
                    DownloadingItem = "FFmpeg";
                    _logger.LogInformation("下载 FFmpeg...");
                    if (!await _downloadService.DownloadFFmpegAsync(progress, _downloadCts.Token))
                    {
                        ErrorMessage = "FFmpeg 下载失败";
                        BotStatus = ProcessStatus.Stopped;
                        return;
                    }
                }

                await _configManager.SaveConfigAsync(config);
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

        if (!File.Exists(config.LLBotPath))
        {
            ErrorMessage = $"LLBot 脚本不存在: {config.LLBotPath}";
            BotStatus = ProcessStatus.Stopped;
            return;
        }

        if (!Utils.FFmpegHelper.CheckFFmpegExists() || !Utils.FFmpegHelper.CheckFFprobeExists())
        {
            ErrorMessage = "FFmpeg / FFprobe 不可用";
            BotStatus = ProcessStatus.Stopped;
            return;
        }

        // 版本门槛: 无头模式只启动 LLBot, 查 LLBot 主版本 (< 8 或读不到则拦截要求更新)
        var versionError = await CheckVersionRequirementAsync(config);
        if (versionError != null)
        {
            ErrorMessage = versionError;
            BotStatus = ProcessStatus.Stopped;
            return;
        }

        if (await EnsureAuthTokenAsync(config) == null)
        {
            return;
        }

        _logger.LogInformation("准备启动 LLBot (无头模式)...");

        // 启动 LLBot 的本地函数: uin 非空走快速登录 (--qq=uin), null 走扫码; 同时拉起 IPC (Windows 命名管道 / Unix UDS)
        async Task<bool> StartLLBotWithUinAsync(string? uin)
        {
            string? pipe = null;
            try { pipe = await _llbotIpc.StartAsync(); }
            catch (Exception ex) { _logger.LogWarning(ex, "启动 LLBot IPC 客户端失败"); }
            return await _processManager.StartLLBotAsync(config.NodePath, config.LLBotPath, pipe, uin, config.HttpProxy);
        }

        if (ShowHeadlessLoginDialog != null && !HasLocalSession(config.LLBotPath, config.AutoLoginQQ))
        {
            // 场景: 未配自动登录号, 或配了但本地找不到对应 session -> 弹框 (账号列表 + 扫码)。
            // 若不弹框就直接 --qq=<uin>, LLBot 会因缺 session 自动转扫码, 但控制面板没开框, 用户看不到二维码。
            var accounts = ScanSessionAccounts(config.LLBotPath);
            _logger.LogInformation("无头登录: 扫描到 {Count} 个本地账号 (AutoLoginQQ='{AutoUin}' 未找到 session 或未配置)",
                accounts.Count, config.AutoLoginQQ);
            var loggedUin = await ShowHeadlessLoginDialog(accounts, StartLLBotWithUinAsync);
            if (string.IsNullOrEmpty(loggedUin))
            {
                _logger.LogWarning("无头登录已取消或失败, 停止服务");
                await _llbotIpc.StopAsync();
                await _processManager.StopLLBotAsync();
                BotStatus = ProcessStatus.Stopped;
                return;
            }
            _logger.LogInformation("无头登录成功: {Uin}", loggedUin);
        }
        else
        {
            // 配了自动登录号且本地有对应 session -> 直接快速登录 (--qq=该号); 或调用方没注入登录框 (视为直接启动)
            var autoUin = string.IsNullOrEmpty(config.AutoLoginQQ) ? null : config.AutoLoginQQ;
            if (!string.IsNullOrEmpty(autoUin))
                _logger.LogInformation("配置了自动登录号且本地存在 session, 直接快速登录: {Uin}", autoUin);
            if (!await StartLLBotWithUinAsync(autoUin))
            {
                ErrorMessage = "LLBot 启动失败，请检查日志";
                _logger.LogError("LLBot 启动失败");
                BotStatus = ProcessStatus.Stopped;
                await _llbotIpc.StopAsync();
                return;
            }
        }

        _logger.LogInformation("LLBot 启动成功");

        // 启动后重新读 LLBot / Node 版本 (首次可能刚下载完)
        LLBotVersion = Utils.VersionDetector.DetectLLBotVersion(config.LLBotPath, _logger) ?? LLBotVersion;
        var nodeVerOnStart = await Utils.NodeHelper.GetNodeVersionAsync(config.NodePath, _logger);
        if (nodeVerOnStart.HasValue) NodeVersion = $"v{nodeVerOnStart.Value}";
        IsServicesRunning = true;
        BotStatus = ProcessStatus.Running;

        // QQ 信息: 登录成功后主动从 IPC 当前登录状态填一次, 不能只靠 SelfInfoStream 后续推送.
        // LLBotIpcClient.ApplySelfInfo 带去重 (_cachedUin 不变就不再推); 登录框路径登录过程中
        // 已经推过该 uin, 这里若清空 QQUin/QQNickname, IPC 慢轮询又因去重不再推, 就永远填不回来了.
        // (QQUin setter 会顺带触发头像加载, 所以填对 uin 头像也跟着回来.)
        QQVersion = string.Empty;   // 无头无 PMHQ /health, 不显示 QQ 版本
        var loginState = _llbotIpc.CurrentLoginState;
        if (loginState is { Uin: var loggedInUin } && AccountInfoHelper.IsValidQQUin(loggedInUin))
        {
            QQNickname = loginState.Nickname ?? string.Empty;
            QQUin = loggedInUin;
            QQStatus = ProcessStatus.Running;
        }
        else
        {
            // 还没登录 (自动登录号路径不等登录就走到这): 清初始态, 等 IPC 首次推 logged_in 填充
            QQUin = string.Empty;
            QQNickname = string.Empty;
            AvatarBitmap = null;
            QQStatus = ProcessStatus.Stopped;
        }

        await ExecuteStartupCommandAsync(config);
        await StartAutoFrameworksAsync(config);
    }

    // 判断 config.AutoLoginQQ 是否有对应的本地 session 文件 -> 决定能否直接快速登录 (不弹框).
    // AutoLoginQQ 为空时返回 false (表示无自动登录号, 走弹框流程).
    private static bool HasLocalSession(string? llbotPath, string? uin)
    {
        if (string.IsNullOrWhiteSpace(uin)) return false;
        var llbotDir = Path.GetDirectoryName(llbotPath);
        if (string.IsNullOrEmpty(llbotDir)) return false;
        var sessionFile = Path.Combine(llbotDir, "data", $"qq-session-{uin}.json");
        return File.Exists(sessionFile);
    }

    // 扫描 LLBot data 目录下的 qq-session-{uin}.json, 提取 uin (文件名) + nick (文件内容), 供快速登录列表
    private static List<LoginAccount> ScanSessionAccounts(string? llbotPath)
    {
        var result = new List<LoginAccount>();
        var llbotDir = Path.GetDirectoryName(llbotPath);
        if (string.IsNullOrEmpty(llbotDir)) return result;
        var dataDir = Path.Combine(llbotDir, "data");
        if (!Directory.Exists(dataDir)) return result;

        foreach (var file in Directory.GetFiles(dataDir, "qq-session-*.json"))
        {
            try
            {
                var name = Path.GetFileNameWithoutExtension(file);
                const string prefix = "qq-session-";
                if (!name.StartsWith(prefix)) continue;
                var uin = name.Substring(prefix.Length);
                if (string.IsNullOrEmpty(uin) || !long.TryParse(uin, out _)) continue;

                var nick = "";
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(file));
                    if (doc.RootElement.TryGetProperty("nick", out var n)) nick = n.GetString() ?? "";
                }
                catch { }
                result.Add(new LoginAccount
                {
                    Uin = uin,
                    NickName = nick,
                    IsQuickLogin = true,
                    FaceUrl = $"https://q1.qlogo.cn/g?b=qq&nk={uin}&s=100",
                });
            }
            catch { }
        }
        return result;
    }

    // 解析 Node.js: 配置 -> PATH -> 本地, 必要时下载. 成功时把路径写回 config.NodePath.
    private async Task<bool> ResolveNodePathAsync(AppConfig config)
    {
        var nodeExists = false;
        if (!string.IsNullOrEmpty(config.NodePath) && File.Exists(config.NodePath))
        {
            if (await Utils.NodeHelper.CheckNodeVersionValidAsync(config.NodePath, 24, _logger))
            {
                nodeExists = true;
            }
            else
            {
                config.NodePath = string.Empty;
            }
        }
        else if (!string.IsNullOrEmpty(config.NodePath))
        {
            config.NodePath = string.Empty;
        }

        if (!nodeExists)
        {
            var systemNode = Utils.NodeHelper.FindNodeInPath();
            if (!string.IsNullOrEmpty(systemNode)
                && await Utils.NodeHelper.CheckNodeVersionValidAsync(systemNode, 24, _logger))
            {
                config.NodePath = systemNode;
                nodeExists = true;
            }
        }

        if (!nodeExists)
        {
            var localNodePath = Utils.Constants.DefaultPaths.NodeExe;
            if (File.Exists(localNodePath)
                && await Utils.NodeHelper.CheckNodeVersionValidAsync(localNodePath, 24, _logger))
            {
                config.NodePath = localNodePath;
                nodeExists = true;
            }
        }

        if (!nodeExists)
        {
            _downloadCts = new CancellationTokenSource();
            IsDownloading = true;
            DownloadingItem = "Node.js";
            try
            {
                var progress = new Progress<DownloadProgress>(p =>
                {
                    DownloadProgress = p.Percentage;
                    DownloadStatus = p.Status;
                });
                if (!await _downloadService.DownloadNodeAsync(progress, _downloadCts.Token))
                {
                    ErrorMessage = "Node.js 下载失败";
                    BotStatus = ProcessStatus.Stopped;
                    return false;
                }
                config.NodePath = Utils.Constants.DefaultPaths.NodeExe;
            }
            finally
            {
                IsDownloading = false;
                DownloadingItem = "";
                _downloadCts = null;
            }
        }

        if (!File.Exists(config.NodePath))
        {
            ErrorMessage = $"Node.js 文件不存在: {config.NodePath}";
            BotStatus = ProcessStatus.Stopped;
            return false;
        }
        return true;
    }

    // 确保 LLBot data/auth_token.txt 存在 (缺则弹窗输入并落盘), 返回 token 内容.
    // 该 token 既供 LLBot 读取, 又会作为 --auth-token 传给 PMHQ.
    // 返回 null 表示用户取消或缺少弹窗注入 (ErrorMessage 已设).
    private async Task<string?> EnsureAuthTokenAsync(AppConfig config)
    {
        var llbotDir = Path.GetDirectoryName(config.LLBotPath);
        if (string.IsNullOrEmpty(llbotDir))
        {
            _logger.LogWarning("无法确定 LLBot 目录, 跳过 auth_token 处理");
            return "";
        }

        var dataDir = Path.Combine(llbotDir, "data");
        var authTokenPath = Path.Combine(dataDir, "auth_token.txt");
        var existingToken = File.Exists(authTokenPath)
            ? (await File.ReadAllTextAsync(authTokenPath)).Trim()
            : "";

        // 已有 token: 启动前走服务端校验。
        // - Valid: 直接用
        // - Invalid (401/403, token 明确失效/被吊销): 落到下面弹框重输
        // - Inconclusive (网络/超时/5xx, 无法判定): 宽松沿用旧 token (弹框也连不上, 无意义)
        if (!string.IsNullOrEmpty(existingToken))
        {
            _logger.LogInformation("校验现有 Auth Token...");
            var result = await _authTokenValidator.ValidateAsync(existingToken);
            if (result.Status == AuthTokenValidationStatus.Valid)
            {
                _logger.LogInformation("现有 Auth Token 校验通过");
                return existingToken;
            }
            if (result.Status == AuthTokenValidationStatus.Inconclusive)
            {
                _logger.LogWarning("Auth Token 服务端校验无法判定 ({Msg}), 沿用现有 token 继续", result.Message);
                return existingToken;
            }
            // Invalid: 现有 token 已失效, 弹框让用户重新输入
            _logger.LogWarning("现有 Auth Token 校验失败 ({Msg}), 需要重新输入", result.Message);
        }

        if (ShowAuthTokenDialog == null)
        {
            ErrorMessage = "缺少 Auth Token，无法启动 LLBot";
            BotStatus = ProcessStatus.Stopped;
            return null;
        }

        var token = await ShowAuthTokenDialog();
        if (string.IsNullOrWhiteSpace(token))
        {
            ErrorMessage = "未提供 Auth Token，已取消启动";
            BotStatus = ProcessStatus.Stopped;
            return null;
        }

        var trimmed = token.Trim();
        Directory.CreateDirectory(dataDir);
        await File.WriteAllTextAsync(authTokenPath, trimmed);
        _logger.LogInformation("Auth Token 已保存到 {Path}", authTokenPath);
        return trimmed;
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

            await _llbotIpc.StopAsync();
            
            var qqPid = _resourceMonitor.QQPid;
            _resourceMonitor.ResetState();

            if (ShowLoadingDialog != null)
            {
                await ShowLoadingDialog("正在关闭相关进程...", async () =>
                {
                    await _processManager.StopAllAsync(qqPid);
                });
            }
            else
            {
                await _processManager.StopAllAsync(qqPid);
            }

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

        // uin/昵称走 LLBot IPC (SelfInfoStream, 构造里已订阅); 这里只拉一次 QQ 版本 (PMHQ /health)
        _ = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var deviceInfo = await _pmhqClient.FetchDeviceInfoAsync(ct);
                    if (deviceInfo != null && !string.IsNullOrEmpty(deviceInfo.BuildVer))
                    {
                        var ver = deviceInfo.BuildVer;
                        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => QQVersion = ver);
                        break;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "获取 QQ 版本时出错");
                }

                try { await Task.Delay(1000, ct); }
                catch (OperationCanceledException) { return; }
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
        if (_isUIUpdatesPaused) return;
        
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
        if (_isUIUpdatesPaused) return;
        
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

    private bool _isUIUpdatesPaused;

    public void PauseUIUpdates()
    {
        if (_isUIUpdatesPaused) return;
        _isUIUpdatesPaused = true;
        _logger.LogDebug("HomeViewModel UI 更新已暂停");
    }

    public void ResumeUIUpdates()
    {
        if (!_isUIUpdatesPaused) return;
        _isUIUpdatesPaused = false;
        
        // 恢复时刷新最近日志
        RefreshRecentLogs();
        
        _logger.LogDebug("HomeViewModel UI 更新已恢复");
    }
    
    private void RefreshRecentLogs()
    {
        RecentLogs.Clear();
        var logs = _logCollector.GetRecentLogs(10);
        foreach (var log in logs)
        {
            RecentLogs.Add(new LogEntryViewModel(log));
        }
        HasRecentLogs = RecentLogs.Count > 0;
    }

}
