using System.Text.Json.Serialization;

namespace LuckyLilliaDesktop.Models;

/// <summary>
/// PMHQ 自我信息
/// </summary>
public class SelfInfo
{
    [JsonPropertyName("uin")]
    public string Uin { get; set; } = string.Empty;

    [JsonPropertyName("nickname")]
    public string Nickname { get; set; } = string.Empty;
}

/// <summary>
/// PMHQ 设备信息
/// </summary>
public class DeviceInfo
{
    [JsonPropertyName("build_ver")]
    public string BuildVer { get; set; } = string.Empty;

    [JsonPropertyName("model")]
    public string Model { get; set; } = string.Empty;
}

/// <summary>
/// PMHQ API 响应
/// </summary>
public class PmhqResponse<T>
{
    [JsonPropertyName("code")]
    public int Code { get; set; }

    [JsonPropertyName("data")]
    public T? Data { get; set; }

    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;
}
