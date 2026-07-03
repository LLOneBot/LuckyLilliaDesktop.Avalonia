using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace LuckyLilliaDesktop.Services;

public enum AuthTokenValidationStatus
{
    // 服务器明确认可 (HTTP 2xx)
    Valid,
    // 服务器明确拒绝 (HTTP 401/403): token 无效 / 已吊销 / 无权限
    Invalid,
    // 无法判定 (网络错误 / 超时 / 5xx): 不足以判死 token
    Inconclusive,
}

public sealed class AuthTokenValidationResult
{
    public AuthTokenValidationStatus Status { get; }
    public string? Message { get; }

    public AuthTokenValidationResult(AuthTokenValidationStatus status, string? message = null)
    {
        Status = status;
        Message = message;
    }
}

public interface IAuthTokenValidator
{
    Task<AuthTokenValidationResult> ValidateAsync(string token, CancellationToken ct = default);
}

/// 用 manager-server 校验 auth_token 是否有效。
///
/// 复现 SecureSDK 的 preflight: GET /api/sign/info + Authorization: Bearer <token>。
/// 该端点不走 envelope、无需设备指纹, 纯 Bearer 鉴权 (见 SecureSDK/src/client.rs::preflight)。
/// 服务端对"已获取但还没绑任何 uin"的新 token 也返 200 (allowed_uins 可空,
/// 见 ManagerServer api/sign.rs::handle_info), 所以首次输入 token 不会被误判无效。
///
/// base url: 生产 https://api-auth.luckylillia.com (见 SecureSDK/src/lib.rs::default_base_url)。
/// 环境变量 LILLIA_AUTH_BASE_URL 可覆盖 (dev 连 http://localhost:8080)。
public sealed class AuthTokenValidator : IAuthTokenValidator
{
    private const string DefaultBaseUrl = "https://api-auth.luckylillia.com";
    private const string InfoPath = "/api/sign/info";

    private readonly ILogger<AuthTokenValidator> _logger;
    private readonly HttpClient _httpClient;

    public AuthTokenValidator(ILogger<AuthTokenValidator> logger)
    {
        _logger = logger;
        // 不设 UseProxy=false: SDK 注释明确生产须走系统代理打 api-auth, 这里跟随系统代理。
        _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(10),
        };
    }

    private static string BaseUrl
    {
        get
        {
            var overrideUrl = Environment.GetEnvironmentVariable("LILLIA_AUTH_BASE_URL");
            return string.IsNullOrWhiteSpace(overrideUrl)
                ? DefaultBaseUrl
                : overrideUrl.TrimEnd('/');
        }
    }

    public async Task<AuthTokenValidationResult> ValidateAsync(string token, CancellationToken ct = default)
    {
        token = token?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(token))
        {
            return new AuthTokenValidationResult(AuthTokenValidationStatus.Invalid, "Auth Token 不能为空");
        }

        var url = BaseUrl + InfoPath;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var resp = await _httpClient.SendAsync(req, ct);
            var status = (int)resp.StatusCode;

            if (status is >= 200 and < 300)
            {
                _logger.LogInformation("Auth Token 验证通过 (HTTP {Status})", status);
                return new AuthTokenValidationResult(AuthTokenValidationStatus.Valid);
            }

            if (status is 401 or 403)
            {
                _logger.LogWarning("Auth Token 验证被拒 (HTTP {Status})", status);
                return new AuthTokenValidationResult(
                    AuthTokenValidationStatus.Invalid,
                    "Auth Token 无效、已失效或无权限，请重新获取");
            }

            _logger.LogWarning("Auth Token 验证未能判定 (HTTP {Status})", status);
            return new AuthTokenValidationResult(
                AuthTokenValidationStatus.Inconclusive,
                $"无法验证 Auth Token（服务器返回 {status}）");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // 外部主动取消, 上抛给调用方处理 (不是超时)。
            throw;
        }
        catch (Exception ex)
        {
            // 超时 (HttpClient.Timeout 抛 TaskCanceledException 但 ct 未取消) / 网络错误: 无法判定。
            _logger.LogWarning(ex, "Auth Token 验证请求失败 (网络或超时)");
            return new AuthTokenValidationResult(
                AuthTokenValidationStatus.Inconclusive,
                "无法连接验证服务器，请检查网络连接");
        }
    }
}
