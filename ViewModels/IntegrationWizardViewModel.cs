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

    public ReactiveCommand<string, Unit> SelectFrameworkCommand { get; }
    public ReactiveCommand<Unit, Unit> CancelInstallCommand { get; }

    public Func<string, string, Task<bool>>? ConfirmInstallCallback { get; set; }
    public Func<string, string, Task>? ShowAlertCallback { get; set; }

    public IntegrationWizardViewModel(
        IKoishiInstallService koishiInstallService,
        IAstrBotInstallService astrBotInstallService,
        ISelfInfoService selfInfoService,
        ILogger<IntegrationWizardViewModel> logger)
    {
        _koishiInstallService = koishiInstallService;
        _astrBotInstallService = astrBotInstallService;
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
            "maimaibot" => "MaimaiBot",
            _ => framework
        };

        if (ConfirmInstallCallback == null) return;

        bool forceReinstall = false;
        
        if (framework == "koishi" && _koishiInstallService.IsInstalled)
        {
            forceReinstall = await ConfirmInstallCallback(frameworkName, 
                $"{frameworkName} 已存在，是否重新下载安装？\n选择「取消」将跳过下载，仅更新配置和依赖。");
        }
        else if (framework == "astrbot" && _astrBotInstallService.IsInstalled)
        {
            forceReinstall = await ConfirmInstallCallback(frameworkName,
                $"{frameworkName} 已存在，是否重新下载安装？\n选择「取消」将跳过安装。");
            if (!forceReinstall) return;
        }
        else
        {
            var confirmed = await ConfirmInstallCallback(frameworkName, $"是否下载并自动安装配置 {frameworkName}？");
            if (!confirmed) return;
        }

        if (framework == "koishi")
            await EnsureSatoriEnabledAsync();

        await InstallFrameworkAsync(framework, forceReinstall);
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
                _ => false
            };

            if (success)
            {
                _logger.LogInformation("{Framework} 安装成功", framework);
                this.RaisePropertyChanged(nameof(KoishiInstalled));
                this.RaisePropertyChanged(nameof(AstrBotInstalled));
                
                if (framework == "koishi")
                    await OnKoishiInstallCompletedAsync();
                else if (framework == "astrbot")
                    await OnAstrBotInstallCompletedAsync();
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
        if (ShowAlertCallback != null)
            await ShowAlertCallback("Koishi 配置完成", 
                $"安装路径: {installPath}\n\n等待框架启动完成后，请在群里发送 help 测试机器人是否有响应。\n\n3秒后将自动启动 Koishi...");

        await Task.Delay(3000);

        _ = Task.Run(() =>
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
        });
    }

    private async Task OnAstrBotInstallCompletedAsync()
    {
        var installPath = Path.GetFullPath("bin/astrbot");
        
        // 启动前配置 default.py
        await ConfigureAstrBotDefaultAsync();
        
        if (ShowAlertCallback != null)
            await ShowAlertCallback("AstrBot 配置完成",
                $"安装路径: {installPath}\n\nAstrBot 已安装完成。\n\n3秒后将自动启动 AstrBot...");

        await Task.Delay(3000);

        _astrBotInstallService.StartAstrBot();
        
        // 配置 LLBot 连接
        await ConfigureLLBotWebSocketAsync(6199);
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
            
            // 检查是否已配置
            if (content.Contains("\"type\": \"aiocqhttp\""))
            {
                _logger.LogInformation("AstrBot default.py 已包含 aiocqhttp 配置");
                return;
            }

            // 查找 "platform": [] 并替换
            const string oldPlatform = "\"platform\": []";
            const string newPlatform = """
"platform": [
        {
            "id": "llbot",
            "type": "aiocqhttp",
            "enable": True,
            "ws_reverse_host": "0.0.0.0",
            "ws_reverse_port": 6199,
            "ws_reverse_token": ""
        }
    ]
""";

            if (content.Contains(oldPlatform))
            {
                content = content.Replace(oldPlatform, newPlatform);
                await File.WriteAllTextAsync(defaultPyPath, content);
                _logger.LogInformation("已配置 AstrBot default.py aiocqhttp");
            }
            else
            {
                _logger.LogWarning("未找到 platform 配置项");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "配置 AstrBot default.py 失败");
        }
    }

    private async Task ConfigureLLBotWebSocketAsync(int wsPort)
    {
        if (string.IsNullOrEmpty(_currentUin)) return;

        var configPath = Path.Combine("bin", "llbot", "data", $"config_{_currentUin}.json");
        if (!File.Exists(configPath)) return;

        try
        {
            var json = await File.ReadAllTextAsync(configPath);
            var config = JsonSerializer.Deserialize<LLBotConfig>(json) ?? LLBotConfig.Default;
            
            var wsUrl = $"ws://localhost:{wsPort}";
            
            // 检查是否已存在启用的 ws-reverse 连接到该端口
            var existingWs = config.OB11.Connect.FirstOrDefault(c => 
                c.Type == "ws-reverse" && c.Url == wsUrl && c.Enable);
            
            if (existingWs != null)
            {
                _logger.LogInformation("LLBot 已存在 AstrBot WebSocket 连接配置");
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
