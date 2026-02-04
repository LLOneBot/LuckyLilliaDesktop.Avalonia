using LuckyLilliaDesktop.Models;
using LuckyLilliaDesktop.Services;
using Microsoft.Extensions.Logging;
using ReactiveUI;
using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Reactive;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace LuckyLilliaDesktop.ViewModels;

public class LLBotConfigViewModel : ViewModelBase, IDisposable
{
    private readonly IPmhqClient _pmhqClient;
    private readonly ISelfInfoService _selfInfoService;
    private readonly IProcessManager _processManager;
    private readonly ILogger<LLBotConfigViewModel> _logger;
    private readonly IDisposable _uinSubscription;

    private string? _currentUin;
    private LLBotConfig _config = LLBotConfig.Default;
    private bool _isLoading;

    public Func<string, string, Task>? ShowAlertDialog { get; set; }

    // 未保存更改标志
    private bool _hasUnsavedChanges;
    public bool HasUnsavedChanges
    {
        get => _hasUnsavedChanges;
        private set => this.RaiseAndSetIfChanged(ref _hasUnsavedChanges, value);
    }

    private void MarkAsModified()
    {
        if (!_isLoading) HasUnsavedChanges = true;
    }

    // 是否有 UIN（用于显示/隐藏配置界面）
    private bool _hasUin;
    public bool HasUin
    {
        get => _hasUin;
        set => this.RaiseAndSetIfChanged(ref _hasUin, value);
    }

    // WebUI 配置
    private bool _webuiEnable = true;
    private int _webuiPort = 3080;
    private int _webuiHostMode; // 0=全部, 1=本地, 2=自定义
    private string _webuiCustomHost = string.Empty;
    private string _webuiPassword = string.Empty;

    public bool WebuiEnable
    {
        get => _webuiEnable;
        set { this.RaiseAndSetIfChanged(ref _webuiEnable, value); MarkAsModified(); }
    }

    public int WebuiPort
    {
        get => _webuiPort;
        set { this.RaiseAndSetIfChanged(ref _webuiPort, value); MarkAsModified(); }
    }

    public int WebuiHostMode
    {
        get => _webuiHostMode;
        set { this.RaiseAndSetIfChanged(ref _webuiHostMode, value); MarkAsModified(); }
    }

    public string WebuiCustomHost
    {
        get => _webuiCustomHost;
        set { this.RaiseAndSetIfChanged(ref _webuiCustomHost, value); MarkAsModified(); }
    }

    public string WebuiPassword
    {
        get => _webuiPassword;
        set { this.RaiseAndSetIfChanged(ref _webuiPassword, value); MarkAsModified(); }
    }

    // OB11 配置
    private bool _ob11Enable = true;
    public bool Ob11Enable
    {
        get => _ob11Enable;
        set { this.RaiseAndSetIfChanged(ref _ob11Enable, value); MarkAsModified(); }
    }

    private int _selectedConnectionIndex;
    public int SelectedConnectionIndex
    {
        get => _selectedConnectionIndex;
        set => this.RaiseAndSetIfChanged(ref _selectedConnectionIndex, value);
    }

    public ObservableCollection<OB11ConnectionViewModel> Ob11Connections { get; } = new();

    // Satori 配置
    private bool _satoriEnable;
    private int _satoriPort = 5600;
    private string _satoriToken = string.Empty;
    private int _satoriHostMode;
    private string _satoriCustomHost = string.Empty;

    public bool SatoriEnable
    {
        get => _satoriEnable;
        set { this.RaiseAndSetIfChanged(ref _satoriEnable, value); MarkAsModified(); }
    }

    public int SatoriPort
    {
        get => _satoriPort;
        set { this.RaiseAndSetIfChanged(ref _satoriPort, value); MarkAsModified(); }
    }

    public string SatoriToken
    {
        get => _satoriToken;
        set { this.RaiseAndSetIfChanged(ref _satoriToken, value); MarkAsModified(); }
    }

