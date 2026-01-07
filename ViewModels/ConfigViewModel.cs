using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using LuckyLilliaDesktop.Models;
using LuckyLilliaDesktop.Services;
using Microsoft.Extensions.Logging;
using ReactiveUI;
using System;
using System.Collections.Generic;
using System.Reactive;
using System.Threading.Tasks;

namespace LuckyLilliaDesktop.ViewModels;

public class ConfigViewModel : ViewModelBase
{
    private readonly IConfigManager _configManager;
    private readonly ILogger<ConfigViewModel> _logger;

    // 路径配置
    private string _qqPath = string.Empty;
    private string _pmhqPath = string.Empty;
    private string _llbotPath = string.Empty;
    private string _nodePath = string.Empty;

    public string QQPath
    {
        get => _qqPath;
        set => this.RaiseAndSetIfChanged(ref _qqPath, value);
    }

    public string PmhqPath
    {
        get => _pmhqPath;
        set => this.RaiseAndSetIfChanged(ref _pmhqPath, value);
    }

    public string LLBotPath
    {
        get => _llbotPath;
        set => this.RaiseAndSetIfChanged(ref _llbotPath, value);
    }

    public string NodePath
    {
        get => _nodePath;
        set => this.RaiseAndSetIfChanged(ref _nodePath, value);
    }

    // 启动选项
    private string _autoLoginQQ = string.Empty;
    private bool _autoStartBot;
    private bool _headless;
    private bool _minimizeToTrayOnStart;
    private bool _startupCommandEnabled;
    private string _startupCommand = string.Empty;

    public string AutoLoginQQ
    {
        get => _autoLoginQQ;
        set => this.RaiseAndSetIfChanged(ref _autoLoginQQ, value);
    }

    public bool AutoStartBot
    {
        get => _autoStartBot;
        set => this.RaiseAndSetIfChanged(ref _autoStartBot, value);
    }

    public bool Headless
    {
        get => _headless;
        set => this.RaiseAndSetIfChanged(ref _headless, value);
    }

    public bool MinimizeToTrayOnStart
    {
        get => _minimizeToTrayOnStart;
        set => this.RaiseAndSetIfChanged(ref _minimizeToTrayOnStart, value);
    }

    public bool StartupCommandEnabled
    {
        get => _startupCommandEnabled;
        set => this.RaiseAndSetIfChanged(ref _startupCommandEnabled, value);
    }

    public string StartupCommand
    {
        get => _startupCommand;
        set => this.RaiseAndSetIfChanged(ref _startupCommand, value);
    }

    // 日志设置
    private bool _logSaveEnabled = true;
    private int _logRetentionHours = 168; // 默认 7 天 (168 小时)

    public bool LogSaveEnabled
    {
        get => _logSaveEnabled;
        set => this.RaiseAndSetIfChanged(ref _logSaveEnabled, value);
    }

    public int LogRetentionHours
    {
        get => _logRetentionHours;
        set => this.RaiseAndSetIfChanged(ref _logRetentionHours, value);
    }

    // 状态标志
    private bool _isSaving;
    public bool IsSaving
    {
        get => _isSaving;
        set => this.RaiseAndSetIfChanged(ref _isSaving, value);
    }

    // 命令
    public ReactiveCommand<Unit, Unit> BrowseQQCommand { get; }
    public ReactiveCommand<Unit, Unit> BrowsePmhqCommand { get; }
    public ReactiveCommand<Unit, Unit> BrowseLLBotCommand { get; }
    public ReactiveCommand<Unit, Unit> BrowseNodeCommand { get; }
    public ReactiveCommand<Unit, Unit> TestCommandCommand { get; }
    public ReactiveCommand<Unit, Unit> SaveConfigCommand { get; }
    public ReactiveCommand<Unit, Unit> LoadConfigCommand { get; }

