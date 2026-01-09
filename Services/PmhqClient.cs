using LuckyLilliaDesktop.Models;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
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

public class LoginAccount
{
    public string Uin { get; set; } = "";
    public string NickName { get; set; } = "";
    public string FaceUrl { get; set; } = "";
    public bool IsQuickLogin { get; set; }
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
        _httpClient = new HttpClient
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
            return null;

        var url = $"http://127.0.0.1:{_port.Value}";
        var payload = new
        {
            type = "call",
            data = new
            {
                func,
                args = args ?? Array.Empty<object>()
            }
        };

        // 合并内部和外部的 CancellationToken
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, ct);

        try
        {
            var response = await _httpClient.PostAsJsonAsync(url, payload, linkedCts.Token);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogDebug("PMHQ API 返回 HTTP {StatusCode}", response.StatusCode);
                return null;
            }

            var json = await response.Content.ReadFromJsonAsync<JsonElement>(linkedCts.Token);
            
            if (json.TryGetProperty("type", out var typeElem) && typeElem.GetString() == "call" &&
                json.TryGetProperty("data", out var dataElem))
            {
                if (dataElem.ValueKind == JsonValueKind.String)
                {
                    var dataStr = dataElem.GetString();
                    if (!string.IsNullOrEmpty(dataStr))
                    {
                        return JsonSerializer.Deserialize<JsonElement>(dataStr);
                    }
                }
                else if (dataElem.ValueKind == JsonValueKind.Object)
                {
                    return dataElem;
                }
            }

            return null;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (HttpRequestException)
        {
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "PMHQ API 调用异常");
            return null;
        }
    }

    public async Task<SelfInfo?> FetchSelfInfoAsync(CancellationToken ct = default)
    {
        var data = await CallAsync("getSelfInfo", ct: ct);
        if (data == null)
            return null;

        var dataElem = data.Value;
        if (!dataElem.TryGetProperty("result", out var result))
            return null;

        if (result.ValueKind != JsonValueKind.Object)
            return null;

        var uin = "";
        if (result.TryGetProperty("uin", out var uinElem))
        {
            uin = uinElem.ValueKind == JsonValueKind.Number 
                ? uinElem.GetInt64().ToString() 
                : uinElem.GetString() ?? "";
        }

        if (string.IsNullOrEmpty(uin))
            return null;

        var nickname = "";
        if (result.TryGetProperty("nickName", out var nickElem) ||
            result.TryGetProperty("nickname", out nickElem) ||
            result.TryGetProperty("nick", out nickElem))
        {
            nickname = nickElem.GetString() ?? "";
        }

        return new SelfInfo { Uin = uin, Nickname = nickname };
    }

    public async Task<DeviceInfo?> FetchDeviceInfoAsync(CancellationToken ct = default)
    {
        var data = await CallAsync("getDeviceInfo", ct: ct);
        if (data == null)
            return null;

        var dataElem = data.Value;
        if (!dataElem.TryGetProperty("result", out var result))
            return null;

        if (result.ValueKind != JsonValueKind.Object)
            return null;

        var buildVer = "";
        if (result.TryGetProperty("buildVer", out var buildVerElem))
        {
            buildVer = buildVerElem.GetString() ?? "";
        }

        var model = "";
        if (result.TryGetProperty("devType", out var modelElem))
        {
            model = modelElem.GetString() ?? "";
        }

        return new DeviceInfo { BuildVer = buildVer, Model = model };
    }

    public async Task<int?> FetchQQPidAsync(CancellationToken ct = default)
    {
        var data = await CallAsync("getProcessInfo", ct: ct);
        if (data == null)
            return null;

        var dataElem = data.Value;
        if (!dataElem.TryGetProperty("result", out var result))
            return null;

        if (result.ValueKind != JsonValueKind.Object)
            return null;

        if (result.TryGetProperty("pid", out var pidElem) && pidElem.ValueKind == JsonValueKind.Number)
        {
            return pidElem.GetInt32();
        }

        return null;
    }

    public async Task<List<LoginAccount>?> GetLoginListAsync(CancellationToken ct = default)
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

    public async Task<bool> QuickLoginAsync(string uin, CancellationToken ct = default)
    {
        var data = await CallAsync("loginService.quickLoginWithUin", [uin], ct);
        if (data == null) return false;

        // 返回格式: {result: {result: "0", loginErrorInfo: {...}}}
        if (data.Value.TryGetProperty("result", out var outer) &&
            outer.TryGetProperty("result", out var inner))
        {
            return inner.GetString() == "0";
        }
        
        return false;
    }

    public async Task<bool> RequestQRCodeAsync(CancellationToken ct = default)
    {
        var data = await CallAsync("loginService.getQRCodePicture", ct: ct);
        return data != null;
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
        _httpClient.Dispose();
    }
}