    public int SatoriHostMode
    {
        get => _satoriHostMode;
        set { this.RaiseAndSetIfChanged(ref _satoriHostMode, value); MarkAsModified(); }
    }

    public string SatoriCustomHost
    {
        get => _satoriCustomHost;
        set { this.RaiseAndSetIfChanged(ref _satoriCustomHost, value); MarkAsModified(); }
    }

    // Milky 配置
    private bool _milkyEnable;
    private bool _milkyReportSelf;
    private int _milkyHttpPort = 3010;
    private string _milkyHttpPrefix = string.Empty;
    private string _milkyHttpToken = string.Empty;
    private string _milkyWebhookToken = string.Empty;
    private int _milkyHostMode;
    private string _milkyCustomHost = string.Empty;

    public bool MilkyEnable
    {
        get => _milkyEnable;
        set { this.RaiseAndSetIfChanged(ref _milkyEnable, value); MarkAsModified(); }
    }

    public bool MilkyReportSelf
    {
        get => _milkyReportSelf;
        set { this.RaiseAndSetIfChanged(ref _milkyReportSelf, value); MarkAsModified(); }
    }

    public int MilkyHttpPort
    {
        get => _milkyHttpPort;
        set { this.RaiseAndSetIfChanged(ref _milkyHttpPort, value); MarkAsModified(); }
    }

    public string MilkyHttpPrefix
    {
        get => _milkyHttpPrefix;
        set { this.RaiseAndSetIfChanged(ref _milkyHttpPrefix, value); MarkAsModified(); }
    }

    public string MilkyHttpToken
    {
        get => _milkyHttpToken;
        set { this.RaiseAndSetIfChanged(ref _milkyHttpToken, value); MarkAsModified(); }
    }

    public string MilkyWebhookToken
    {
        get => _milkyWebhookToken;
        set { this.RaiseAndSetIfChanged(ref _milkyWebhookToken, value); MarkAsModified(); }
    }

    public int MilkyHostMode
    {
        get => _milkyHostMode;
        set { this.RaiseAndSetIfChanged(ref _milkyHostMode, value); MarkAsModified(); }
    }

    public string MilkyCustomHost
    {
        get => _milkyCustomHost;
        set { this.RaiseAndSetIfChanged(ref _milkyCustomHost, value); MarkAsModified(); }
    }

    public ObservableCollection<string> MilkyWebhookUrls { get; } = new();

    // 其他配置
    private bool _enableLocalFile2Url;
    private bool _logEnable = true;
    private bool _autoDeleteFile;
    private int _autoDeleteFileSecond = 60;
    private string _musicSignUrl = "https://llob.linyuchen.net/sign/music";
    private int _msgCacheExpire = 120;
    private string _ffmpegPath = string.Empty;

    public bool EnableLocalFile2Url
    {
        get => _enableLocalFile2Url;
        set { this.RaiseAndSetIfChanged(ref _enableLocalFile2Url, value); MarkAsModified(); }
    }

    public bool LogEnable
    {
        get => _logEnable;
        set { this.RaiseAndSetIfChanged(ref _logEnable, value); MarkAsModified(); }
    }

    public bool AutoDeleteFile
    {
        get => _autoDeleteFile;
        set { this.RaiseAndSetIfChanged(ref _autoDeleteFile, value); MarkAsModified(); }
    }

    public int AutoDeleteFileSecond
    {
        get => _autoDeleteFileSecond;
        set { this.RaiseAndSetIfChanged(ref _autoDeleteFileSecond, value); MarkAsModified(); }
    }

    public string MusicSignUrl
    {
        get => _musicSignUrl;
        set { this.RaiseAndSetIfChanged(ref _musicSignUrl, value); MarkAsModified(); }
    }

    public int MsgCacheExpire
    {
        get => _msgCacheExpire;
        set { this.RaiseAndSetIfChanged(ref _msgCacheExpire, value); MarkAsModified(); }
    }

