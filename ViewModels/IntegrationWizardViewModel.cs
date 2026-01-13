using System;
using System.IO;
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
    private readonly IResourceMonitor _resourceMonitor;
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

    public ReactiveCommand<string, Unit> SelectFrameworkCommand { get; }
    public ReactiveCommand<Unit, Unit> CancelInstallCommand { get; }

    public Func<string, string, Task<bool>>? ConfirmInstallCallback { get; set; }
    public Func<string, string, Task>? ShowAlertCallback { get; set; }

    public IntegrationWizardViewModel(
        IKoishiInstallService koishiInstallService,
        IResourceMonitor resourceMonitor,
        ILogger<IntegrationWizardViewModel> logger)
    {
        _koishiInstallService = koishiInstallService;
        _resourceMonitor = resourceMonitor;
        _logger = logger;

        SelectFrameworkCommand = ReactiveCommand.CreateFromTask<string>(OnSelectFrameworkAsync);
        CancelInstallCommand = ReactiveCommand.Create(OnCancelInstall);

        _uinSubscription = _resourceMonitor.UinStream
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(info =>
            {
                HasUin = !string.IsNullOrEmpty(info.Uin);
                _currentUin = info.Uin;
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
                _ => false
            };

            if (success)
            {
                _logger.LogInformation("{Framework} 安装成功", framework);
                this.RaisePropertyChanged(nameof(KoishiInstalled));
                if (framework == "koishi")
                    await OnKoishiInstallCompletedAsync();
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
        if (ShowAlertCallback != null)
            await ShowAlertCallback("Koishi 配置完成", 
                "等待框架启动完成后，请在群里发送 help 测试机器人是否有响应。\n\n3秒后将自动启动 Koishi...");

        await Task.Delay(3000);

        _ = Task.Run(() =>
        {
            try
            {
                var koiExe = Path.GetFullPath("bin/koishi/koi.exe");
                if (File.Exists(koiExe))
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = koiExe,
                        WorkingDirectory = Path.GetDirectoryName(koiExe),
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

    public void Dispose() => _uinSubscription.Dispose();
}
