using System;
using System.IO;
using System.IO.Compression;
using System.Threading;
using System.Threading.Tasks;
using LuckyLilliaDesktop.Services;
using Microsoft.Extensions.Logging;

namespace LuckyLilliaDesktop.Utils;

public interface IGitHubArchiveHelper
{
    Task<bool> DownloadAndExtractAsync(string repoPath, string branch, string targetDir,
        Action<long, long>? onProgress = null, CancellationToken ct = default);
}

public class GitHubArchiveHelper : IGitHubArchiveHelper
{
    private readonly ILogger<GitHubArchiveHelper> _logger;
    private readonly IGitHubHelper _gitHubHelper;

    public GitHubArchiveHelper(ILogger<GitHubArchiveHelper> logger, IGitHubHelper gitHubHelper)
    {
        _logger = logger;
        _gitHubHelper = gitHubHelper;
    }

    public async Task<bool> DownloadAndExtractAsync(string repoPath, string branch, string targetDir,
        Action<long, long>? onProgress = null, CancellationToken ct = default)
    {
        var zipFile = Path.Combine(Path.GetTempPath(), $"{Path.GetFileName(repoPath)}-{branch}-{Guid.NewGuid():N}.zip");

        try
        {
            string[] urls = [
                $"https://gh-proxy.com/https://github.com/{repoPath}/archive/refs/heads/{branch}.zip",
                $"https://ghproxy.net/https://github.com/{repoPath}/archive/refs/heads/{branch}.zip",
                $"https://mirror.ghproxy.com/https://github.com/{repoPath}/archive/refs/heads/{branch}.zip",
                $"https://github.com/{repoPath}/archive/refs/heads/{branch}.zip"
            ];

            var downloadSuccess = await _gitHubHelper.DownloadWithFallbackAsync(urls, zipFile, onProgress, ct);
            if (!downloadSuccess)
            {
                _logger.LogError("下载 {RepoPath} 失败", repoPath);
                return false;
            }

            var tempExtract = Path.Combine(Path.GetTempPath(), $"{Path.GetFileName(repoPath)}-extract-{Guid.NewGuid():N}");
            await SafeDeleteDirectoryAsync(tempExtract);
            
            // 使用 ConfigureAwait(false) 避免阻塞 UI 线程
            await Task.Run(() => ZipFile.ExtractToDirectory(zipFile, tempExtract, true), ct).ConfigureAwait(false);
            await Task.Run(() => File.Delete(zipFile), ct).ConfigureAwait(false);

            await SafeDeleteDirectoryAsync(targetDir);
            var extractedDir = Path.Combine(tempExtract, $"{Path.GetFileName(repoPath)}-{branch}");
            await Task.Run(() => CopyDirectory(extractedDir, targetDir), ct).ConfigureAwait(false);
            await SafeDeleteDirectoryAsync(tempExtract);

            _logger.LogInformation("成功下载并解压 {RepoPath} 到 {TargetDir}", repoPath, targetDir);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "下载并解压 {RepoPath} 失败", repoPath);
            return false;
        }
        finally
        {
            if (File.Exists(zipFile))
            {
                try { File.Delete(zipFile); } catch { }
            }
        }
    }

    private static void CopyDirectory(string sourceDir, string destDir)
    {
        Directory.CreateDirectory(destDir);
        foreach (var file in Directory.GetFiles(sourceDir))
            File.Copy(file, Path.Combine(destDir, Path.GetFileName(file)));
        foreach (var dir in Directory.GetDirectories(sourceDir))
            CopyDirectory(dir, Path.Combine(destDir, Path.GetFileName(dir)));
    }

    private static async Task SafeDeleteDirectoryAsync(string path)
    {
        if (!Directory.Exists(path)) return;
        foreach (var file in Directory.GetFiles(path, "*", SearchOption.AllDirectories))
        {
            try { File.SetAttributes(file, FileAttributes.Normal); } catch { }
        }
        for (int i = 0; i < 3; i++)
        {
            try { Directory.Delete(path, true); return; }
            catch (UnauthorizedAccessException) when (i < 2) { await Task.Delay(500); }
            catch (IOException) when (i < 2) { await Task.Delay(500); }
        }
    }
}