    public string FfmpegPath
    {
        get => _ffmpegPath;
        set { this.RaiseAndSetIfChanged(ref _ffmpegPath, value); MarkAsModified(); }
    }

    // host 字符串转 mode+customHost
    private static (int mode, string customHost) HostToMode(string host)
    {
        return host switch
        {
            "" => (0, ""),
            "127.0.0.1" => (1, ""),
            _ => (2, host)
        };
    }

    // mode+customHost 转 host 字符串
    private static string ModeToHost(int mode, string customHost)
    {
        return mode switch
        {
            0 => "",
            1 => "127.0.0.1",
            _ => customHost
        };
    }

    // 命令
    public ReactiveCommand<Unit, Unit> SaveConfigCommand { get; }
    public ReactiveCommand<Unit, Unit> RefreshCommand { get; }
    public ReactiveCommand<Unit, Unit> OpenWebuiCommand { get; }
    public ReactiveCommand<string, Unit> AddConnectionCommand { get; }
    public ReactiveCommand<OB11ConnectionViewModel, Unit> RemoveConnectionCommand { get; }
    public ReactiveCommand<Unit, Unit> AddWebhookUrlCommand { get; }
    public ReactiveCommand<string, Unit> RemoveWebhookUrlCommand { get; }

    public LLBotConfigViewModel(
        IPmhqClient pmhqClient,
        ISelfInfoService selfInfoService,
        IProcessManager processManager,
        ILogger<LLBotConfigViewModel> logger)
    {
        _pmhqClient = pmhqClient;
        _selfInfoService = selfInfoService;
        _processManager = processManager;
        _logger = logger;

        SaveConfigCommand = ReactiveCommand.CreateFromTask(SaveConfigAsync);
        RefreshCommand = ReactiveCommand.CreateFromTask(RefreshAsync);
        OpenWebuiCommand = ReactiveCommand.Create(OpenWebui);
        AddConnectionCommand = ReactiveCommand.Create<string>(AddConnection);
        RemoveConnectionCommand = ReactiveCommand.Create<OB11ConnectionViewModel>(RemoveConnection);
        AddWebhookUrlCommand = ReactiveCommand.Create(AddWebhookUrl);
        RemoveWebhookUrlCommand = ReactiveCommand.Create<string>(RemoveWebhookUrl);

        _uinSubscription = _selfInfoService.UinStream
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(OnUinReceived);

        _processManager.ProcessStatusChanged += OnProcessStatusChanged;
    }

    private void OnProcessStatusChanged(object? sender, ProcessStatus status)
    {
        var qqStatus = _processManager.GetProcessStatus("QQ");
        if (qqStatus != ProcessStatus.Running)
        {
            _currentUin = null;
            HasUin = false;
        }
    }

    private async void OnUinReceived(string uin)
    {
        if (string.IsNullOrEmpty(uin))
        {
            _currentUin = null;
            HasUin = false;
            return;
        }
        _currentUin = uin;
        HasUin = true;
        await LoadConfigAsync();
    }

    private string? GetConfigPath()
    {
        if (string.IsNullOrEmpty(_currentUin))
            return null;

        return Path.Combine("bin", "llbot", "data", $"config_{_currentUin}.json");
    }

    private string GetWebuiTokenPath()
    {
        return Path.Combine("bin", "llbot", "data", "webui_token.txt");
    }

    private async Task<string> LoadWebuiTokenAsync()
    {
        try
        {
            var tokenPath = GetWebuiTokenPath();
            if (File.Exists(tokenPath))
            {
                return await File.ReadAllTextAsync(tokenPath);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "加载 WebUI Token 失败");
        }
        return string.Empty;
    }

