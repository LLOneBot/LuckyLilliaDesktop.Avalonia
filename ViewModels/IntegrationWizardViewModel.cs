using System;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Reactive;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using LuckyLilliaDesktop.Models;
using LuckyLilliaDesktop.Services;
using LuckyLilliaDesktop.Utils;
using Microsoft.Extensions.Logging;
using ReactiveUI;

namespace LuckyLilliaDesktop.ViewModels;

public class IntegrationWizardViewModel : ViewModelBase, IDisposable
{
    private readonly ILogger<IntegrationWizardViewModel> _logger;
    private readonly IKoishiInstallService _koishiInstallService;
    private readonly IAstrBotInstallService _astrBotInstallService;
    private readonly IZhenxunInstallService _zhenxunInstallService;
    private readonly IDDBotInstallService _ddbotInstallService;
    private readonly IYunzaiInstallService _yunzaiInstallService;
    private readonly IZeroBotPluginInstallService _zeroBotPluginInstallService;
    private readonly IOpenClawInstallService _openClawInstallService;
    private readonly ISelfInfoService _selfInfoService;
    private readonly IConfigManager _configManager;
    private readonly IDisposable _uinSubscription;

    private bool _isInstalling;
    private int _currentStep;
    private int _totalSteps;
    private string _currentStepName = "";
    private string _statusText = "";
    private double _progressPercentage;
    private bool _hasError;
    private string? _errorMessage;
    private bool _isCompleted;
    private bool _hasUin;
    private string? _currentUin;
    private CancellationTokenSource? _cts;

    public bool IsInstalling { get => _isInstalling; set => this.RaiseAndSetIfChanged(ref _isInstalling, value); }
    public int CurrentStep { get => _currentStep; set => this.RaiseAndSetIfChanged(ref _currentStep, value); }
    public int TotalSteps { get => _totalSteps; set => this.RaiseAndSetIfChanged(ref _totalSteps, value); }
    public string CurrentStepName { get => _currentStepName; set => this.RaiseAndSetIfChanged(ref _currentStepName, value); }
    public string StatusText { get => _statusText; set => this.RaiseAndSetIfChanged(ref _statusText, value); }
    public double ProgressPercentage { get => _progressPercentage; set => this.RaiseAndSetIfChanged(ref _progressPercentage, value); }
    public bool HasError { get => _hasError; set => this.RaiseAndSetIfChanged(ref _hasError, value); }
    public string? ErrorMessage { get => _errorMessage; set => this.RaiseAndSetIfChanged(ref _errorMessage, value); }
    public bool IsCompleted { get => _isCompleted; set => this.RaiseAndSetIfChanged(ref _isCompleted, value); }
    public bool HasUin { get => _hasUin; set => this.RaiseAndSetIfChanged(ref _hasUin, value); }
    public bool KoishiInstalled => _koishiInstallService.IsInstalled;
    public bool AstrBotInstalled => _astrBotInstallService.IsInstalled;
    public bool ZhenxunInstalled => _zhenxunInstallService.IsInstalled;
    public bool DDBotInstalled => _ddbotInstallService.IsInstalled;
    public bool YunzaiInstalled => _yunzaiInstallService.IsInstalled;
    public bool ZeroBotPluginInstalled => _zeroBotPluginInstallService.IsInstalled;
    public bool OpenClawInstalled => _openClawInstallService.IsInstalled;
    public bool ShowZeroBotPlugin => !PlatformHelper.IsMacOS;

    public ReactiveCommand<string, Unit> SelectFrameworkCommand { get; }
    public ReactiveCommand<Unit, Unit> CancelInstallCommand { get; }

    public Func<string, string, Task<bool>>? ConfirmInstallCallback { get; set; }
    public Func<string, string, Task>? ShowAlertCallback { get; set; }
    public Func<string, string, int, Action, Task>? ShowAutoCloseAlertCallback { get; set; }
    public Func<string, string, Task<int>>? ThreeChoiceCallback { get; set; }
    /// <summary>
    /// 已安装框架的操作对话框回调。参数: (frameworkName, message, autoStartChecked, showAutoStart) → (choice, autoStart)
    /// </summary>
    public Func<string, string, bool, bool, Task<(int choice, bool autoStart)>>? FrameworkActionCallback { get; set; }
    public Func<string, string, string, Task<string?>>? TextInputCallback { get; set; }

