using System;
using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using LuckyLilliaDesktop.Utils;
using Microsoft.Extensions.Logging;

namespace LuckyLilliaDesktop.Services;

/// <summary>
/// 下载进度信息
/// </summary>
public class DownloadProgress
{
    public long Downloaded { get; set; }
    public long Total { get; set; }
    public double Percentage => Total > 0 ? (double)Downloaded / Total * 100 : 0;
    public string Status { get; set; } = "";
}

/// <summary>
/// 下载服务接口
/// </summary>
public interface IDownloadService
{
    Task<bool> DownloadPmhqAsync(IProgress<DownloadProgress>? progress = null, CancellationToken ct = default);
    Task<bool> DownloadLLBotAsync(IProgress<DownloadProgress>? progress = null, CancellationToken ct = default);
    Task<bool> DownloadNodeAsync(IProgress<DownloadProgress>? progress = null, CancellationToken ct = default);
    Task<bool> DownloadFFmpegAsync(IProgress<DownloadProgress>? progress = null, CancellationToken ct = default);
    Task<bool> DownloadQQAsync(IProgress<DownloadProgress>? progress = null, CancellationToken ct = default);
    bool CheckFileExists(string path);
    string? FindInPath(string executable);
}

/// <summary>
/// 下载服务实现
/// </summary>
public class DownloadService : IDownloadService
{
    private readonly ILogger<DownloadService> _logger;
    private readonly NpmApiClient _npmClient;
    private readonly HttpClient _httpClient;

