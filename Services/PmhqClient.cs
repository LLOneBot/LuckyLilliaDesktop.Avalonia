using LuckyLilliaDesktop.Models;
using Microsoft.Extensions.Logging;
using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace LuckyLilliaDesktop.Services;

public interface IPmhqClient
{
    void SetPort(int port);
    void ClearPort();
    bool HasPort { get; }
    Task<SelfInfo?> FetchSelfInfoAsync(CancellationToken ct = default);
    Task<DeviceInfo?> FetchDeviceInfoAsync(CancellationToken ct = default);
    Task<int?> FetchQQPidAsync(CancellationToken ct = default);
    Task<List<LoginAccount>?> GetLoginListAsync(CancellationToken ct = default);
    Task<bool> QuickLoginAsync(string uin, CancellationToken ct = default);
    Task<bool> RequestQRCodeAsync(CancellationToken ct = default);
    void CancelAll();
}

public class LoginAccount : INotifyPropertyChanged
{
    public string Uin { get; set; } = "";
    public string NickName { get; set; } = "";
    public string FaceUrl { get; set; } = "";
    public bool IsQuickLogin { get; set; }

    // 列表显示用: 有昵称显昵称, 否则显 QQ 号
    public string DisplayName => string.IsNullOrEmpty(NickName) ? Uin : NickName;
    public bool HasNick => !string.IsNullOrEmpty(NickName);

