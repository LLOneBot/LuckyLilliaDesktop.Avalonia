using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace LuckyLilliaDesktop.Utils;

/// <summary>
/// NPM API 客户端
/// </summary>
public class NpmApiClient
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<NpmApiClient>? _logger;
    private readonly string[] _registryMirrors;

    public NpmApiClient(ILogger<NpmApiClient>? logger = null)
    {
        _logger = logger;
        _registryMirrors = Constants.NpmRegistryMirrors;
        _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(Constants.Timeouts.UpdateCheck)
        };
    }

    /// <summary>
    /// 获取 NPM 包信息
    /// </summary>
    public async Task<NpmPackageInfo?> GetPackageInfoAsync(string packageName, CancellationToken ct = default)
    {
        Exception? lastException = null;

        foreach (var registry in _registryMirrors)
        {
            try
            {
                var url = $"{registry}/{packageName}";
                _logger?.LogDebug("尝试获取包信息: {Url}", url);

                var response = await _httpClient.GetAsync(url, ct);
                if (!response.IsSuccessStatusCode)
                {
                    _logger?.LogWarning("获取包信息失败: {StatusCode} from {Registry}", response.StatusCode, registry);
                    continue;
                }

                var json = await response.Content.ReadAsStringAsync(ct);
                var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                var version = root.TryGetProperty("dist-tags", out var distTags) &&
                              distTags.TryGetProperty("latest", out var latestTag)
                    ? latestTag.GetString() ?? ""
                    : "";

                var tarballUrl = "";
                if (!string.IsNullOrEmpty(version) &&
                    root.TryGetProperty("versions", out var versions) &&
                    versions.TryGetProperty(version, out var versionInfo) &&
                    versionInfo.TryGetProperty("dist", out var dist) &&
                    dist.TryGetProperty("tarball", out var tarball))
                {
                    tarballUrl = tarball.GetString() ?? "";
                }

                return new NpmPackageInfo
                {
                    Name = packageName,
                    Version = version,
                    TarballUrl = tarballUrl,
                    Registry = registry
                };
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
            {
                lastException = ex;
                _logger?.LogWarning(ex, "从 {Registry} 获取包信息失败", registry);
            }
        }

        _logger?.LogError(lastException, "所有镜像源都无法获取包信息: {Package}", packageName);
        return null;
    }

    /// <summary>
    /// 获取包的 tarball 下载地址
    /// </summary>
    public async Task<string?> GetTarballUrlAsync(string packageName, CancellationToken ct = default)
    {
        var info = await GetPackageInfoAsync(packageName, ct);
        return info?.TarballUrl;
    }
}

/// <summary>
/// NPM 包信息
/// </summary>
public class NpmPackageInfo
{
    public string Name { get; set; } = "";
    public string Version { get; set; } = "";
    public string TarballUrl { get; set; } = "";
    public string Registry { get; set; } = "";
}