    public DownloadService(ILogger<DownloadService> logger)
    {
        _logger = logger;
        _npmClient = new NpmApiClient();
        _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(Constants.Timeouts.Download)
        };
    }

    public bool CheckFileExists(string path)
    {
        return File.Exists(path);
    }

    public string? FindInPath(string executable)
    {
        var pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(pathEnv)) return null;

        var paths = pathEnv.Split(Path.PathSeparator);
        foreach (var path in paths)
        {
            var fullPath = Path.Combine(path, executable);
            if (File.Exists(fullPath)) return fullPath;

            // Windows: try with .exe extension
            if (OperatingSystem.IsWindows())
            {
                var exePath = Path.Combine(path, executable + ".exe");
                if (File.Exists(exePath)) return exePath;
            }
        }
        return null;
    }

    public async Task<bool> DownloadPmhqAsync(IProgress<DownloadProgress>? progress = null, CancellationToken ct = default)
    {
        return await DownloadAndExtractAsync(
            Constants.NpmPackages.Pmhq,
            Constants.DefaultPaths.PmhqDir,
            progress,
            ct);
    }

    public async Task<bool> DownloadLLBotAsync(IProgress<DownloadProgress>? progress = null, CancellationToken ct = default)
    {
        return await DownloadAndExtractAsync(
            Constants.NpmPackages.LLBot,
            Constants.DefaultPaths.LLBotDir,
            progress,
            ct);
    }

    public async Task<bool> DownloadNodeAsync(IProgress<DownloadProgress>? progress = null, CancellationToken ct = default)
    {
        return await DownloadAndExtractAsync(
            Constants.NpmPackages.Node,
            Constants.DefaultPaths.LLBotDir,
            progress,
            ct,
            skipFiles: new[] { "package.json" });
    }

    public async Task<bool> DownloadFFmpegAsync(IProgress<DownloadProgress>? progress = null, CancellationToken ct = default)
    {
        return await DownloadAndExtractAsync(
            Constants.NpmPackages.FFmpeg,
            Constants.DefaultPaths.LLBotDir,
            progress,
            ct,
            skipFiles: new[] { "package.json" });
    }

    public async Task<bool> DownloadQQAsync(IProgress<DownloadProgress>? progress = null, CancellationToken ct = default)
    {
        try
        {
            progress?.Report(new DownloadProgress { Status = "正在下载 QQ 安装包..." });

            var tempFile = Path.Combine(Path.GetTempPath(), "QQ_Setup.exe");

            using (var response = await _httpClient.GetAsync(Constants.QQDownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                response.EnsureSuccessStatusCode();

                var totalBytes = response.Content.Headers.ContentLength ?? 0;
                var downloadedBytes = 0L;

                await using var contentStream = await response.Content.ReadAsStreamAsync(ct);
                await using var fileStream = new FileStream(tempFile, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true);

                var buffer = new byte[8192];
                int bytesRead;

                while ((bytesRead = await contentStream.ReadAsync(buffer, ct)) > 0)
                {
                    await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), ct);
                    downloadedBytes += bytesRead;

                    progress?.Report(new DownloadProgress
                    {
                        Downloaded = downloadedBytes,
                        Total = totalBytes,
                        Status = $"正在下载 QQ... {downloadedBytes / 1024 / 1024:F1} MB / {totalBytes / 1024 / 1024:F1} MB"
                    });
                }
            }

            progress?.Report(new DownloadProgress { Status = "正在安装 QQ..." });

            var process = new System.Diagnostics.Process
            {
                StartInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = tempFile,
                    Arguments = "/S",
                    UseShellExecute = true
                }
            };
            process.Start();
            await process.WaitForExitAsync(ct);

            try { File.Delete(tempFile); } catch { }

            _logger.LogInformation("QQ 安装完成");
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "下载安装 QQ 失败");
            return false;
        }
    }

    private async Task<bool> DownloadAndExtractAsync(
        string packageName,
        string extractDir,
        IProgress<DownloadProgress>? progress,
        CancellationToken ct,
        string[]? skipFiles = null)
    {
        try
        {
            progress?.Report(new DownloadProgress { Status = "正在获取下载地址..." });

            var tarballUrl = await _npmClient.GetTarballUrlAsync(packageName, ct);
            if (string.IsNullOrEmpty(tarballUrl))
            {
                _logger.LogError("无法获取 {Package} 的下载地址", packageName);
                return false;
            }

            _logger.LogInformation("开始下载: {Url}", tarballUrl);
            progress?.Report(new DownloadProgress { Status = "正在下载..." });

            // 确保目录存在
            Directory.CreateDirectory(extractDir);

            var tempFile = Path.Combine(extractDir, "temp_download.tgz");

            // 下载文件
            using (var response = await _httpClient.GetAsync(tarballUrl, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                response.EnsureSuccessStatusCode();

                var totalBytes = response.Content.Headers.ContentLength ?? 0;
                var downloadedBytes = 0L;

                await using var contentStream = await response.Content.ReadAsStreamAsync(ct);
                await using var fileStream = new FileStream(tempFile, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true);

                var buffer = new byte[8192];
                int bytesRead;

                while ((bytesRead = await contentStream.ReadAsync(buffer, ct)) > 0)
                {
                    await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), ct);
                    downloadedBytes += bytesRead;

                    progress?.Report(new DownloadProgress
                    {
                        Downloaded = downloadedBytes,
                        Total = totalBytes,
                        Status = $"正在下载... {downloadedBytes / 1024 / 1024:F1} MB / {totalBytes / 1024 / 1024:F1} MB"
                    });
                }
            }

            progress?.Report(new DownloadProgress { Status = "正在解压..." });

            // 解压 tarball (tgz = tar.gz)
            await ExtractTarGzAsync(tempFile, extractDir, skipFiles, ct);

            // 删除临时文件
            if (File.Exists(tempFile))
            {
                File.Delete(tempFile);
            }

            _logger.LogInformation("下载并解压完成: {Package} -> {Dir}", packageName, extractDir);
            progress?.Report(new DownloadProgress { Status = "下载完成" });

            return true;
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("下载已取消: {Package}", packageName);
            progress?.Report(new DownloadProgress { Status = "下载已取消" });
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "下载失败: {Package}", packageName);
            progress?.Report(new DownloadProgress { Status = $"下载失败: {ex.Message}" });
            return false;
        }
    }

    private async Task ExtractTarGzAsync(string tarGzPath, string extractDir, string[]? skipFiles, CancellationToken ct)
    {
        // 先解压 gzip
        var tarPath = Path.Combine(Path.GetDirectoryName(tarGzPath)!, "temp.tar");

        await using (var gzStream = new FileStream(tarGzPath, FileMode.Open, FileAccess.Read))
        await using (var decompressedStream = new GZipStream(gzStream, CompressionMode.Decompress))
        await using (var tarStream = new FileStream(tarPath, FileMode.Create, FileAccess.Write))
        {
            await decompressedStream.CopyToAsync(tarStream, ct);
        }

        // 解压 tar
        var packageDir = Path.Combine(extractDir, "package");

        await using (var tarStream = new FileStream(tarPath, FileMode.Open, FileAccess.Read))
        {
            await TarFile.ExtractToDirectoryAsync(tarStream, extractDir, true, ct);
        }

        // 移动 package 目录内容到目标目录
        if (Directory.Exists(packageDir))
        {
            foreach (var item in Directory.GetFileSystemEntries(packageDir))
            {
                var itemName = Path.GetFileName(item);

                // 跳过指定的文件
                if (skipFiles != null && Array.Exists(skipFiles, f => f.Equals(itemName, StringComparison.OrdinalIgnoreCase)))
                {
                    _logger.LogDebug("跳过文件: {Item}", itemName);
                    continue;
                }

                var destPath = Path.Combine(extractDir, itemName);

                if (File.Exists(destPath))
                    File.Delete(destPath);
                if (Directory.Exists(destPath))
                    Directory.Delete(destPath, true);

                if (File.Exists(item))
                    File.Move(item, destPath);
                else if (Directory.Exists(item))
                    Directory.Move(item, destPath);
            }

            Directory.Delete(packageDir, true);
        }

        // 删除临时 tar 文件
        if (File.Exists(tarPath))
        {
            File.Delete(tarPath);
        }
    }
}
