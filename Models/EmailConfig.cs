using System.Text.Json.Serialization;

namespace LuckyLilliaDesktop.Models;

public class EmailConfig
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = false;

    [JsonPropertyName("smtp")]
    public SmtpConfig Smtp { get; set; } = new();

    [JsonPropertyName("from")]
    public string From { get; set; } = string.Empty;

    [JsonPropertyName("to")]
    public string To { get; set; } = string.Empty;
}

public class SmtpConfig
{
    [JsonPropertyName("host")]
    public string Host { get; set; } = string.Empty;

    [JsonPropertyName("port")]
    public int Port { get; set; } = 587;

    [JsonPropertyName("secure")]
    public bool Secure { get; set; } = true;

    [JsonPropertyName("auth")]
    public SmtpAuth Auth { get; set; } = new();
}

public class SmtpAuth
{
    [JsonPropertyName("user")]
    public string User { get; set; } = string.Empty;

    [JsonPropertyName("pass")]
    public string Pass { get; set; } = string.Empty;
}
