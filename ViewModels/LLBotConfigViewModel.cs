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
    private readonly IResourceMonitor _resourceMonitor;
    private readonly ILogger<LLBotConfigViewModel> _logger;
    private readonly IDisposable _uinSubscription;

    private string? _currentUin;
    private LLBotConfig _config = LLBotConfig.Default;

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

    public bool WebuiEnable
    {
        get => _webuiEnable;
        set => this.RaiseAndSetIfChanged(ref _webuiEnable, value);
    }

    public int WebuiPort
    {
        get => _webuiPort;
        set => this.RaiseAndSetIfChanged(ref _webuiPort, value);
    }

    // OB11 配置
    private bool _ob11Enable = true;
    public bool Ob11Enable
    {
        get => _ob11Enable;
        set => this.RaiseAndSetIfChanged(ref _ob11Enable, value);
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

    public bool SatoriEnable
    {
        get => _satoriEnable;
        set => this.RaiseAndSetIfChanged(ref _satoriEnable, value);
    }

    public int SatoriPort
    {
        get => _satoriPort;
        set => this.RaiseAndSetIfChanged(ref _satoriPort, value);
    }

    public string SatoriToken
    {
        get => _satoriToken;
        set => this.RaiseAndSetIfChanged(ref _satoriToken, value);
    }

    // Milky 配置
    private bool _milkyEnable;
    private bool _milkyReportSelf;
    private int _milkyHttpPort = 3010;
    private string _milkyHttpPrefix = string.Empty;
    private string _milkyHttpToken = string.Empty;
    private string _milkyWebhookToken = string.Empty;

    public bool MilkyEnable
    {
        get => _milkyEnable;
        set => this.RaiseAndSetIfChanged(ref _milkyEnable, value);
    }

    public bool MilkyReportSelf
    {
        get => _milkyReportSelf;
        set => this.RaiseAndSetIfChanged(ref _milkyReportSelf, value);
    }

    public int MilkyHttpPort
    {
        get => _milkyHttpPort;
        set => this.RaiseAndSetIfChanged(ref _milkyHttpPort, value);
    }

    public string MilkyHttpPrefix
    {
        get => _milkyHttpPrefix;
        set => this.RaiseAndSetIfChanged(ref _milkyHttpPrefix, value);
    }

    public string MilkyHttpToken
    {
        get => _milkyHttpToken;
        set => this.RaiseAndSetIfChanged(ref _milkyHttpToken, value);
    }

    public string MilkyWebhookToken
    {
        get => _milkyWebhookToken;
        set => this.RaiseAndSetIfChanged(ref _milkyWebhookToken, value);
    }

    public ObservableCollection<string> MilkyWebhookUrls { get; } = new();

    // 其他配置
    private bool _enableLocalFile2Url;
    private bool _logEnable = true;
    private bool _autoDeleteFile;
    private int _autoDeleteFileSecond = 60;
    private string _musicSignUrl = "https://llob.linyuchen.net/sign/music";
    private int _msgCacheExpire = 120;
    private bool _onlyLocalhost = true;
    private string _ffmpegPath = string.Empty;

    public bool EnableLocalFile2Url
    {
        get => _enableLocalFile2Url;
        set => this.RaiseAndSetIfChanged(ref _enableLocalFile2Url, value);
    }

    public bool LogEnable
    {
        get => _logEnable;
        set => this.RaiseAndSetIfChanged(ref _logEnable, value);
    }

    public bool AutoDeleteFile
    {
        get => _autoDeleteFile;
        set => this.RaiseAndSetIfChanged(ref _autoDeleteFile, value);
    }

    public int AutoDeleteFileSecond
    {
        get => _autoDeleteFileSecond;
        set => this.RaiseAndSetIfChanged(ref _autoDeleteFileSecond, value);
    }

    public string MusicSignUrl
    {
        get => _musicSignUrl;
        set => this.RaiseAndSetIfChanged(ref _musicSignUrl, value);
    }

    public int MsgCacheExpire
    {
        get => _msgCacheExpire;
        set => this.RaiseAndSetIfChanged(ref _msgCacheExpire, value);
    }

    public bool OnlyLocalhost
    {
        get => _onlyLocalhost;
        set => this.RaiseAndSetIfChanged(ref _onlyLocalhost, value);
    }

    public string FfmpegPath
    {
        get => _ffmpegPath;
        set => this.RaiseAndSetIfChanged(ref _ffmpegPath, value);
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
        IResourceMonitor resourceMonitor,
        ILogger<LLBotConfigViewModel> logger)
    {
        _pmhqClient = pmhqClient;
        _resourceMonitor = resourceMonitor;
        _logger = logger;

        SaveConfigCommand = ReactiveCommand.CreateFromTask(SaveConfigAsync);
        RefreshCommand = ReactiveCommand.CreateFromTask(RefreshAsync);
        OpenWebuiCommand = ReactiveCommand.Create(OpenWebui);
        AddConnectionCommand = ReactiveCommand.Create<string>(AddConnection);
        RemoveConnectionCommand = ReactiveCommand.Create<OB11ConnectionViewModel>(RemoveConnection);
        AddWebhookUrlCommand = ReactiveCommand.Create(AddWebhookUrl);
        RemoveWebhookUrlCommand = ReactiveCommand.Create<string>(RemoveWebhookUrl);

        // 订阅 UIN 更新
        _uinSubscription = _resourceMonitor.UinStream
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(OnUinReceived);

        // 初始化时尝试加载配置
        _ = RefreshAsync();
    }

    private async void OnUinReceived(SelfInfo selfInfo)
    {
        if (!string.IsNullOrEmpty(selfInfo.Uin))
        {
            _currentUin = selfInfo.Uin;
            HasUin = true;
            await LoadConfigAsync();
        }
    }

    private string? GetConfigPath()
    {
        if (string.IsNullOrEmpty(_currentUin))
            return null;

        return Path.Combine("bin", "llbot", "data", $"config_{_currentUin}.json");
    }

    public async Task RefreshAsync()
    {
        try
        {
            // 尝试获取 UIN
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
        // WebUI
        WebuiEnable = _config.WebUI.Enable;
        WebuiPort = _config.WebUI.Port;

        // OB11
        Ob11Enable = _config.OB11.Enable;
        Ob11Connections.Clear();
        foreach (var conn in _config.OB11.Connect)
        {
            Ob11Connections.Add(new OB11ConnectionViewModel(conn));
        }

        // Satori
        SatoriEnable = _config.Satori.Enable;
        SatoriPort = _config.Satori.Port;
        SatoriToken = _config.Satori.Token;

        // Milky
        MilkyEnable = _config.Milky.Enable;
        MilkyReportSelf = _config.Milky.ReportSelfMessage;
        MilkyHttpPort = _config.Milky.Http.Port;
        MilkyHttpPrefix = _config.Milky.Http.Prefix;
        MilkyHttpToken = _config.Milky.Http.AccessToken;
        MilkyWebhookToken = _config.Milky.Webhook.AccessToken;
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
        OnlyLocalhost = _config.OnlyLocalhost;
        FfmpegPath = _config.Ffmpeg;
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

            // 更新配置对象
            _config.WebUI.Enable = WebuiEnable;
            _config.WebUI.Port = WebuiPort;

            _config.OB11.Enable = Ob11Enable;
            _config.OB11.Connect.Clear();
            foreach (var connVm in Ob11Connections)
            {
                _config.OB11.Connect.Add(connVm.ToModel());
            }

            _config.Satori.Enable = SatoriEnable;
            _config.Satori.Port = SatoriPort;
            _config.Satori.Token = SatoriToken;

            _config.Milky.Enable = MilkyEnable;
            _config.Milky.ReportSelfMessage = MilkyReportSelf;
            _config.Milky.Http.Port = MilkyHttpPort;
            _config.Milky.Http.Prefix = MilkyHttpPrefix;
            _config.Milky.Http.AccessToken = MilkyHttpToken;
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
            _config.OnlyLocalhost = OnlyLocalhost;
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
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "打开 WebUI 失败");
        }
    }

    private void AddConnection(string type)
    {
        var conn = new OB11Connection
        {
            Type = type,
            Enable = false,
            Port = type == "ws" ? 3001 : type == "http" ? 3000 : 0,
            HeartInterval = 60000,
            MessageFormat = "array"
        };
        Ob11Connections.Add(new OB11ConnectionViewModel(conn));
        SelectedConnectionIndex = Ob11Connections.Count - 1;
    }

    private void RemoveConnection(OB11ConnectionViewModel conn)
    {
        Ob11Connections.Remove(conn);
    }

    private void AddWebhookUrl()
    {
        MilkyWebhookUrls.Add(string.Empty);
    }

    private void RemoveWebhookUrl(string url)
    {
        MilkyWebhookUrls.Remove(url);
    }

    public void Dispose()
    {
        _uinSubscription.Dispose();
    }
}