    private async Task SaveWebuiTokenAsync(string token)
    {
        try
        {
            var tokenPath = GetWebuiTokenPath();
            var dir = Path.GetDirectoryName(tokenPath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            await File.WriteAllTextAsync(tokenPath, token);
            _logger.LogInformation("WebUI Token 已保存: {Path}", tokenPath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "保存 WebUI Token 失败");
        }
    }

    public async Task RefreshAsync()
    {
        try
        {
            // 直接调用 API 获取 UIN，不依赖 ResourceMonitor 的缓存
            var selfInfo = await _pmhqClient.FetchSelfInfoAsync();
            if (selfInfo != null && !string.IsNullOrEmpty(selfInfo.Uin))
            {
                _currentUin = selfInfo.Uin;
                HasUin = true;
                await LoadConfigAsync();
            }
            else
            {
                HasUin = false;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "获取 UIN 失败，可能 PMHQ 未启动");
            HasUin = false;
        }
    }

    public async Task OnPageEnterAsync()
    {
        await RefreshAsync();
    }

    private async Task LoadConfigAsync()
    {
        try
        {
            var configPath = GetConfigPath();
            if (string.IsNullOrEmpty(configPath) || !File.Exists(configPath))
            {
                _config = LLBotConfig.Default;
                UpdateUIFromConfig();
                return;
            }

            var json = await File.ReadAllTextAsync(configPath);
            _config = JsonSerializer.Deserialize<LLBotConfig>(json) ?? LLBotConfig.Default;
            
            // 加载 WebUI token
            WebuiPassword = await LoadWebuiTokenAsync();
            
            UpdateUIFromConfig();
            _logger.LogInformation("LLBot 配置已加载: {Path}", configPath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "加载 LLBot 配置失败");
            _config = LLBotConfig.Default;
            UpdateUIFromConfig();
        }
    }

    private void UpdateUIFromConfig()
    {
        _isLoading = true;
        try
        {
            // WebUI
            WebuiEnable = _config.WebUI.Enable;
            WebuiPort = _config.WebUI.Port;
            var (webuiMode, webuiCustom) = HostToMode(_config.WebUI.Host);
            WebuiHostMode = webuiMode;
            WebuiCustomHost = webuiCustom;

            // OB11
            Ob11Enable = _config.OB11.Enable;
            Ob11Connections.Clear();
            foreach (var conn in _config.OB11.Connect)
            {
                var vm = new OB11ConnectionViewModel(conn);
                vm.PropertyModified += MarkAsModified;
                Ob11Connections.Add(vm);
            }

            // Satori
            SatoriEnable = _config.Satori.Enable;
            SatoriPort = _config.Satori.Port;
            SatoriToken = _config.Satori.Token;
            var (satoriMode, satoriCustom) = HostToMode(_config.Satori.Host);
            SatoriHostMode = satoriMode;
            SatoriCustomHost = satoriCustom;

            // Milky
            MilkyEnable = _config.Milky.Enable;
            MilkyReportSelf = _config.Milky.ReportSelfMessage;
            MilkyHttpPort = _config.Milky.Http.Port;
            MilkyHttpPrefix = _config.Milky.Http.Prefix;
            MilkyHttpToken = _config.Milky.Http.AccessToken;
            MilkyWebhookToken = _config.Milky.Webhook.AccessToken;
            var (milkyMode, milkyCustom) = HostToMode(_config.Milky.Http.Host);
            MilkyHostMode = milkyMode;
            MilkyCustomHost = milkyCustom;
            MilkyWebhookUrls.Clear();
            foreach (var url in _config.Milky.Webhook.Urls)
            {
                MilkyWebhookUrls.Add(url);
            }

            // 其他
            EnableLocalFile2Url = _config.EnableLocalFile2Url;
            LogEnable = _config.Log;
            AutoDeleteFile = _config.AutoDeleteFile;
            AutoDeleteFileSecond = _config.AutoDeleteFileSecond;
            MusicSignUrl = _config.MusicSignUrl;
            MsgCacheExpire = _config.MsgCacheExpire;
            FfmpegPath = _config.Ffmpeg;
        }
        finally
        {
            _isLoading = false;
            HasUnsavedChanges = false;
        }
    }

    private async Task SaveConfigAsync()
    {
        try
        {
            var configPath = GetConfigPath();
            if (string.IsNullOrEmpty(configPath))
            {
                _logger.LogWarning("无法保存配置：未获取到 UIN");
                return;
            }

            // 验证：监听地址为"全部"时必须设置密码/token
            if (WebuiEnable && WebuiHostMode == 0 && string.IsNullOrWhiteSpace(WebuiPassword))
            {
                if (ShowAlertDialog != null)
                    await ShowAlertDialog("保存失败", "WebUI 监听全部地址时必须设置密码");
                return;
            }

            if (SatoriEnable && SatoriHostMode == 0 && string.IsNullOrWhiteSpace(SatoriToken))
            {
                if (ShowAlertDialog != null)
                    await ShowAlertDialog("保存失败", "Satori 监听全部地址时必须设置 Token");
                return;
            }

            if (MilkyEnable && MilkyHostMode == 0 && string.IsNullOrWhiteSpace(MilkyHttpToken))
            {
                if (ShowAlertDialog != null)
                    await ShowAlertDialog("保存失败", "Milky 监听全部地址时必须设置 AccessToken");
                return;
            }

            // 检查 OB11 连接的 token
            foreach (var conn in Ob11Connections)
            {
                if (conn.Enable && conn.HasPort && conn.HostMode == 0 && string.IsNullOrWhiteSpace(conn.Token))
                {
                    if (ShowAlertDialog != null)
                        await ShowAlertDialog("保存失败", $"OneBot11 {conn.TypeName} 监听全部地址时必须设置 Token");
                    return;
                }
            }

            _logger.LogInformation("保存 LLBot 配置: {Path}", configPath);

            // 更新配置对象
            _config.WebUI.Enable = WebuiEnable;
            _config.WebUI.Port = WebuiPort;
            _config.WebUI.Host = ModeToHost(WebuiHostMode, WebuiCustomHost);

            // 单独保存 WebUI token
            await SaveWebuiTokenAsync(WebuiPassword);

            _config.OB11.Enable = Ob11Enable;
            _config.OB11.Connect.Clear();
            foreach (var connVm in Ob11Connections)
            {
                _config.OB11.Connect.Add(connVm.ToModel());
            }

            _config.Satori.Enable = SatoriEnable;
            _config.Satori.Port = SatoriPort;
            _config.Satori.Token = SatoriToken;
            _config.Satori.Host = ModeToHost(SatoriHostMode, SatoriCustomHost);

            _config.Milky.Enable = MilkyEnable;
            _config.Milky.ReportSelfMessage = MilkyReportSelf;
            _config.Milky.Http.Port = MilkyHttpPort;
            _config.Milky.Http.Prefix = MilkyHttpPrefix;
            _config.Milky.Http.AccessToken = MilkyHttpToken;
            _config.Milky.Http.Host = ModeToHost(MilkyHostMode, MilkyCustomHost);
            _config.Milky.Webhook.AccessToken = MilkyWebhookToken;
            _config.Milky.Webhook.Urls.Clear();
            foreach (var url in MilkyWebhookUrls)
            {
                if (!string.IsNullOrWhiteSpace(url))
                    _config.Milky.Webhook.Urls.Add(url);
            }

            _config.EnableLocalFile2Url = EnableLocalFile2Url;
            _config.Log = LogEnable;
            _config.AutoDeleteFile = AutoDeleteFile;
            _config.AutoDeleteFileSecond = AutoDeleteFileSecond;
            _config.MusicSignUrl = MusicSignUrl;
            _config.MsgCacheExpire = MsgCacheExpire;
            _config.Ffmpeg = FfmpegPath;

            // 确保目录存在
            var dir = Path.GetDirectoryName(configPath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            // 写入文件
            var json = JsonSerializer.Serialize(_config, new JsonSerializerOptions
            {
                WriteIndented = true
            });
            await File.WriteAllTextAsync(configPath, json);

            HasUnsavedChanges = false;
            _logger.LogInformation("LLBot 配置已保存: {Path}", configPath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "保存 LLBot 配置失败");
        }
    }

    private void OpenWebui()
    {
        try
        {
            var url = $"http://localhost:{WebuiPort}";
            _logger.LogInformation("打开 WebUI: {Url}", url);
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "打开 WebUI 失败");
        }
    }

    private void AddConnection(string type)
    {
        _logger.LogInformation("添加 OB11 连接: {Type}", type);
        var conn = new OB11Connection
        {
            Type = type,
            Enable = false,
            Host = "127.0.0.1", // 默认本地
            Port = type == "ws" ? 3001 : type == "http" ? 3000 : 0,
            HeartInterval = 60000,
            MessageFormat = "array"
        };
        var vm = new OB11ConnectionViewModel(conn);
        vm.PropertyModified += MarkAsModified;
        Ob11Connections.Add(vm);
        SelectedConnectionIndex = Ob11Connections.Count - 1;
        HasUnsavedChanges = true;
    }

    private void RemoveConnection(OB11ConnectionViewModel conn)
    {
        _logger.LogInformation("移除 OB11 连接: {Type}", conn.Type);
        conn.PropertyModified -= MarkAsModified;
        Ob11Connections.Remove(conn);
        HasUnsavedChanges = true;
    }

    private void AddWebhookUrl()
    {
        MilkyWebhookUrls.Add(string.Empty);
        HasUnsavedChanges = true;
    }

    private void RemoveWebhookUrl(string url)
    {
        MilkyWebhookUrls.Remove(url);
        HasUnsavedChanges = true;
    }

    public void Dispose()
    {
        _uinSubscription.Dispose();
        _processManager.ProcessStatusChanged -= OnProcessStatusChanged;
    }
}

public class OB11ConnectionViewModel : ViewModelBase
{
    public event Action? PropertyModified;

