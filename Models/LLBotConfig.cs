using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace LuckyLilliaDesktop.Models;

/// <summary>
/// LLBot 配置 - 与 Python 版本的 llbot_config_page.py 保持一致
/// 对应配置文件: bin/llbot/data/config_{uin}.json
/// </summary>
public class LLBotConfig
{
    [JsonPropertyName("webui")]
    public WebUIConfig WebUI { get; set; } = new();

    [JsonPropertyName("satori")]
    public SatoriConfig Satori { get; set; } = new();

    [JsonPropertyName("ob11")]
    public OB11Config OB11 { get; set; } = new();

    [JsonPropertyName("milky")]
    public MilkyConfig Milky { get; set; } = new();

    [JsonPropertyName("enableLocalFile2Url")]
    public bool EnableLocalFile2Url { get; set; } = false;

    [JsonPropertyName("log")]
    public bool Log { get; set; } = true;

    [JsonPropertyName("autoDeleteFile")]
    public bool AutoDeleteFile { get; set; } = false;

    [JsonPropertyName("autoDeleteFileSecond")]
    public int AutoDeleteFileSecond { get; set; } = 60;

    [JsonPropertyName("musicSignUrl")]
    public string MusicSignUrl { get; set; } = "https://llob.linyuchen.net/sign/music";

    [JsonPropertyName("msgCacheExpire")]
    public int MsgCacheExpire { get; set; } = 120;

    [JsonPropertyName("ffmpeg")]
    public string Ffmpeg { get; set; } = string.Empty;

    public static LLBotConfig Default => new();
}

public class WebUIConfig
{
    [JsonPropertyName("enable")]
    public bool Enable { get; set; } = true;

    [JsonPropertyName("host")]
    public string Host { get; set; } = "127.0.0.1";

    [JsonPropertyName("port")]
    public int Port { get; set; } = 3080;
}

public class SatoriConfig
{
    [JsonPropertyName("enable")]
    public bool Enable { get; set; } = false;

    [JsonPropertyName("host")]
    public string Host { get; set; } = string.Empty;

    [JsonPropertyName("port")]
    public int Port { get; set; } = 5600;

    [JsonPropertyName("token")]
    public string Token { get; set; } = string.Empty;
}

public class OB11Config
{
    [JsonPropertyName("enable")]
    public bool Enable { get; set; } = true;

    [JsonPropertyName("connect")]
    public List<OB11Connection> Connect { get; set; } = new();
}

public class OB11Connection
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "ws"; // ws, ws-reverse, http, http-post

    [JsonPropertyName("enable")]
    public bool Enable { get; set; } = false;

    [JsonPropertyName("host")]
    public string Host { get; set; } = "127.0.0.1";

    [JsonPropertyName("port")]
    public int Port { get; set; } = 3001;

    [JsonPropertyName("url")]
    public string Url { get; set; } = string.Empty;

    [JsonPropertyName("heartInterval")]
    public int HeartInterval { get; set; } = 60000;

    [JsonPropertyName("enableHeart")]
    public bool EnableHeart { get; set; } = false;

    [JsonPropertyName("token")]
    public string Token { get; set; } = string.Empty;

    [JsonPropertyName("reportSelfMessage")]
    public bool ReportSelfMessage { get; set; } = false;

    [JsonPropertyName("reportOfflineMessage")]
    public bool ReportOfflineMessage { get; set; } = false;

    [JsonPropertyName("messageFormat")]
    public string MessageFormat { get; set; } = "array";

    [JsonPropertyName("debug")]
    public bool Debug { get; set; } = false;
}

public class MilkyConfig
{
    [JsonPropertyName("enable")]
    public bool Enable { get; set; } = false;

    [JsonPropertyName("reportSelfMessage")]
    public bool ReportSelfMessage { get; set; } = false;

    [JsonPropertyName("http")]
    public MilkyHttpConfig Http { get; set; } = new();

    [JsonPropertyName("webhook")]
    public MilkyWebhookConfig Webhook { get; set; } = new();
}

public class MilkyHttpConfig
{
    [JsonPropertyName("host")]
    public string Host { get; set; } = string.Empty;

    [JsonPropertyName("port")]
    public int Port { get; set; } = 3010;

    [JsonPropertyName("prefix")]
    public string Prefix { get; set; } = string.Empty;

    [JsonPropertyName("accessToken")]
    public string AccessToken { get; set; } = string.Empty;
}

public class MilkyWebhookConfig
{
    [JsonPropertyName("urls")]
    public List<string> Urls { get; set; } = new();

    [JsonPropertyName("accessToken")]
    public string AccessToken { get; set; } = string.Empty;
}