    public IntegrationWizardViewModel(
        IKoishiInstallService koishiInstallService,
        IAstrBotInstallService astrBotInstallService,
        IZhenxunInstallService zhenxunInstallService,
        IDDBotInstallService ddbotInstallService,
        IYunzaiInstallService yunzaiInstallService,
        IZeroBotPluginInstallService zeroBotPluginInstallService,
        IOpenClawInstallService openClawInstallService,
        ISelfInfoService selfInfoService,
        IConfigManager configManager,
        ILogger<IntegrationWizardViewModel> logger)
    {
        _koishiInstallService = koishiInstallService;
        _astrBotInstallService = astrBotInstallService;
        _zhenxunInstallService = zhenxunInstallService;
        _ddbotInstallService = ddbotInstallService;
        _yunzaiInstallService = yunzaiInstallService;
        _zeroBotPluginInstallService = zeroBotPluginInstallService;
        _openClawInstallService = openClawInstallService;
        _selfInfoService = selfInfoService;
        _configManager = configManager;
        _logger = logger;

        SelectFrameworkCommand = ReactiveCommand.CreateFromTask<string>(OnSelectFrameworkAsync);
        CancelInstallCommand = ReactiveCommand.Create(OnCancelInstall);

        _uinSubscription = _selfInfoService.UinStream
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(uin =>
            {
                HasUin = !string.IsNullOrEmpty(uin);
                _currentUin = string.IsNullOrEmpty(uin) ? null : uin;
            });
    }

    private async Task OnSelectFrameworkAsync(string framework)
    {
        var frameworkName = framework switch
        {
            "koishi" => "Koishi",
            "astrbot" => "AstrBot",
            "zhenxun" => "真寻Bot",
            "ddbot" => "DDBot",
            "yunzai" => "云崽",
            "zbp" => "ZeroBot-Plugin",
            "openclaw" => "OpenClaw",
            _ => framework
        };

        if (ConfirmInstallCallback == null) return;

        bool isInstalled = framework switch
        {
            "koishi" => _koishiInstallService.IsInstalled,
            "astrbot" => _astrBotInstallService.IsInstalled,
            "zhenxun" => _zhenxunInstallService.IsInstalled,
            "ddbot" => _ddbotInstallService.IsInstalled,
            "yunzai" => _yunzaiInstallService.IsInstalled,
            "zbp" => _zeroBotPluginInstallService.IsInstalled,
            "openclaw" => _openClawInstallService.IsInstalled,
            _ => false
        };

        if (isInstalled && FrameworkActionCallback != null)
        {
            var config = await _configManager.LoadConfigAsync();
            var autoStartEnabled = config.AutoStartFrameworks.Contains(framework);
            var showAutoStart = framework != "openclaw";

            // 0=启动, 1=重新安装, 2=打开目录, 3=查看文档, 4=取消
            var (choice, autoStart) = await FrameworkActionCallback(frameworkName, $"{frameworkName} 已安装，请选择操作：", autoStartEnabled, showAutoStart);

            // 保存自动启动状态
            if (autoStart != autoStartEnabled)
            {
                config = await _configManager.LoadConfigAsync();
                if (autoStart && !config.AutoStartFrameworks.Contains(framework))
                    config.AutoStartFrameworks.Add(framework);
                else if (!autoStart)
                    config.AutoStartFrameworks.Remove(framework);
                await _configManager.SaveConfigAsync(config);
            }

            if (choice == 4) return; // 取消

            if (choice == 0) // 启动
            {
                StartFramework(framework);
                return;
            }

            if (choice == 2) // 打开目录
            {
                OpenFrameworkDir(framework);
                return;
            }

            if (choice == 3) // 查看文档
            {
                OpenFrameworkDocs(framework);
                return;
            }

            // choice == 1: 重新安装，继续执行下面的安装流程
        }
        else if (!isInstalled && ThreeChoiceCallback != null)
        {
            // 0=安装, 1=查看文档, 2=取消
            var choice = await ThreeChoiceCallback(frameworkName, $"是否下载并自动安装配置 {frameworkName}？");
            
            if (choice == 2) return; // 取消
            
            if (choice == 1) // 查看文档
            {
                OpenFrameworkDocs(framework);
                return;
            }
            
            // choice == 0: 安装，继续执行下面的安装流程
        }

        if (framework == "koishi")
            await EnsureSatoriEnabledAsync();

        await InstallFrameworkAsync(framework, true);
    }