    // 头像异步下载后填充, 通知 UI 更新
    private Avalonia.Media.Imaging.Bitmap? _avatar;
    public Avalonia.Media.Imaging.Bitmap? Avatar
    {
        get => _avatar;
        set
        {
            _avatar = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Avatar)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

public class PmhqClient : IPmhqClient, IDisposable
{
    private readonly ILogger<PmhqClient> _logger;
    private readonly HttpClient _httpClient;
    private CancellationTokenSource _cts = new();
    private int? _port = null;

    public bool HasPort => _port.HasValue;

    public PmhqClient(ILogger<PmhqClient> logger)
    {
        _logger = logger;
        _httpClient = new HttpClient(new HttpClientHandler { UseProxy = false })
        {
            Timeout = TimeSpan.FromSeconds(5)
        };
    }

    public void SetPort(int port)
    {
        _port = port;
        _logger.LogInformation("PMHQ 端口设置为: {Port}", port);
    }

    public void ClearPort()
    {
        _port = null;
        _logger.LogInformation("PMHQ 端口已清除");
    }

    public void CancelAll()
    {
        _cts.Cancel();
        _cts.Dispose();
        _cts = new CancellationTokenSource();
    }

    private async Task<JsonElement?> CallAsync(string func, object[]? args = null, CancellationToken ct = default)
    {
        if (!_port.HasValue)
        {
            _logger.LogDebug("PMHQ 端口未设置，跳过 API 调用: {Func}", func);
            return null;
        }

        var url = $"http://127.0.0.1:{_port.Value}";
        var argArray = new JsonArray();
        foreach (var arg in args ?? Array.Empty<object>())
        {
            ((IList<JsonNode?>)argArray).Add(ConvertRpcArgumentToJsonNode(arg));
        }

        var payload = new JsonObject
        {
            ["type"] = "call",
            ["data"] = new JsonObject
            {
                ["func"] = func,
                ["args"] = argArray
            }
        };

        // 合并内部和外部的 CancellationToken
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, ct);

        try
        {
            _logger.LogDebug("调用 PMHQ API: {Func}, URL: {Url}", func, url);
            using var content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");
            var response = await _httpClient.PostAsync(url, content, linkedCts.Token);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("PMHQ API 返回 HTTP {StatusCode}: {Func}", response.StatusCode, func);
                return null;
            }

            var responseJson = await response.Content.ReadAsStringAsync(linkedCts.Token);
            using var document = JsonDocument.Parse(responseJson);
            var json = document.RootElement;

            if (json.TryGetProperty("type", out var typeElem) && typeElem.GetString() == "call" &&
                json.TryGetProperty("data", out var dataElem))
            {
                if (dataElem.ValueKind == JsonValueKind.String)
                {
                    var dataStr = dataElem.GetString();
                    if (!string.IsNullOrEmpty(dataStr))
                    {
                        _logger.LogDebug("PMHQ API 调用成功: {Func}", func);
                        using var dataDocument = JsonDocument.Parse(dataStr);
                        return dataDocument.RootElement.Clone();
                    }
                }
                else if (dataElem.ValueKind == JsonValueKind.Object)
                {
                    _logger.LogDebug("PMHQ API 调用成功: {Func}", func);
                    return dataElem.Clone();
                }
            }

            _logger.LogWarning("PMHQ API 响应格式异常: {Func}, Response: {Json}", func, json.ToString());
            return null;
        }
        catch (TaskCanceledException ex)
        {
            _logger.LogWarning("PMHQ API 请求超时: {Func}, URL: {Url}, Message: {Message}", func, url, ex.Message);
            return null;
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("PMHQ API 调用已取消: {Func}", func);
            return null;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "PMHQ API 连接失败: {Func}, URL: {Url}", func, url);
            return null;
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "PMHQ API 响应 JSON 解析失败: {Func}", func);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "PMHQ API 调用异常: {Func}, URL: {Url}", func, url);
            return null;
        }
    }

    private static JsonNode? ConvertRpcArgumentToJsonNode(object? arg)
    {
        return arg switch
        {
            null => null,
            JsonNode node => node.DeepClone(),
            JsonElement element => JsonNode.Parse(element.GetRawText()),
            string value => JsonValue.Create(value),
            bool value => JsonValue.Create(value),
            byte value => JsonValue.Create(value),
            sbyte value => JsonValue.Create(value),
            short value => JsonValue.Create(value),
            ushort value => JsonValue.Create(value),
            int value => JsonValue.Create(value),
            uint value => JsonValue.Create(value),
            long value => JsonValue.Create(value),
            ulong value => JsonValue.Create(value),
            float value => JsonValue.Create(value),
            double value => JsonValue.Create(value),
            decimal value => JsonValue.Create(value),
            IReadOnlyDictionary<string, object?> dict => ConvertDictionaryToJsonObject(dict),
            IDictionary<string, object?> dict => ConvertDictionaryToJsonObject(dict),
            IEnumerable enumerable when arg is not string => ConvertEnumerableToJsonArray(enumerable),
            _ => throw new NotSupportedException($"PMHQ RPC 参数类型不支持 AOT-safe JSON 序列化: {arg.GetType().FullName}")
        };
    }

    private static JsonObject ConvertDictionaryToJsonObject(IEnumerable<KeyValuePair<string, object?>> values)
    {
        var json = new JsonObject();
        foreach (var pair in values)
        {
            json[pair.Key] = ConvertRpcArgumentToJsonNode(pair.Value);
        }

        return json;
    }

    private static JsonArray ConvertEnumerableToJsonArray(IEnumerable values)
    {
        var json = new JsonArray();
        foreach (var value in values)
        {
            ((IList<JsonNode?>)json).Add(ConvertRpcArgumentToJsonNode(value));
        }

        return json;
    }

    // PMHQ 不再提供 getSelfInfo 查询 QQ 账号信息, uin/昵称改走 LLBot IPC. 直接返回 null 避免无效请求/刷日志.
    public Task<SelfInfo?> FetchSelfInfoAsync(CancellationToken ct = default)
    {
        return Task.FromResult<SelfInfo?>(null);
    }

    // PMHQ /health (GET) 返回 qq_pid + qq_version, 取代旧的 getDeviceInfo / getProcessInfo
    private async Task<(int? Pid, string? Version)?> FetchHealthAsync(CancellationToken ct = default)
    {
        if (!_port.HasValue) return null;
        var url = $"http://127.0.0.1:{_port.Value}/health";
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, ct);
        try
        {
            var responseJson = await _httpClient.GetStringAsync(url, linkedCts.Token);
            using var document = JsonDocument.Parse(responseJson);
            var json = document.RootElement;
            int? pid = null;
            if (json.TryGetProperty("qq_pid", out var pidElem) && pidElem.ValueKind == JsonValueKind.Number)
                pid = pidElem.GetInt32();
            string? version = null;
            if (json.TryGetProperty("qq_version", out var verElem) && verElem.ValueKind == JsonValueKind.String)
                version = verElem.GetString();
            return (pid, version);
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception ex)
        {
            _logger.LogDebug("PMHQ /health 调用失败: {Message}", ex.Message);
            return null;
        }
    }

    public async Task<DeviceInfo?> FetchDeviceInfoAsync(CancellationToken ct = default)
    {
        var health = await FetchHealthAsync(ct);
        if (string.IsNullOrEmpty(health?.Version))
            return null;
        return new DeviceInfo { BuildVer = health.Value.Version!, Model = "" };
    }

    public async Task<int?> FetchQQPidAsync(CancellationToken ct = default)
    {
        var health = await FetchHealthAsync(ct);
        return health?.Pid;
    }

    public async Task<List<LoginAccount>?> GetLoginListAsync(CancellationToken ct = default)
    {
        try
        {
            var data = await CallAsync("loginService.getLoginList", ct: ct);
            if (data == null) return null;

            var dataElem = data.Value;
            if (!dataElem.TryGetProperty("result", out var result)) return null;
            if (result.ValueKind != JsonValueKind.Object) return null;
            if (!result.TryGetProperty("LocalLoginInfoList", out var listElem)) return null;
            if (listElem.ValueKind != JsonValueKind.Array) return null;

            var accounts = new List<LoginAccount>();
            foreach (var item in listElem.EnumerateArray())
            {
                var isQuickLogin = item.TryGetProperty("isQuickLogin", out var qe) && qe.GetBoolean();
                var isUserLogin = item.TryGetProperty("isUserLogin", out var ue) && ue.GetBoolean();
                if (!isQuickLogin || isUserLogin) continue;

                accounts.Add(new LoginAccount
                {
                    Uin = item.TryGetProperty("uin", out var u) ? u.GetString() ?? "" : "",
                    NickName = item.TryGetProperty("nickName", out var n) ? n.GetString() ?? "" : "",
                    FaceUrl = item.TryGetProperty("faceUrl", out var f) ? f.GetString() ?? "" : "",
                    IsQuickLogin = true
                });
            }
            return accounts;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "解析登录列表失败");
            return null;
        }
    }

    public async Task<bool> QuickLoginAsync(string uin, CancellationToken ct = default)
    {
        try
        {
            _logger.LogInformation("尝试快速登录: {Uin}", uin);
            var data = await CallAsync("loginService.quickLoginWithUin", [uin], ct);
            if (data == null)
            {
                _logger.LogWarning("快速登录失败: API 返回空");
                return false;
            }

            // 返回格式: {result: {result: "0", loginErrorInfo: {...}}}
            if (data.Value.TryGetProperty("result", out var outer) &&
                outer.TryGetProperty("result", out var inner))
            {
                var success = inner.GetString() == "0";
                if (success)
                    _logger.LogInformation("快速登录成功: {Uin}", uin);
                else
                    _logger.LogWarning("快速登录失败: {Uin}", uin);
                return success;
            }

            _logger.LogWarning("快速登录失败: 响应格式异常");
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "快速登录异常: {Uin}", uin);
            return false;
        }
    }

    public async Task<bool> RequestQRCodeAsync(CancellationToken ct = default)
    {
        try
        {
            _logger.LogInformation("请求二维码登录...");
            var data = await CallAsync("loginService.getQRCodePicture", ct: ct);
            var success = data != null;
            if (success)
                _logger.LogInformation("二维码请求成功");
            else
                _logger.LogWarning("二维码请求失败");
            return success;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "请求二维码异常");
            return false;
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
        _httpClient.Dispose();
        GC.SuppressFinalize(this);
    }
}
