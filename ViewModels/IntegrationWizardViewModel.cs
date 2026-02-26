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
    public bool DDBotInstalled => _ddbotInstallService.IsInstalled;
    public bool YunzaiInstalled => _yunzaiInstallService.IsInstalled;
    public bool ZeroBotPluginInstalled => _zeroBotPluginInstallService.IsInstalled;
    public bool ShowZeroBotPlugin => !PlatformHelper.IsMacOS;

    public ReactiveCommand<string, Unit> SelectFrameworkCommand { get; }
    public ReactiveCommand<Unit, Unit> CancelInstallCommand { get; }

    public Func<string, string, Task<bool>>? ConfirmInstallCallback { get; set; }
    public Func<string, string, Task>? ShowAlertCallback { get; set; }
    public Func<string, string, int, Action, Task>? ShowAutoCloseAlertCallback { get; set; }
    public Func<string, string, Task<int>>? ThreeChoiceCallback { get; set; }
    public Func<string, string, Task<int>>? FourChoiceCallback { get; set; }

    public IntegrationWizardViewModel(
        IKoishiInstallService koishiInstallService,
        IAstrBotInstallService astrBotInstallService,
        IZhenxunInstallService zhenxunInstallService,
        IDDBotInstallService ddbotInstallService,
        IYunzaiInstallService yunzaiInstallService,
        IZeroBotPluginInstallService zeroBotPluginInstallService,
        ISelfInfoService selfInfoService,
        ILogger<IntegrationWizardViewModel> logger)
    {
        _koishiInstallService = koishiInstallService;
        _astrBotInstallService = astrBotInstallService;
        _zhenxunInstallService = zhenxunInstallService;
        _ddbotInstallService = ddbotInstallService;
        _yunzaiInstallService = yunzaiInstallService;
        _zeroBotPluginInstallService = zeroBotPluginInstallService;
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
            "ddbot" => "DDBot",
            "yunzai" => "云崽",
            "zbp" => "ZeroBot-Plugin",
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
            _ => false
        };

        if (isInstalled && FourChoiceCallback != null)
        {
            // 0=启动, 1=重新安装, 2=查看文档, 3=取消
            var choice = await FourChoiceCallback(frameworkName, $"{frameworkName} 已安装，请选择操作：");
            
            if (choice == 3) return; // 取消
            
            if (choice == 0) // 启动
            {
                StartFramework(framework);
                return;
            }
            
            if (choice == 2) // 查看文档
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