    private void NotifyModified()
    {
        PropertyModified?.Invoke();
    }

    private string _type;
    private string? _name;
    private bool _enable;
    private int _port;
    private string _url = string.Empty;
    private int _heartInterval = 60000;
    private bool _enableHeart;
    private string _token = string.Empty;
    private bool _reportSelfMessage;
    private bool _reportOfflineMessage;
    private string _messageFormat = "array";
    private bool _debug;
    private int _hostMode = 1; // 默认本地
    private string _customHost = string.Empty;

    public string Type
    {
        get => _type;
        set => this.RaiseAndSetIfChanged(ref _type, value);
    }

    public string? Name
    {
        get => _name;
        set 
        { 
            this.RaiseAndSetIfChanged(ref _name, value); 
            this.RaisePropertyChanged(nameof(TypeName));
            NotifyModified(); 
        }
    }

    public string TypeName
    {
        get
        {
            var baseName = Type switch
            {
                "ws" => "WebSocket 服务端(正向)",
                "ws-reverse" => "WebSocket 客户端(反向)",
                "http" => "HTTP 服务端",
                "http-post" => "WebHook",
                _ => Type
            };
            
            return string.IsNullOrWhiteSpace(Name) ? baseName : $"{baseName} ({Name})";
        }
    }

    public bool Enable
    {
        get => _enable;
        set { this.RaiseAndSetIfChanged(ref _enable, value); NotifyModified(); }
    }