    private void StartFramework(string framework)
    {
        switch (framework)
        {
            case "koishi":
                _koishiInstallService.StartKoishi();
                break;
            case "astrbot":
                _astrBotInstallService.StartAstrBot();
                break;
            case "zhenxun":
                _zhenxunInstallService.StartZhenxun();
                break;
            case "ddbot":
                _ddbotInstallService.StartDDBot();
                break;
            case "yunzai":
                _yunzaiInstallService.StartYunzai();
                break;
            case "zbp":
                _zeroBotPluginInstallService.StartZeroBotPlugin();
                break;
            case "openclaw":
                if (_openClawInstallService.IsFirstRun)
                    _openClawInstallService.StartOnboard();
                else
                    _openClawInstallService.StartGateway();
                break;
        }
    }

    private void OpenFrameworkDocs(string framework)
    {
        var url = framework switch
        {
            "koishi" => "https://koishi.chat/",
            "astrbot" => "https://astrbot.app/",
            "zhenxun" => "https://zhenxun-org.github.io/zhenxun_bot/",
            "ddbot" => "https://ddbot.songlist.icu/",
            "yunzai" => "https://yunzai-bot.com/",
            "zbp" => "https://github.com/FloatTech/ZeroBot-Plugin",
            "openclaw" => "https://github.com/constansino/openclaw_qq",
            _ => ""
        };

        if (!string.IsNullOrEmpty(url))
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = url,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "打开文档失败: {Url}", url);
            }
        }
    }

    private void OpenFrameworkDir(string framework)
    {
        var dir = framework switch
        {
            "koishi" => Path.GetFullPath("bin/koishi"),
            "astrbot" => Path.GetFullPath("bin/astrbot"),
            "zhenxun" => Path.GetFullPath("bin/zhenxun"),
            "ddbot" => Path.GetFullPath("bin/ddbot"),
            "yunzai" => Path.GetFullPath("bin/yunzai"),
            "zbp" => Path.GetFullPath("bin/ZeroBot-Plugin"),
            "openclaw" => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".openclaw"),
            _ => ""
        };

        if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = dir,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "打开目录失败: {Dir}", dir);
            }
        }
    }

    private async Task<int> EnsureSatoriEnabledAsync()
    {
        if (string.IsNullOrEmpty(_currentUin)) return 5600;

        var configPath = Path.Combine("bin", "llbot", "data", $"config_{_currentUin}.json");
        LLBotConfig config;

        if (File.Exists(configPath))
        {
            var json = await File.ReadAllTextAsync(configPath);
            config = JsonSerializer.Deserialize<LLBotConfig>(json) ?? LLBotConfig.Default;
        }
        else
        {
            config = LLBotConfig.Default;
        }

        if (config.Satori.Enable)
        {
            _logger.LogInformation("Satori 已启用，端口: {Port}", config.Satori.Port);
            return config.Satori.Port;
        }

        var port = FindAvailablePort(5600);
        config.Satori.Enable = true;
        config.Satori.Port = port;
        config.Satori.Host = "127.0.0.1";

        var dir = Path.GetDirectoryName(configPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        await File.WriteAllTextAsync(configPath, JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true }));
        _logger.LogInformation("已启用 Satori，端口: {Port}", port);
        return port;
    }

    private static int FindAvailablePort(int startPort)
    {
        for (int port = startPort; port < startPort + 100; port++)
        {
            try
            {
                using var listener = new TcpListener(System.Net.IPAddress.Loopback, port);
                listener.Start();
                listener.Stop();
                return port;
            }
            catch { }
        }
        return startPort;
    }

    private async Task InstallFrameworkAsync(string framework, bool forceReinstall = false)
    {
        ResetProgress();
        IsInstalling = true;
        _cts = new CancellationTokenSource();

        try
        {
            var progress = new Progress<InstallProgress>(p =>
            {
                CurrentStep = p.Step;
                TotalSteps = p.TotalSteps;
                CurrentStepName = p.StepName;
                StatusText = p.Status;
                ProgressPercentage = p.Percentage;
                HasError = p.HasError;
                ErrorMessage = p.ErrorMessage;
                IsCompleted = p.IsCompleted;
            });

            bool success = framework switch
            {
                "koishi" => await _koishiInstallService.InstallAsync(forceReinstall, progress, _cts.Token),
                "astrbot" => await _astrBotInstallService.InstallAsync(progress, _cts.Token),
                "zhenxun" => await _zhenxunInstallService.InstallAsync(progress, _cts.Token),
                "ddbot" => await _ddbotInstallService.InstallAsync(progress, _cts.Token),
                "yunzai" => await _yunzaiInstallService.InstallAsync(progress, _cts.Token),
                "zbp" => await _zeroBotPluginInstallService.InstallAsync(progress, _cts.Token),
                "openclaw" => await _openClawInstallService.InstallAsync(progress, _cts.Token),
                _ => false
            };

            if (success)
            {
                _logger.LogInformation("{Framework} 安装成功", framework);
                this.RaisePropertyChanged(nameof(KoishiInstalled));
                this.RaisePropertyChanged(nameof(AstrBotInstalled));
                this.RaisePropertyChanged(nameof(ZhenxunInstalled));
                this.RaisePropertyChanged(nameof(DDBotInstalled));
                this.RaisePropertyChanged(nameof(YunzaiInstalled));
                this.RaisePropertyChanged(nameof(ZeroBotPluginInstalled));
                this.RaisePropertyChanged(nameof(OpenClawInstalled));
                
                if (framework == "koishi")
                    await OnKoishiInstallCompletedAsync();
                else if (framework == "astrbot")
                    await OnAstrBotInstallCompletedAsync();
                else if (framework == "zhenxun")
                    await OnZhenxunInstallCompletedAsync();
                else if (framework == "ddbot")
                    await OnDDBotInstallCompletedAsync();
                else if (framework == "yunzai")
                    await OnYunzaiInstallCompletedAsync();
                else if (framework == "zbp")
                    await OnZeroBotPluginInstallCompletedAsync();
                else if (framework == "openclaw")
                    await OnOpenClawInstallCompletedAsync();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "安装 {Framework} 失败", framework);
            HasError = true;
            ErrorMessage = ex.Message;
        }
        finally
        {
            IsInstalling = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    private void OnCancelInstall()
    {
        _cts?.Cancel();
        IsInstalling = false;
        StatusText = "安装已取消";
    }

    private async Task OnKoishiInstallCompletedAsync()
    {
        var installPath = Path.GetFullPath("bin/koishi");
        
        CreateStartBat(installPath, "koi.exe");

        Action startKoishi = () =>
        {
            try
            {
                var koiExe = Path.Combine(installPath, "koi.exe");
                if (File.Exists(koiExe))
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = koiExe,
                        WorkingDirectory = installPath,
                        UseShellExecute = true
                    });
                    _logger.LogInformation("Koishi 已启动");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "启动 Koishi 失败");
            }
        };
        
        if (ShowAutoCloseAlertCallback != null)
            await ShowAutoCloseAlertCallback("Koishi 配置完成", 
                $"安装路径: {installPath}\n\n等待框架启动完成后，请在群里发送 help 测试机器人是否有响应。\n\n3秒后将自动启动 Koishi...", 3, startKoishi);
    }

    private async Task OnAstrBotInstallCompletedAsync()
    {
        var installPath = Path.GetFullPath("bin/astrbot");
        
        await ConfigureAstrBotDefaultAsync();
        CreateStartBat(installPath, "main.py", true);

        Action startAstrBot = () =>
        {
            _astrBotInstallService.StartAstrBot();
            _ = ConfigureLLBotWebSocketAsync(6199, "/ws", "AstrBot");
        };
        
        if (ShowAutoCloseAlertCallback != null)
            await ShowAutoCloseAlertCallback("AstrBot 配置完成",
                $"安装路径: {installPath}\n\nAstrBot 已安装完成，启动完成后群里@机器人发送 help 可查看功能\n\n3秒后将自动启动 AstrBot...", 3, startAstrBot);
    }

    private async Task ConfigureAstrBotDefaultAsync()
    {
        var defaultPyPath = Path.GetFullPath("bin/astrbot/astrbot/core/config/default.py");
        if (!File.Exists(defaultPyPath))
        {
            _logger.LogWarning("default.py 不存在，跳过配置");
            return;
        }

        try
        {
            var content = await File.ReadAllTextAsync(defaultPyPath);
            
            // 检查 DEFAULT_CONFIG 的 platform 是否已配置（不是空数组）
            // 注意：不能用 Contains("aiocqhttp") 因为 CONFIG_METADATA_2 模板中也有这个字符串
            const string emptyPlatform = "\"platform\": [],";
            if (!content.Contains(emptyPlatform))
            {
                _logger.LogInformation("AstrBot default.py platform 已配置");
                return;
            }

            // 替换为包含 llbot 配置的 platform（保持 4 空格缩进）
            const string newPlatform = 
                "\"platform\": [\n" +
                "        {\n" +
                "            \"id\": \"llbot\",\n" +
                "            \"type\": \"aiocqhttp\",\n" +
                "            \"enable\": True,\n" +
                "            \"ws_reverse_host\": \"0.0.0.0\",\n" +
                "            \"ws_reverse_port\": 6199,\n" +
                "            \"ws_reverse_token\": \"\"\n" +
                "        }\n" +
                "    ],";

            content = content.Replace(emptyPlatform, newPlatform);
            await File.WriteAllTextAsync(defaultPyPath, content, System.Text.Encoding.UTF8);
            _logger.LogInformation("已配置 AstrBot default.py aiocqhttp");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "配置 AstrBot default.py 失败");
        }
    }

    private async Task ConfigureLLBotWebSocketAsync(int wsPort, string path = "", string? frameworkName = null)
    {
        if (string.IsNullOrEmpty(_currentUin)) return;

        var configPath = Path.Combine("bin", "llbot", "data", $"config_{_currentUin}.json");
        if (!File.Exists(configPath)) return;

        try
        {
            var json = await File.ReadAllTextAsync(configPath);
            var config = JsonSerializer.Deserialize<LLBotConfig>(json) ?? LLBotConfig.Default;
            var changed = false;
            
            var wsUrl = string.IsNullOrEmpty(path) 
                ? $"ws://127.0.0.1:{wsPort}" 
                : $"ws://127.0.0.1:{wsPort}{path}";
            
            // 检查是否已存在 ws-reverse 连接到该 URL（无论启用状态）
            var existingWs = config.OB11.Connect.FirstOrDefault(c =>
                c.Type == "ws-reverse" && c.Url == wsUrl);

            if (existingWs != null)
            {
                // 如果已存在，确保它是启用的
                if (!existingWs.Enable)
                {
                    existingWs.Enable = true;
                    changed = true;
                    _logger.LogInformation("已启用现有的 WebSocket 连接配置: {Url}", wsUrl);
                }
                else
                {
                    _logger.LogInformation("LLBot 已存在 WebSocket 连接配置: {Url}", wsUrl);
                }

                if (!config.OB11.Enable)
                {
                    config.OB11.Enable = true;
                    changed = true;
                }

                if (changed)
                {
                    await File.WriteAllTextAsync(configPath,
                        JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true }));
                }
                return;
            }

            // 添加新的 ws-reverse 连接
            config.OB11.Connect.Add(new OB11Connection
            {
                Type = "ws-reverse",
                Name = frameworkName,
                Enable = true,
                Url = wsUrl,
                Token = "",
                MessageFormat = "array",
                ReportSelfMessage = false,
                HeartInterval = 60000
            });

            if (!config.OB11.Enable)
            {
                config.OB11.Enable = true;
            }

            await File.WriteAllTextAsync(configPath, 
                JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true }));
            _logger.LogInformation("已添加 LLBot WebSocket 客户端配置: {Url}", wsUrl);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "配置 LLBot WebSocket 失败");
        }
    }

    /// <summary>
    /// 配置 LLBot WebSocket 服务端(正向)连接，供 ZeroBot-Plugin 等框架主动连接
    /// </summary>
    private async Task ConfigureLLBotWebSocketServerAsync(int wsPort, string? frameworkName = null)
    {
        if (string.IsNullOrEmpty(_currentUin)) return;

        var configPath = Path.Combine("bin", "llbot", "data", $"config_{_currentUin}.json");
        if (!File.Exists(configPath)) return;

        try
        {
            var json = await File.ReadAllTextAsync(configPath);
            var config = JsonSerializer.Deserialize<LLBotConfig>(json) ?? LLBotConfig.Default;
            var changed = false;

            // 检查是否已存在 WebSocket 服务端配置到该端口（无论启用状态）
            var existingWs = config.OB11.Connect.FirstOrDefault(c =>
                c.Type == "ws" && c.Port == wsPort);

            if (existingWs != null)
            {
                // 如果已存在，确保它是启用的
                if (!existingWs.Enable)
                {
                    existingWs.Enable = true;
                    changed = true;
                    _logger.LogInformation("已启用现有的 WebSocket 服务端配置: 端口 {Port}", wsPort);
                }
                else
                {
                    _logger.LogInformation("LLBot 已存在 WebSocket 服务端配置: 端口 {Port}", wsPort);
                }

                if (!config.OB11.Enable)
                {
                    config.OB11.Enable = true;
                    changed = true;
                }

                if (changed)
                {
                    await File.WriteAllTextAsync(configPath,
                        JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true }));
                }
                return;
            }

            config.OB11.Connect.Add(new OB11Connection
            {
                Type = "ws",
                Name = frameworkName,
                Enable = true,
                Host = "127.0.0.1",
                Port = wsPort,
                Token = "",
                MessageFormat = "array",
                ReportSelfMessage = false,
                HeartInterval = 60000
            });

            if (!config.OB11.Enable)
            {
                config.OB11.Enable = true;
            }

            await File.WriteAllTextAsync(configPath,
                JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true }));
            _logger.LogInformation("已添加 LLBot WebSocket 服务端配置: 端口 {Port}", wsPort);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "配置 LLBot WebSocket 服务端失败");
        }
    }

    private async Task OnZhenxunInstallCompletedAsync()
    {
        var installPath = _zhenxunInstallService.ZhenxunPath;
        
        var superUser = _currentUin ?? "";
        var nbPort = FindAvailablePort(8080);
        await _zhenxunInstallService.ConfigureEnvAsync(superUser, nbPort);
        
        await ConfigureLLBotWebSocketAsync(nbPort, "/onebot/v11/ws", "真寻Bot");
        
        CreateStartBat(installPath, "bot.py", true);

        Action startZhenxun = () => _zhenxunInstallService.StartZhenxun();
        
        if (ShowAutoCloseAlertCallback != null)
            await ShowAutoCloseAlertCallback("真寻Bot 安装完成",
                $"安装路径: {installPath}\n\n群里@机器人发送 help 查看功能\n\n3秒后将自动启动真寻Bot...", 3, startZhenxun);
    }

    private async Task OnDDBotInstallCompletedAsync()
    {
        var installPath = _ddbotInstallService.DDBotPath;
        
        // 配置 LLBot WebSocket 客户端连接到 DDBot
        await ConfigureLLBotWebSocketAsync(15630, "/ws", "DDBot");
        
        CreateStartBat(installPath, "DDBOT-WSa.exe");

        Action startDDBot = () => _ddbotInstallService.StartDDBot();
        
        if (ShowAutoCloseAlertCallback != null)
            await ShowAutoCloseAlertCallback("DDBot 安装完成",
                $"安装路径: {installPath}\n\nDDBot 是一个直播/状态推送框架\n\n3秒后将自动启动 DDBot...", 3, startDDBot);
    }

    private async Task OnYunzaiInstallCompletedAsync()
    {
        var installPath = _yunzaiInstallService.YunzaiPath;
        
        // 配置 LLBot WebSocket 客户端连接到云崽
        await ConfigureLLBotWebSocketAsync(2536, "/OneBotv11", "云崽");
        
        CreateYunzaiStartBat(installPath);

        Action startYunzai = () => _yunzaiInstallService.StartYunzai();
        
        if (ShowAutoCloseAlertCallback != null)
            await ShowAutoCloseAlertCallback("云崽安装完成",
                $"安装路径: {installPath}\n\n云崽是一个原神QQ机器人\n\n3秒后将自动启动云崽...", 3, startYunzai);
    }

    private async Task OnZeroBotPluginInstallCompletedAsync()
    {
        var installPath = _zeroBotPluginInstallService.ZeroBotPluginPath;

        // ZeroBot-Plugin 需要 WebSocket 服务端(正向) 端口 6700
        await ConfigureLLBotWebSocketServerAsync(6700, "zbp");

        CreateStartBat(installPath, "zbp.exe");

        Action startZbp = () => _zeroBotPluginInstallService.StartZeroBotPlugin();

        if (ShowAutoCloseAlertCallback != null)
            await ShowAutoCloseAlertCallback("ZeroBot-Plugin 安装完成",
                $"安装路径: {installPath}\n\nZeroBot-Plugin 是一个基于 ZeroBot 的多功能群管/娱乐插件集\n\n3秒后将自动启动 ZeroBot-Plugin...", 3, startZbp);
    }

    private async Task OnOpenClawInstallCompletedAsync()
    {
        // 智能查找/配置 OneBot11 正向 WS 端口
        var wsPort = await EnsureOpenClawWebSocketAsync();

        // 询问主人QQ号
        string? adminQQ = null;
        if (TextInputCallback != null)
            adminQQ = await TextInputCallback("设置主人QQ",
                "OpenClaw 权限特殊，需要设置主人QQ，只有主人才可使用功能", "主人QQ号");

        // 首次安装完成后，弹出终端执行 onboard，并监控进程退出
        Action startOnboard = () =>
        {
            _openClawInstallService.StartOnboardAndWatch(() =>
            {
                Dispatcher.UIThread.Post(async () =>
                {
                    _logger.LogInformation("onboard 进程正常退出，自动配置并启动 gateway");

                    // 配置 openclaw.json（含主人QQ）
                    _openClawInstallService.EnsureOpenClawConfigured(wsPort, adminQQ);

                    // 启动 gateway
                    _openClawInstallService.StartGateway();

                    if (ShowAlertCallback != null)
                        await ShowAlertCallback("OpenClaw 已就绪",
                            $"检测到初始化配置完成，已自动配置 WebSocket 端口 {wsPort} 并启动 OpenClaw Gateway。");
                });
            });
        };

        if (ShowAutoCloseAlertCallback != null)
            await ShowAutoCloseAlertCallback("OpenClaw 安装完成",
                $"OpenClaw 已安装完成，WebSocket 端口: {wsPort}\n\n3秒后将弹出终端窗口进行初始化配置（openclaw onboard），完成后将自动启动 Gateway。", 3, startOnboard);
    }

    /// <summary>
    /// 查找或创建 OpenClaw 使用的 OneBot11 正向 WS：
    /// 1. 优先查找名为 OpenClaw 的正向 ws，启用并返回端口
    /// 2. 其次查找已启用的正向 ws，直接复用端口
    /// 3. 都没有则查找可用端口新建一个
    /// </summary>
    private async Task<int> EnsureOpenClawWebSocketAsync()
    {
        if (string.IsNullOrEmpty(_currentUin)) return 3001;

        var configPath = Path.Combine("bin", "llbot", "data", $"config_{_currentUin}.json");
        if (!File.Exists(configPath)) return 3001;

        try
        {
            var json = await File.ReadAllTextAsync(configPath);
            var config = JsonSerializer.Deserialize<LLBotConfig>(json) ?? LLBotConfig.Default;
            var changed = false;

            // 1. 查找名为 OpenClaw 的正向 ws
            var openclawWs = config.OB11.Connect.FirstOrDefault(c =>
                c.Type == "ws" && string.Equals(c.Name, "OpenClaw", StringComparison.OrdinalIgnoreCase));
            if (openclawWs != null)
            {
                if (!openclawWs.Enable)
                {
                    openclawWs.Enable = true;
                    changed = true;
                }
                if (!config.OB11.Enable)
                {
                    config.OB11.Enable = true;
                    changed = true;
                }
                if (changed)
                {
                    await File.WriteAllTextAsync(configPath,
                        JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true }));
                }
                _logger.LogInformation("复用已有 OpenClaw WebSocket 配置，端口: {Port}", openclawWs.Port);
                return openclawWs.Port;
            }

            // 2. 查找已启用的正向 ws
            var enabledWs = config.OB11.Connect.FirstOrDefault(c =>
                c.Type == "ws" && c.Enable);
            if (enabledWs != null)
            {
                _logger.LogInformation("复用已启用的正向 WebSocket，端口: {Port}", enabledWs.Port);
                return enabledWs.Port;
            }

            // 3. 没有任何正向 ws，找可用端口新建
            var port = FindAvailablePort(3001);
            config.OB11.Connect.Add(new OB11Connection
            {
                Type = "ws",
                Name = "OpenClaw",
                Enable = true,
                Host = "127.0.0.1",
                Port = port,
                Token = "",
                MessageFormat = "array",
                ReportSelfMessage = false,
                HeartInterval = 60000
            });

            if (!config.OB11.Enable)
                config.OB11.Enable = true;

            await File.WriteAllTextAsync(configPath,
                JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true }));
            _logger.LogInformation("已新建 OpenClaw WebSocket 服务端配置，端口: {Port}", port);
            return port;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "配置 OpenClaw WebSocket 失败");
            return 3001;
        }
    }

    private void CreateYunzaiStartBat(string installPath)
    {
        try
        {
            if (PlatformHelper.IsWindows)
            {
                var batPath = Path.Combine(installPath, "start.bat");
                var nodeExe = Path.GetFullPath(Path.Combine(_yunzaiInstallService.Node24Path, "node.exe"));
                var content = $"@echo off\r\ncd /d \"%~dp0\"\r\n\"{nodeExe}\" .\r\npause\r\n";
                File.WriteAllText(batPath, content, System.Text.Encoding.Default);
                _logger.LogInformation("已创建云崽启动脚本: {Path}", batPath);
            }
            else
            {
                // macOS/Linux
                var shPath = Path.Combine(installPath, "start.sh");
                var nodeExe = Path.GetFullPath(Path.Combine(_yunzaiInstallService.Node24Path, "bin/node"));
                var content = $"#!/bin/bash\ncd \"$(dirname \"$0\")\"\n\"{nodeExe}\" .\nread -p \"Press enter to exit...\"\n";
                File.WriteAllText(shPath, content, System.Text.Encoding.UTF8);

                // 添加执行权限
                var chmod = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "chmod",
                    Arguments = $"+x \"{shPath}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true
                });
                chmod?.WaitForExit();

                _logger.LogInformation("已创建云崽启动脚本: {Path}", shPath);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "创建云崽启动脚本失败");
        }
    }

    private void CreateStartBat(string installPath, string executable, bool isPython = false)
    {
        try
        {
            if (PlatformHelper.IsWindows)
            {
                var batPath = Path.Combine(installPath, "start.bat");
                string content;

                if (isPython)
                {
                    content = $"@echo off\r\ncd /d \"%~dp0\"\r\npython {executable}\r\npause\r\n";
                }
                else
                {
                    content = $"@echo off\r\ncd /d \"%~dp0\"\r\nstart \"\" \"{executable}\"\r\n";
                }

                File.WriteAllText(batPath, content, System.Text.Encoding.Default);
                _logger.LogInformation("已创建启动脚本: {Path}", batPath);
            }
            else
            {
                // macOS/Linux
                var shPath = Path.Combine(installPath, "start.sh");
                var absExecutable = Path.GetFullPath(Path.Combine(installPath, executable));
                string content;

                if (isPython)
                {
                    content = $"#!/bin/bash\ncd \"$(dirname \"$0\")\"\npython3 {executable}\nread -p \"Press enter to exit...\"\n";
                }
                else
                {
                    content = $"#!/bin/bash\ncd \"$(dirname \"$0\")\"\n\"{absExecutable}\"\nread -p \"Press enter to exit...\"\n";
                }

                File.WriteAllText(shPath, content, System.Text.Encoding.UTF8);

                // 添加执行权限
                var chmod = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "chmod",
                    Arguments = $"+x \"{shPath}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true
                });
                chmod?.WaitForExit();

                _logger.LogInformation("已创建启动脚本: {Path}", shPath);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "创建启动脚本失败");
        }
    }

    private void ResetProgress()
    {
        CurrentStep = 0;
        TotalSteps = 0;
        CurrentStepName = "";
        StatusText = "";
        ProgressPercentage = 0;
        HasError = false;
        ErrorMessage = null;
        IsCompleted = false;
    }

    public void OnPageEnter()
    {
        var uin = _selfInfoService.CurrentUin;
        if (!string.IsNullOrEmpty(uin))
        {
            _currentUin = uin;
            HasUin = true;
        }
    }

    public void Dispose() => _uinSubscription.Dispose();
}
