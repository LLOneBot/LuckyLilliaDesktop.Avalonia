using Microsoft.Extensions.Logging;
using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;

namespace LuckyLilliaDesktop.Services;

public enum SystemTimeCheckStatus
{
    Accurate,
    Inaccurate,
    Unavailable
}

/// <param name="Offset">网络时间减去本机时间；正数表示本机时间偏慢。</param>
public sealed record SystemTimeCheckResult(SystemTimeCheckStatus Status, TimeSpan Offset);

public interface ISystemTimeChecker
{
    Task<SystemTimeCheckResult> CheckAsync(string? httpProxy = null, CancellationToken cancellationToken = default);
}

/// <summary>
/// 通过认证服务器响应的 Date 头校验本机 UTC 时间。
/// 使用 HTTP 是为了在本机时间错误导致 TLS 证书校验失败时仍能完成检查。
/// </summary>
public sealed class SystemTimeChecker : ISystemTimeChecker
{
    private static readonly Uri TimeEndpoint = new("http://api-auth.luckylillia.com/");
    private static readonly TimeSpan MaximumAllowedOffset = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(5);

    private readonly ILogger<SystemTimeChecker> _logger;

    public SystemTimeChecker(ILogger<SystemTimeChecker> logger)
    {
        _logger = logger;
    }

    public async Task<SystemTimeCheckResult> CheckAsync(
        string? httpProxy = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var handler = new HttpClientHandler
            {
                AllowAutoRedirect = false
            };

            if (!string.IsNullOrWhiteSpace(httpProxy))
            {
                if (!Uri.TryCreate(httpProxy.Trim(), UriKind.Absolute, out var proxyUri))
                {
                    _logger.LogWarning("HTTP 代理格式无效，无法校验系统时间");
                    return new SystemTimeCheckResult(SystemTimeCheckStatus.Unavailable, TimeSpan.Zero);
                }

                handler.Proxy = new WebProxy(proxyUri);
                handler.UseProxy = true;
            }

            using var client = new HttpClient(handler)
            {
                Timeout = Timeout.InfiniteTimeSpan
            };
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(RequestTimeout);

            var endpoint = new UriBuilder(TimeEndpoint)
            {
                Query = $"_={Guid.NewGuid():N}"
            }.Uri;
            using var request = new HttpRequestMessage(HttpMethod.Head, endpoint);
            request.Headers.CacheControl = new CacheControlHeaderValue
            {
                NoCache = true,
                NoStore = true
            };

            var sentAt = DateTimeOffset.UtcNow;
            using var response = await client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                timeoutCts.Token);
            var receivedAt = DateTimeOffset.UtcNow;

            if (response.Headers.Date is not { } serverTime)
            {
                _logger.LogWarning("认证服务器响应缺少 Date 头，无法校验系统时间");
                return new SystemTimeCheckResult(SystemTimeCheckStatus.Unavailable, TimeSpan.Zero);
            }

            var roundTrip = receivedAt - sentAt;
            var localMidpoint = sentAt + TimeSpan.FromTicks(roundTrip.Ticks / 2);
            var offset = serverTime.ToUniversalTime() - localMidpoint;
            var status = offset.Duration() <= MaximumAllowedOffset
                ? SystemTimeCheckStatus.Accurate
                : SystemTimeCheckStatus.Inaccurate;

            _logger.LogInformation(
                "系统时间校验完成: 状态={Status}, 偏差={OffsetSeconds:F1}秒, 请求耗时={RoundTripMilliseconds:F0}毫秒",
                status,
                offset.TotalSeconds,
                roundTrip.TotalMilliseconds);

            return new SystemTimeCheckResult(status, offset);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("校验系统时间超时，本次继续启动");
            return new SystemTimeCheckResult(SystemTimeCheckStatus.Unavailable, TimeSpan.Zero);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "校验系统时间失败，本次继续启动");
            return new SystemTimeCheckResult(SystemTimeCheckStatus.Unavailable, TimeSpan.Zero);
        }
    }
}