    public int Port
    {
        get => _port;
        set { this.RaiseAndSetIfChanged(ref _port, value); NotifyModified(); }
    }

    public string Url
    {
        get => _url;
        set { this.RaiseAndSetIfChanged(ref _url, value); NotifyModified(); }
    }

    public int HeartInterval
    {
        get => _heartInterval;
        set { this.RaiseAndSetIfChanged(ref _heartInterval, value); NotifyModified(); }
    }

    public bool EnableHeart
    {
        get => _enableHeart;
        set { this.RaiseAndSetIfChanged(ref _enableHeart, value); NotifyModified(); }
    }

    public string Token
    {
        get => _token;
        set { this.RaiseAndSetIfChanged(ref _token, value); NotifyModified(); }
    }

    public bool ReportSelfMessage
    {
        get => _reportSelfMessage;
        set { this.RaiseAndSetIfChanged(ref _reportSelfMessage, value); NotifyModified(); }
    }

    public bool ReportOfflineMessage
    {
        get => _reportOfflineMessage;
        set { this.RaiseAndSetIfChanged(ref _reportOfflineMessage, value); NotifyModified(); }
    }

    public string MessageFormat
    {
        get => _messageFormat;
        set { this.RaiseAndSetIfChanged(ref _messageFormat, value); NotifyModified(); }
    }

