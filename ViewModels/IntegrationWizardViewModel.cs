using System;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Reactive;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LuckyLilliaDesktop.Models;
using LuckyLilliaDesktop.Services;
using Microsoft.Extensions.Logging;
using ReactiveUI;

namespace LuckyLilliaDesktop.ViewModels;

public class IntegrationWizardViewModel : ViewModelBase, IDisposable
{
    private readonly ILogger<IntegrationWizardViewModel> _logger;
    private readonly IKoishiInstallService _koishiInstallService;
    private readonly IAstrBotInstallService _astrBotInstallService;
    private readonly IZhenxunInstallService _zhenxunInstallService;
    private readonly ISelfInfoService _selfInfoService;
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

    public ReactiveCommand<string, Unit> SelectFrameworkCommand { get; }
    public ReactiveCommand<Unit, Unit> CancelInstallCommand { get; }

    public Func<string, string, Task<bool>>? ConfirmInstallCallback { get; set; }
    public Func<string, string, Task>? ShowAlertCallback { get; set; }
    public Func<string, string, int, Action, Task>? ShowAutoCloseAlertCallback { get; set; }
    public Func<string, string, Task<int>>? ThreeChoiceCallback { get; set; }

    public IntegrationWizardViewModel(
        IKoishiInstallService koishiInstallService,
        IAstrBotInstallService astrBotInstallService,
        IZhenxunInstallService zhenxunInstallService,
        ISelfInfoService selfInfoService,
        ILogger<IntegrationWizardViewModel> logger)
    {
        _koishiInstallService = koishiInstallService;
        _astrBotInstallService = astrBotInstallService;
        _zhenxunInstallService = zhenxunInstallService;
        _selfInfoService = selfInfoService;
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
            _ => framework
        };

        if (ConfirmInstallCallback == null) return;

        bool isInstalled = framework switch
        {
            "koishi" => _koishiInstallService.IsInstalled,
            "astrbot" => _astrBotInstallService.IsInstalled,
            "zhenxun" => _zhenxunInstallService.IsInstalled,
            _ => false
        };

        if (isInstalled && ThreeChoiceCallback != null)
        {
            // 0=启动, 1=重新安装, 2=取消
            var choice = await ThreeChoiceCallback(frameworkName, $"{frameworkName} 已安装，请选择操作：");
            
            if (choice == 2) return; // 取消
            
            if (choice == 0) // 启动
            {
                StartFramework(framework);
                return;
            }
            
            // choice == 1: 重新安装，继续执行下面的安装流程
        }
        else if (!isInstalled)
        {
            var confirmed = await ConfirmInstallCallback(frameworkName, $"是否下载并自动安装配置 {frameworkName}？");
            if (!confirmed) return;
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
                _ => false
            };

            if (success)
            {
                _logger.LogInformation("{Framework} 安装成功", framework);
                this.RaisePropertyChanged(nameof(KoishiInstalled));
                this.RaisePropertyChanged(nameof(AstrBotInstalled));
                this.RaisePropertyChanged(nameof(ZhenxunInstalled));
                
                if (framework == "koishi")
                    await OnKoishiInstallCompletedAsync();
                else if (framework == "astrbot")
                    await OnAstrBotInstallCompletedAsync();
                else if (framework == "zhenxun")
                    await OnZhenxunInstallCompletedAsync();
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
            _ = ConfigureLLBotWebSocketAsync(6199, "/ws");
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

    private async Task ConfigureLLBotWebSocketAsync(int wsPort, string path = "")
    {
        if (string.IsNullOrEmpty(_currentUin)) return;

        var configPath = Path.Combine("bin", "llbot", "data", $"config_{_currentUin}.json");
        if (!File.Exists(configPath)) return;

        try
        {
            var json = await File.ReadAllTextAsync(configPath);
            var config = JsonSerializer.Deserialize<LLBotConfig>(json) ?? LLBotConfig.Default;
            
            var wsUrl = string.IsNullOrEmpty(path) 
                ? $"ws://127.0.0.1:{wsPort}" 
                : $"ws://127.0.0.1:{wsPort}{path}";
            
            // 检查是否已存在启用的 ws-reverse 连接到该端口
            var existingWs = config.OB11.Connect.FirstOrDefault(c => 
                c.Type == "ws-reverse" && c.Url == wsUrl && c.Enable);
            
            if (existingWs != null)
            {
                _logger.LogInformation("LLBot 已存在 WebSocket 连接配置: {Url}", wsUrl);
                return;
            }

            // 添加新的 ws-reverse 连接
            config.OB11.Connect.Add(new OB11Connection
            {
                Type = "ws-reverse",
                Enable = true,
                Url = wsUrl,
                Token = "",
                MessageFormat = "array",
                ReportSelfMessage = false,
                HeartInterval = 60000
            });

            await File.WriteAllTextAsync(configPath, 
                JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true }));
            _logger.LogInformation("已添加 LLBot WebSocket 客户端配置: {Url}", wsUrl);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "配置 LLBot WebSocket 失败");
        }
    }

    private async Task OnZhenxunInstallCompletedAsync()
    {
        var installPath = _zhenxunInstallService.ZhenxunPath;
        
        var superUser = _currentUin ?? "";
        var nbPort = FindAvailablePort(8080);
        await _zhenxunInstallService.ConfigureEnvAsync(superUser, nbPort);
        
        await ConfigureLLBotWebSocketAsync(nbPort, "/onebot/v11/ws");
        
        CreateStartBat(installPath, "bot.py", true);

        Action startZhenxun = () => _zhenxunInstallService.StartZhenxun();
        
        if (ShowAutoCloseAlertCallback != null)
            await ShowAutoCloseAlertCallback("真寻Bot 安装完成",
                $"安装路径: {installPath}\n\n群里@机器人发送 help 查看功能\n\n3秒后将自动启动真寻Bot...", 3, startZhenxun);
    }

    private void CreateStartBat(string installPath, string executable, bool isPython = false)
    {
        try
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
