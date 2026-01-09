using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using LuckyLilliaDesktop.Models;
using LuckyLilliaDesktop.Services;
using Microsoft.Extensions.Logging;
using ReactiveUI;
using System;
using System.Collections.Generic;
using System.IO;
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
    private bool _startupEnabled;
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

    public bool StartupEnabled
    {
        get => _startupEnabled;
        set
        {
            if (this.RaiseAndSetIfChanged(ref _startupEnabled, value) && value != Utils.StartupManager.IsStartupEnabled())
            {
                if (value)
                    Utils.StartupManager.EnableStartup();
                else
                    Utils.StartupManager.DisableStartup();
            }
        }
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
            var path = await BrowseFileAsync("选择 QQ 可执行文件", new[] { "exe" }, QQPath);
            if (!string.IsNullOrEmpty(path))
                QQPath = path;
        });

        BrowsePmhqCommand = ReactiveCommand.CreateFromTask(async () =>
        {
            var path = await BrowseFileAsync("选择 PMHQ 可执行文件", new[] { "exe" }, PmhqPath);
            if (!string.IsNullOrEmpty(path))
                PmhqPath = path;
        });

        BrowseLLBotCommand = ReactiveCommand.CreateFromTask(async () =>
        {
            var path = await BrowseFileAsync("选择 LLBot 脚本文件", new[] { "js" }, LLBotPath);
            if (!string.IsNullOrEmpty(path))
                LLBotPath = path;
        });

        BrowseNodeCommand = ReactiveCommand.CreateFromTask(async () =>
        {
            var path = await BrowseFileAsync("选择 Node.js 可执行文件", new[] { "exe" }, NodePath);
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

    private async Task<string?> BrowseFileAsync(string title, string[] extensions, string? currentPath = null)
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

                // 处理初始目录
                string? suggestedStartLocation = null;
                if (!string.IsNullOrEmpty(currentPath))
                {
                    // 如果是相对路径，转换为绝对路径
                    var fullPath = Path.IsPathRooted(currentPath) 
                        ? currentPath 
                        : Path.GetFullPath(currentPath);
                    
                    // 如果文件存在，使用文件所在目录；否则使用当前工作目录
                    if (File.Exists(fullPath))
                    {
                        suggestedStartLocation = Path.GetDirectoryName(fullPath);
                    }
                    else if (Directory.Exists(Path.GetDirectoryName(fullPath)))
                    {
                        suggestedStartLocation = Path.GetDirectoryName(fullPath);
                    }
                }

                // 如果没有合适的初始目录，使用当前工作目录
                suggestedStartLocation ??= Environment.CurrentDirectory;

                var options = new FilePickerOpenOptions
                {
                    Title = title,
                    AllowMultiple = false,
                    FileTypeFilter = filters
                };

                // 设置初始目录
                if (Directory.Exists(suggestedStartLocation))
                {
                    try
                    {
                        options.SuggestedStartLocation = await mainWindow.StorageProvider.TryGetFolderFromPathAsync(suggestedStartLocation);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "设置初始目录失败: {Path}", suggestedStartLocation);
                    }
                }

                var result = await mainWindow.StorageProvider.OpenFilePickerAsync(options);

                if (result.Count > 0)
                {
                    var selectedPath = result[0].Path.LocalPath;
                    
                    // 尝试转换为相对路径（如果在当前工作目录下）
                    try
                    {
                        var currentDir = Environment.CurrentDirectory;
                        var relativePath = Path.GetRelativePath(currentDir, selectedPath);
                        
                        // 如果相对路径更短且不包含 ".."，使用相对路径
                        if (relativePath.Length < selectedPath.Length && !relativePath.StartsWith(".."))
                        {
                            return relativePath;
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "转换相对路径失败，使用绝对路径");
                    }
                    
                    return selectedPath;
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
            _startupEnabled = Utils.StartupManager.IsStartupEnabled();
            this.RaisePropertyChanged(nameof(StartupEnabled));
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
