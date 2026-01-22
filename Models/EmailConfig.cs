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
    public int? Port { get; set; }

    [JsonPropertyName("secure")]
    public bool Secure { get; set; } = true;

    [JsonPropertyName("auth")]
    public SmtpAuth Auth { get; set; } = new();
    
    [JsonIgnore]
    public int PortValue => Port ?? 587;
    
    [JsonIgnore]
    public string SecureMode
    {
        get => Secure ? "ssl" : "starttls";
        set => Secure = value == "ssl";
    }
}

public class SmtpAuth
{
    [JsonPropertyName("user")]
    public string User { get; set; } = string.Empty;

    [JsonPropertyName("pass")]
    public string Pass { get; set; } = string.Empty;
}