public class OB11ConnectionViewModel : ViewModelBase
{
    private string _type;
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

    public string Type
    {
        get => _type;
        set => this.RaiseAndSetIfChanged(ref _type, value);
    }

    public string TypeName => Type switch
    {
        "ws" => "WebSocket",
        "ws-reverse" => "反向WS",
        "http" => "HTTP",
        "http-post" => "HTTP POST",
        _ => Type
    };

    public bool Enable
    {
        get => _enable;
        set => this.RaiseAndSetIfChanged(ref _enable, value);
    }

    public int Port
    {
        get => _port;
        set => this.RaiseAndSetIfChanged(ref _port, value);
    }

    public string Url
    {
        get => _url;
        set => this.RaiseAndSetIfChanged(ref _url, value);
    }

    public int HeartInterval
    {
        get => _heartInterval;
        set => this.RaiseAndSetIfChanged(ref _heartInterval, value);
    }

    public bool EnableHeart
    {
        get => _enableHeart;
        set => this.RaiseAndSetIfChanged(ref _enableHeart, value);
    }

    public string Token
    {
        get => _token;
        set => this.RaiseAndSetIfChanged(ref _token, value);
    }

    public bool ReportSelfMessage
    {
        get => _reportSelfMessage;
        set => this.RaiseAndSetIfChanged(ref _reportSelfMessage, value);
    }

    public bool ReportOfflineMessage
    {
        get => _reportOfflineMessage;
        set => this.RaiseAndSetIfChanged(ref _reportOfflineMessage, value);
    }

    public string MessageFormat
    {
        get => _messageFormat;
        set => this.RaiseAndSetIfChanged(ref _messageFormat, value);
    }

    public bool Debug
    {
        get => _debug;
        set => this.RaiseAndSetIfChanged(ref _debug, value);
    }

    // 辅助属性 - 是否显示端口
    public bool HasPort => Type is "ws" or "http";
    // 辅助属性 - 是否显示 URL
    public bool HasUrl => Type is "ws-reverse" or "http-post";
    // 辅助属性 - 是否显示心跳
    public bool HasHeart => Type != "http";
    // 辅助属性 - 是否显示启用心跳
    public bool HasEnableHeart => Type == "http-post";

    public OB11ConnectionViewModel(OB11Connection model)
    {
        _type = model.Type;
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
    }

    public OB11Connection ToModel() => new()
    {
        Type = Type,
        Enable = Enable,
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
}