    public int MessageFormatIndex
    {
        get => _messageFormat == "string" ? 1 : 0;
        set => MessageFormat = value == 1 ? "string" : "array";
    }

    public bool Debug
    {
        get => _debug;
        set { this.RaiseAndSetIfChanged(ref _debug, value); NotifyModified(); }
    }

    public int HostMode
    {
        get => _hostMode;
        set { this.RaiseAndSetIfChanged(ref _hostMode, value); NotifyModified(); }
    }

    public string CustomHost
    {
        get => _customHost;
        set { this.RaiseAndSetIfChanged(ref _customHost, value); NotifyModified(); }
    }

    // 辅助属性 - 是否显示端口
    public bool HasPort => Type is "ws" or "http";
    // 辅助属性 - 是否显示 URL
    public bool HasUrl => Type is "ws-reverse" or "http-post";
    // 辅助属性 - 是否显示心跳
    public bool HasHeart => Type != "http";
    // 辅助属性 - 是否显示启用心跳
    public bool HasEnableHeart => Type == "http-post";
    // 辅助属性 - URL 占位符
    public string UrlPlaceholder => Type switch
    {
        "ws-reverse" => "ws://127.0.0.1:8080/onebot/v11/ws",
        "http-post" => "http://127.0.0.1:5700",
        _ => ""
    };

    public OB11ConnectionViewModel(OB11Connection model)
    {
        _type = model.Type;
        _name = model.Name;
        _enable = model.Enable;
        _port = model.Port;
        _url = model.Url;
        _heartInterval = model.HeartInterval;
        _enableHeart = model.EnableHeart;
        _token = model.Token;
        _reportSelfMessage = model.ReportSelfMessage;
        _reportOfflineMessage = model.ReportOfflineMessage;
        _messageFormat = model.MessageFormat;
        _debug = model.Debug;
        
        // host 字符串转 mode+customHost
        var (mode, customHost) = HostToMode(model.Host);
        _hostMode = mode;
        _customHost = customHost;
    }

    public OB11Connection ToModel() => new()
    {
        Type = Type,
        Name = Name,
        Enable = Enable,
        Host = ModeToHost(HostMode, CustomHost),
        Port = Port,
        Url = Url,
        HeartInterval = HeartInterval,
        EnableHeart = EnableHeart,
        Token = Token,
        ReportSelfMessage = ReportSelfMessage,
        ReportOfflineMessage = ReportOfflineMessage,
        MessageFormat = MessageFormat,
        Debug = Debug
    };

    // host 字符串转 mode+customHost
    private static (int mode, string customHost) HostToMode(string host)
    {
        return host switch
        {
            "" => (0, ""),
            "127.0.0.1" => (1, ""),
            _ => (2, host)
        };
    }

    // mode+customHost 转 host 字符串
    private static string ModeToHost(int mode, string customHost)
    {
        return mode switch
        {
            0 => "",
            1 => "127.0.0.1",
            _ => customHost
        };
    }
}
