using System;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace LuckyLilliaDesktop.Services;

public interface IGitHubHelper
{
    Task<string?> GetLatestTagAsync(string owner, string repo, CancellationToken ct = default);
    Task<bool> DownloadFileAsync(string url, string destPath, 
        Action<long, long>? onProgress = null, CancellationToken ct = default);
    Task<bool> DownloadWithFallbackAsync(string[] urls, string destPath,
        Action<long, long>? onProgress = null, CancellationToken ct = default);
    string GetProxiedUrl(string originalUrl);
}

public class GitHubHelper : IGitHubHelper, IDisposable
{
    private readonly ILogger<GitHubHelper> _logger;
    private readonly HttpClient _httpClient;

    private static readonly string[] GhProxies = [
        "https://gh-proxy.com/",
        "https://ghproxy.net/",
    ];

    public GitHubHelper(ILogger<GitHubHelper> logger)
    {
        _logger = logger;
        _httpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        _httpClient.DefaultRequestHeaders.Add("User-Agent", "LuckyLilliaDesktop");
    }

    public async Task<string?> GetLatestTagAsync(string owner, string repo, CancellationToken ct = default)
    {
        var apiUrl = $"https://api.github.com/repos/{owner}/{repo}/releases/latest";

        // 先尝试直连
        try
        {
            var response = await _httpClient.GetStringAsync(apiUrl, ct);
            using var doc = JsonDocument.Parse(response);
            return doc.RootElement.GetProperty("tag_name").GetString();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "直连 GitHub API 失败，尝试代理");
        }

        // 尝试各个代理
        foreach (var proxy in GhProxies)
        {
            try
            {
                var proxyUrl = $"{proxy}{apiUrl}";
                var response = await _httpClient.GetStringAsync(proxyUrl, ct);
                using var doc = JsonDocument.Parse(response);
                return doc.RootElement.GetProperty("tag_name").GetString();
            }
            catch { }
        }

        _logger.LogError("所有代理获取版本都失败");
        return null;
    }

    public string GetProxiedUrl(string originalUrl)
    {
        if (originalUrl.StartsWith("https://github.com"))
            return $"{GhProxies[0]}{originalUrl}";
        return originalUrl;
    }

    public async Task<bool> DownloadWithFallbackAsync(string[] urls, string destPath,
        Action<long, long>? onProgress = null, CancellationToken ct = default)
    {
        foreach (var url in urls)
        {
            _logger.LogInformation("尝试下载: {Url}", url);
            if (await DownloadFileAsync(url, destPath, onProgress, ct))
                return true;
            _logger.LogWarning("下载失败，尝试下一个地址");
        }
        return false;
    }

    public async Task<bool> DownloadFileAsync(string url, string destPath,
        Action<long, long>? onProgress = null, CancellationToken ct = default)
    {
        const int maxRetries = 3;
        
        for (int retry = 0; retry < maxRetries; retry++)
        {
            try
            {
                if (retry > 0)
                {
                    _logger.LogInformation("重试下载 ({Retry}/{Max})", retry + 1, maxRetries);
                    await Task.Delay(1000 * retry, ct);
                }

                using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
                response.EnsureSuccessStatusCode();

                var totalBytes = response.Content.Headers.ContentLength ?? 0;
                var downloadedBytes = 0L;

                await using var contentStream = await response.Content.ReadAsStreamAsync(ct);
                await using var fileStream = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None, 65536, true);

                var buffer = new byte[65536];
                int bytesRead;

                while ((bytesRead = await contentStream.ReadAsync(buffer, ct)) > 0)
                {
                    await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), ct);
                    downloadedBytes += bytesRead;
                    onProgress?.Invoke(downloadedBytes, totalBytes);
                }

                return true;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "下载失败 (尝试 {Retry}/{Max}): {Url}", retry + 1, maxRetries, url);
            }
        }

        _logger.LogError("下载文件失败，已重试 {Max} 次: {Url}", maxRetries, url);
        return false;
    }

    public void Dispose()
    {
        _httpClient.Dispose();
        GC.SuppressFinalize(this);
    }
}
