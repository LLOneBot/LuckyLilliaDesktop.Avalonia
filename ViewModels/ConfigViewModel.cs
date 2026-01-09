using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using LuckyLilliaDesktop.Models;
using LuckyLilliaDesktop.Services;
using LuckyLilliaDesktop.Views;
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

    private AppConfig _savedConfig = new();
    private bool _savedStartupEnabled;

    private bool _hasUnsavedChanges;
    public bool HasUnsavedChanges
    {
        get => _hasUnsavedChanges;
        private set => this.RaiseAndSetIfChanged(ref _hasUnsavedChanges, value);
    }

    private void CheckUnsavedChanges()
    {
        HasUnsavedChanges =
            QQPath != _savedConfig.QQPath ||
            PmhqPath != _savedConfig.PmhqPath ||
            LLBotPath != _savedConfig.LLBotPath ||
            NodePath != _savedConfig.NodePath ||
            AutoLoginQQ != _savedConfig.AutoLoginQQ ||
            AutoStartBot != _savedConfig.AutoStartBot ||
            Headless != _savedConfig.Headless ||
            MinimizeToTrayOnStart != _savedConfig.MinimizeToTrayOnStart ||
            StartupEnabled != _savedStartupEnabled ||
            StartupCommandEnabled != _savedConfig.StartupCommandEnabled ||
            StartupCommand != _savedConfig.StartupCommand ||
            LogSaveEnabled != _savedConfig.LogSaveEnabled ||
            LogRetentionHours != _savedConfig.LogRetentionSeconds / 3600;
    }

    private string _qqPath = string.Empty;
    private string _pmhqPath = string.Empty;
    private string _llbotPath = string.Empty;
    private string _nodePath = string.Empty;

    public string QQPath
    {
        get => _qqPath;
        set { this.RaiseAndSetIfChanged(ref _qqPath, value); CheckUnsavedChanges(); }
    }

    public string PmhqPath
    {
        get => _pmhqPath;
        set { this.RaiseAndSetIfChanged(ref _pmhqPath, value); CheckUnsavedChanges(); }
    }

    public string LLBotPath
    {
        get => _llbotPath;
        set { this.RaiseAndSetIfChanged(ref _llbotPath, value); CheckUnsavedChanges(); }
    }

    public string NodePath
    {
        get => _nodePath;
        set { this.RaiseAndSetIfChanged(ref _nodePath, value); CheckUnsavedChanges(); }
    }

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
        set { this.RaiseAndSetIfChanged(ref _autoLoginQQ, value); CheckUnsavedChanges(); }
    }

    public bool AutoStartBot
    {
        get => _autoStartBot;
        set { this.RaiseAndSetIfChanged(ref _autoStartBot, value); CheckUnsavedChanges(); }
    }

    public bool Headless
    {
        get => _headless;
        set { this.RaiseAndSetIfChanged(ref _headless, value); CheckUnsavedChanges(); }
    }

    public bool MinimizeToTrayOnStart
    {
        get => _minimizeToTrayOnStart;
        set { this.RaiseAndSetIfChanged(ref _minimizeToTrayOnStart, value); CheckUnsavedChanges(); }
    }

    public bool StartupEnabled
    {
        get => _startupEnabled;
        set
        {
            if (_startupEnabled == value) return;
            
            if (value && string.IsNullOrWhiteSpace(AutoLoginQQ))
            {
                ShowStartupConfirmDialog();
            }
            else
            {
                this.RaiseAndSetIfChanged(ref _startupEnabled, value);
                if (value) AutoStartBot = true;
                CheckUnsavedChanges();
            }
        }
    }

    private async void ShowStartupConfirmDialog()
    {
        try
        {
            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                var mainWindow = desktop.MainWindow;
                if (mainWindow == null) return;

                var dialog = new ConfirmDialog("没有填入自动登录QQ号，确定依然要开机自启？");
                var result = await dialog.ShowDialog<bool?>(mainWindow);

                if (result == true)
                {
                    _startupEnabled = true;
                    this.RaisePropertyChanged(nameof(StartupEnabled));
                    AutoStartBot = true;
                    CheckUnsavedChanges();
                }
                else
                {
                    // 取消时强制刷新 UI（先设 true 再设 false 触发变更通知）
                    _startupEnabled = true;
                    this.RaisePropertyChanged(nameof(StartupEnabled));
                    _startupEnabled = false;
                    this.RaisePropertyChanged(nameof(StartupEnabled));
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "显示开机自启确认对话框失败");
        }
    }

    public bool StartupCommandEnabled
    {
        get => _startupCommandEnabled;
        set { this.RaiseAndSetIfChanged(ref _startupCommandEnabled, value); CheckUnsavedChanges(); }
    }

    public string StartupCommand
    {
        get => _startupCommand;
        set { this.RaiseAndSetIfChanged(ref _startupCommand, value); CheckUnsavedChanges(); }
    }

    private bool _logSaveEnabled = true;
    private int _logRetentionHours = 168;

    public bool LogSaveEnabled
    {
        get => _logSaveEnabled;
        set { this.RaiseAndSetIfChanged(ref _logSaveEnabled, value); CheckUnsavedChanges(); }
    }

    public int LogRetentionHours
    {
        get => _logRetentionHours;
        set { this.RaiseAndSetIfChanged(ref _logRetentionHours, value); CheckUnsavedChanges(); }
    }

    private bool _isSaving;
    public bool IsSaving
    {
        get => _isSaving;
        set => this.RaiseAndSetIfChanged(ref _isSaving, value);
    }

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

        BrowseQQCommand = ReactiveCommand.CreateFromTask(async () =>
        {
            var path = await BrowseFileAsync("选择 QQ 可执行文件", ["exe"], QQPath);
            if (!string.IsNullOrEmpty(path)) QQPath = path;
        });

        BrowsePmhqCommand = ReactiveCommand.CreateFromTask(async () =>
        {
            var path = await BrowseFileAsync("选择 PMHQ 可执行文件", ["exe"], PmhqPath);
            if (!string.IsNullOrEmpty(path)) PmhqPath = path;
        });

        BrowseLLBotCommand = ReactiveCommand.CreateFromTask(async () =>
        {
            var path = await BrowseFileAsync("选择 LLBot 脚本文件", ["js"], LLBotPath);
            if (!string.IsNullOrEmpty(path)) LLBotPath = path;
        });

        BrowseNodeCommand = ReactiveCommand.CreateFromTask(async () =>
        {
            var path = await BrowseFileAsync("选择 Node.js 可执行文件", ["exe"], NodePath);
            if (!string.IsNullOrEmpty(path)) NodePath = path;
        });

        TestCommandCommand = ReactiveCommand.Create(() =>
        {
            if (string.IsNullOrWhiteSpace(StartupCommand)) return;
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
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "执行命令失败");
            }
        });

        SaveConfigCommand = ReactiveCommand.CreateFromTask(SaveConfigAsync);
        LoadConfigCommand = ReactiveCommand.CreateFromTask(LoadConfigAsync);

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
                    new(title)
                    {
                        Patterns = extensions.Length > 0
                            ? Array.ConvertAll(extensions, ext => $"*.{ext}")
                            : ["*.*"]
                    }
                };

                string? suggestedStartLocation = null;
                if (!string.IsNullOrEmpty(currentPath))
                {
                    var fullPath = Path.IsPathRooted(currentPath) ? currentPath : Path.GetFullPath(currentPath);
                    if (File.Exists(fullPath))
                        suggestedStartLocation = Path.GetDirectoryName(fullPath);
                    else if (Directory.Exists(Path.GetDirectoryName(fullPath)))
                        suggestedStartLocation = Path.GetDirectoryName(fullPath);
                }
                suggestedStartLocation ??= Environment.CurrentDirectory;

                var options = new FilePickerOpenOptions
                {
                    Title = title,
                    AllowMultiple = false,
                    FileTypeFilter = filters
                };

                if (Directory.Exists(suggestedStartLocation))
                {
                    try
                    {
                        options.SuggestedStartLocation = await mainWindow.StorageProvider.TryGetFolderFromPathAsync(suggestedStartLocation);
                    }
                    catch { }
                }

                var result = await mainWindow.StorageProvider.OpenFilePickerAsync(options);
                if (result.Count > 0)
                {
                    var selectedPath = result[0].Path.LocalPath;
                    try
                    {
                        var currentDir = Environment.CurrentDirectory;
                        var relativePath = Path.GetRelativePath(currentDir, selectedPath);
                        if (relativePath.Length < selectedPath.Length && !relativePath.StartsWith(".."))
                            return relativePath;
                    }
                    catch { }
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

            QQPath = config.QQPath;
            PmhqPath = config.PmhqPath;
            LLBotPath = config.LLBotPath;
            NodePath = config.NodePath;

            if (string.IsNullOrEmpty(QQPath))
            {
                var detectedPath = Utils.QQPathHelper.GetQQPathFromRegistry();
                if (!string.IsNullOrEmpty(detectedPath))
                    QQPath = detectedPath;
            }

            AutoLoginQQ = config.AutoLoginQQ;
            AutoStartBot = config.AutoStartBot;
            Headless = config.Headless;
            MinimizeToTrayOnStart = config.MinimizeToTrayOnStart;
            _startupEnabled = Utils.StartupManager.IsStartupEnabled();
            this.RaisePropertyChanged(nameof(StartupEnabled));
            StartupCommandEnabled = config.StartupCommandEnabled;
            StartupCommand = config.StartupCommand;

            LogSaveEnabled = config.LogSaveEnabled;
            LogRetentionHours = config.LogRetentionSeconds / 3600;

            _savedConfig = new AppConfig
            {
                QQPath = QQPath,
                PmhqPath = PmhqPath,
                LLBotPath = LLBotPath,
                NodePath = NodePath,
                AutoLoginQQ = AutoLoginQQ,
                AutoStartBot = AutoStartBot,
                Headless = Headless,
                MinimizeToTrayOnStart = MinimizeToTrayOnStart,
                StartupCommandEnabled = StartupCommandEnabled,
                StartupCommand = StartupCommand,
                LogSaveEnabled = LogSaveEnabled,
                LogRetentionSeconds = LogRetentionHours * 3600
            };
            _savedStartupEnabled = _startupEnabled;
            HasUnsavedChanges = false;
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
            _logger.LogInformation("开始保存配置...");

            var config = new AppConfig
            {
                QQPath = QQPath,
                PmhqPath = PmhqPath,
                LLBotPath = LLBotPath,
                NodePath = NodePath,
                AutoLoginQQ = AutoLoginQQ,
                AutoStartBot = AutoStartBot,
                Headless = Headless,
                MinimizeToTrayOnStart = MinimizeToTrayOnStart,
                StartupCommandEnabled = StartupCommandEnabled,
                StartupCommand = StartupCommand,
                LogSaveEnabled = LogSaveEnabled,
                LogRetentionSeconds = LogRetentionHours * 3600
            };

            var success = await _configManager.SaveConfigAsync(config);

            if (success)
            {
                // 保存时处理开机自启注册表
                if (StartupEnabled != _savedStartupEnabled)
                {
                    if (StartupEnabled)
                        Utils.StartupManager.EnableStartup();
                    else
                        Utils.StartupManager.DisableStartup();
                }

                _savedConfig = config;
                _savedStartupEnabled = StartupEnabled;
                HasUnsavedChanges = false;
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
