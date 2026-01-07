using LuckyLilliaDesktop.Models;
using Microsoft.Extensions.Logging;
using System;
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
    Task<int?> FetchQQPidAsync(CancellationToken ct = default);
    void CancelAll();
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

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
        _httpClient.Dispose();
    }
}