    public ConfigViewModel(IConfigManager configManager, ILogger<ConfigViewModel> logger)
    {
        _configManager = configManager;
        _logger = logger;

        // 浏览文件命令
        BrowseQQCommand = ReactiveCommand.CreateFromTask(async () =>
        {
            var path = await BrowseFileAsync("选择 QQ 可执行文件", new[] { "exe" });
            if (!string.IsNullOrEmpty(path))
                QQPath = path;
        });

        BrowsePmhqCommand = ReactiveCommand.CreateFromTask(async () =>
        {
            var path = await BrowseFileAsync("选择 PMHQ 可执行文件", new[] { "exe" });
            if (!string.IsNullOrEmpty(path))
                PmhqPath = path;
        });

        BrowseLLBotCommand = ReactiveCommand.CreateFromTask(async () =>
        {
            var path = await BrowseFileAsync("选择 LLBot 脚本文件", new[] { "js" });
            if (!string.IsNullOrEmpty(path))
                LLBotPath = path;
        });

        BrowseNodeCommand = ReactiveCommand.CreateFromTask(async () =>
        {
            var path = await BrowseFileAsync("选择 Node.js 可执行文件", new[] { "exe" });
            if (!string.IsNullOrEmpty(path))
                NodePath = path;
        });

        // 测试命令
        TestCommandCommand = ReactiveCommand.Create(() =>
        {
            if (string.IsNullOrWhiteSpace(StartupCommand))
            {
                _logger.LogWarning("请先输入要测试的命令");
                return;
            }

            try
            {
                var process = new System.Diagnostics.Process
                {
                    StartInfo = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "cmd.exe",
                        Arguments = $"/c start cmd /c \"{StartupCommand} & pause\"",
                        UseShellExecute = true,
                        CreateNoWindow = false
                    }
                };
                process.Start();
                _logger.LogInformation("命令已在新窗口中启动");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "执行命令失败");
            }
        });

        // 保存配置命令
        SaveConfigCommand = ReactiveCommand.CreateFromTask(SaveConfigAsync);

        // 加载配置命令
        LoadConfigCommand = ReactiveCommand.CreateFromTask(LoadConfigAsync);

        // 初始化时加载配置
        _ = LoadConfigAsync();
    }

    private async Task<string?> BrowseFileAsync(string title, string[] extensions)
    {
        try
        {
            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                var mainWindow = desktop.MainWindow;
                if (mainWindow == null) return null;

                var filters = new List<FilePickerFileType>
                {
                    new FilePickerFileType(title)
                    {
                        Patterns = extensions.Length > 0
                            ? Array.ConvertAll(extensions, ext => $"*.{ext}")
                            : new[] { "*.*" }
                    }
                };

                var result = await mainWindow.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
                {
                    Title = title,
                    AllowMultiple = false,
                    FileTypeFilter = filters
                });

                if (result.Count > 0)
                {
                    return result[0].Path.LocalPath;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "打开文件选择对话框失败");
        }
        return null;
    }

    private async Task LoadConfigAsync()
    {
        try
        {
            var config = await _configManager.LoadConfigAsync();

            // 路径配置
            QQPath = config.QQPath;
            PmhqPath = config.PmhqPath;
            LLBotPath = config.LLBotPath;
            NodePath = config.NodePath;

            // 如果 QQ 路径为空，尝试自动检测
            if (string.IsNullOrEmpty(QQPath))
            {
                var detectedPath = Utils.QQPathHelper.GetQQPathFromRegistry();
                if (!string.IsNullOrEmpty(detectedPath))
                {
                    QQPath = detectedPath;
                    _logger.LogInformation("自动检测到 QQ 路径: {Path}", detectedPath);
                }
            }

            // 启动选项
            AutoLoginQQ = config.AutoLoginQQ;
            AutoStartBot = config.AutoStartBot;
            Headless = config.Headless;
            MinimizeToTrayOnStart = config.MinimizeToTrayOnStart;
            StartupCommandEnabled = config.StartupCommandEnabled;
            StartupCommand = config.StartupCommand;

            // 日志设置 (秒转小时)
            LogSaveEnabled = config.LogSaveEnabled;
            LogRetentionHours = config.LogRetentionSeconds / 3600;

            _logger.LogInformation("配置已加载");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "加载配置失败");
        }
    }

    private async Task SaveConfigAsync()
    {
        try
        {
            IsSaving = true;

            var config = new AppConfig
            {
                // 路径配置
                QQPath = QQPath,
                PmhqPath = PmhqPath,
                LLBotPath = LLBotPath,
                NodePath = NodePath,

                // 启动选项
                AutoLoginQQ = AutoLoginQQ,
                AutoStartBot = AutoStartBot,
                Headless = Headless,
                MinimizeToTrayOnStart = MinimizeToTrayOnStart,
                StartupCommandEnabled = StartupCommandEnabled,
                StartupCommand = StartupCommand,

                // 日志设置 (小时转秒)
                LogSaveEnabled = LogSaveEnabled,
                LogRetentionSeconds = LogRetentionHours * 3600
            };

            var success = await _configManager.SaveConfigAsync(config);

            if (success)
            {
                _logger.LogInformation("配置已保存");
            }
            else
            {
                _logger.LogError("保存配置失败");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "保存配置时出错");
        }
        finally
        {
            IsSaving = false;
        }
    }
}
