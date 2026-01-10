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
/// 应用更新结果
/// </summary>
public class AppUpdateResult
{
    public bool Success { get; set; }
    public string? NewExePath { get; set; }
    public string? UpdateScriptPath { get; set; }
    public string? Error { get; set; }
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
    Task<AppUpdateResult> DownloadAppUpdateAsync(IProgress<DownloadProgress>? progress = null, CancellationToken ct = default);
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
        
        // 设置 User-Agent 提高下载速度
        _httpClient.DefaultRequestHeaders.Add("User-Agent", 
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
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
        _logger.LogInformation("开始下载 PMHQ...");
        return await DownloadAndExtractAsync(
            Constants.NpmPackages.Pmhq,
            Constants.DefaultPaths.PmhqDir,
            progress,
            ct);
    }

    public async Task<bool> DownloadLLBotAsync(IProgress<DownloadProgress>? progress = null, CancellationToken ct = default)
    {
        _logger.LogInformation("开始下载 LLBot...");
        return await DownloadAndExtractAsync(
            Constants.NpmPackages.LLBot,
            Constants.DefaultPaths.LLBotDir,
            progress,
            ct);
    }

    public async Task<bool> DownloadNodeAsync(IProgress<DownloadProgress>? progress = null, CancellationToken ct = default)
    {
        _logger.LogInformation("开始下载 Node.js...");
        return await DownloadAndExtractAsync(
            Constants.NpmPackages.Node,
            Constants.DefaultPaths.LLBotDir,
            progress,
            ct,
            skipFiles: new[] { "package.json" });
    }

    public async Task<bool> DownloadFFmpegAsync(IProgress<DownloadProgress>? progress = null, CancellationToken ct = default)
    {
        _logger.LogInformation("开始下载 FFmpeg...");
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
            _logger.LogInformation("开始下载 QQ 安装包...");
            progress?.Report(new DownloadProgress { Status = "正在下载 QQ 安装包..." });

            var tempFile = Path.Combine(Path.GetTempPath(), "QQ_Setup.exe");
            _logger.LogInformation("下载地址: {Url}", Constants.QQDownloadUrl);
            _logger.LogInformation("临时文件: {Path}", tempFile);

            using (var response = await _httpClient.GetAsync(Constants.QQDownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                response.EnsureSuccessStatusCode();

                var totalBytes = response.Content.Headers.ContentLength ?? 0;
                var downloadedBytes = 0L;

                await using var contentStream = await response.Content.ReadAsStreamAsync(ct);
                await using var fileStream = new FileStream(tempFile, FileMode.Create, FileAccess.Write, FileShare.None, 65536, true);

                var buffer = new byte[65536]; // 64KB chunk 提高下载速度
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
            _logger.LogInformation("QQ 下载完成，开始安装...");

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

    public async Task<AppUpdateResult> DownloadAppUpdateAsync(IProgress<DownloadProgress>? progress = null, CancellationToken ct = default)
    {
        try
        {
            progress?.Report(new DownloadProgress { Status = "正在获取下载地址..." });

            var packageInfo = await _npmClient.GetPackageInfoAsync(Constants.NpmPackages.App, ct);
            if (packageInfo == null || string.IsNullOrEmpty(packageInfo.TarballUrl))
            {
                return new AppUpdateResult { Success = false, Error = "无法获取下载地址" };
            }

            var tarballUrls = _npmClient.GetTarballUrls(packageInfo.TarballUrl);
            _logger.LogInformation("获取到 {Count} 个下载地址", tarballUrls.Length);

            var tempDir = Path.Combine(Path.GetTempPath(), $"app_update_{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDir);

            var tempFile = Path.Combine(tempDir, "update.tgz");

            // 依次尝试各个镜像源
            Exception? lastException = null;
            foreach (var tarballUrl in tarballUrls)
            {
                try
                {
                    _logger.LogInformation("尝试下载应用更新: {Url}", tarballUrl);
                    progress?.Report(new DownloadProgress { Status = "正在下载更新..." });

                    using var response = await _httpClient.GetAsync(tarballUrl, HttpCompletionOption.ResponseHeadersRead, ct);
                    
                    if (!response.IsSuccessStatusCode)
                    {
                        _logger.LogWarning("下载失败: {StatusCode} from {Url}", response.StatusCode, tarballUrl);
                        continue;
                    }

                    var totalBytes = response.Content.Headers.ContentLength ?? 0;
                    var downloadedBytes = 0L;

                    await using var contentStream = await response.Content.ReadAsStreamAsync(ct);
                    await using var fileStream = new FileStream(tempFile, FileMode.Create, FileAccess.Write, FileShare.None, 65536, true);

                    var buffer = new byte[65536];
                    int bytesRead;

                    while ((bytesRead = await contentStream.ReadAsync(buffer, ct)) > 0)
                    {
                        await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), ct);
                        downloadedBytes += bytesRead;

                        progress?.Report(new DownloadProgress
                        {
                            Downloaded = downloadedBytes,
                            Total = totalBytes,
                            Status = $"正在下载更新... {downloadedBytes / 1024 / 1024:F1} MB / {totalBytes / 1024 / 1024:F1} MB"
                        });
                    }

                    _logger.LogInformation("下载成功: {Url}", tarballUrl);
                    lastException = null;
                    break;
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
                {
                    lastException = ex;
                    _logger.LogWarning(ex, "从 {Url} 下载失败，尝试下一个镜像源", tarballUrl);
                }
            }

            if (lastException != null)
            {
                Directory.Delete(tempDir, true);
                return new AppUpdateResult { Success = false, Error = "所有下载源都失败" };
            }

            progress?.Report(new DownloadProgress { Status = "正在解压..." });

            // 解压
            await ExtractTarGzAsync(tempFile, tempDir, null, ct);

            // 删除临时 tgz 文件
            if (File.Exists(tempFile)) File.Delete(tempFile);

            // 查找 exe 文件
            string? newExePath = null;
            foreach (var file in Directory.GetFiles(tempDir, "*.exe"))
            {
                newExePath = file;
                break;
            }

            if (string.IsNullOrEmpty(newExePath))
            {
                Directory.Delete(tempDir, true);
                return new AppUpdateResult { Success = false, Error = "更新包中未找到可执行文件" };
            }

            // 生成更新脚本
            var currentExe = Environment.ProcessPath ?? System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
            if (string.IsNullOrEmpty(currentExe))
            {
                Directory.Delete(tempDir, true);
                return new AppUpdateResult { Success = false, Error = "无法获取当前程序路径" };
            }

            var currentPid = Environment.ProcessId;
            var scriptPath = CreateUpdateScript(newExePath, currentExe, currentPid, tempDir);

            _logger.LogInformation("应用更新下载完成，更新脚本: {Script}", scriptPath);

            return new AppUpdateResult
            {
                Success = true,
                NewExePath = newExePath,
                UpdateScriptPath = scriptPath
            };
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("应用更新下载已取消");
            return new AppUpdateResult { Success = false, Error = "下载已取消" };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "下载应用更新失败");
            return new AppUpdateResult { Success = false, Error = ex.Message };
        }
    }

    private string CreateUpdateScript(string newExePath, string currentExePath, int currentPid, string tempDir)
    {
        var currentDir = Path.GetDirectoryName(currentExePath)!;
        var currentExeName = Path.GetFileName(currentExePath);
        var scriptPath = Path.Combine(tempDir, "_update.bat");

        var scriptContent = $"""
            @echo off
            chcp 65001 >nul

            cd /d "{currentDir}"
            echo 正在更新应用程序，请稍候...
            echo.

            :: 等待原程序退出
            set count=0
            :wait_loop
            tasklist /FI "PID eq {currentPid}" 2>NUL | find /I "{currentPid}" >NUL
            if errorlevel 1 goto do_update
            set /a count=%count%+1
            if %count% geq 20 (
                echo 等待超时，尝试强制终止进程...
                taskkill /F /PID {currentPid} 2>NUL
                timeout /t 1 /nobreak >nul
                goto do_update
            )
            echo 等待程序退出... %count%/20
            timeout /t 1 /nobreak >nul
            goto wait_loop

            :do_update
            echo 程序已退出，开始更新...

            echo 正在备份旧版本...
            if exist "{currentExePath}.bak" del /f /q "{currentExePath}.bak"
            if exist "{currentExePath}" move /y "{currentExePath}" "{currentExePath}.bak"

            echo 正在安装新版本...
            copy /y "{newExePath}" "{currentExePath}"

            if errorlevel 1 (
                echo 更新失败，正在恢复旧版本...
                if exist "{currentExePath}.bak" move /y "{currentExePath}.bak" "{currentExePath}"
                pause
                exit /b 1
            )

            echo.
            echo 更新完成！正在启动新版本...
            timeout /t 2 /nobreak >nul

            cd /d "{currentDir}"
            start "" "{currentExeName}"

            :: 清理临时文件
            start /b "" cmd /c "timeout /t 5 /nobreak >nul & rmdir /s /q "{tempDir}" 2>nul"
            exit
            """;

        File.WriteAllText(scriptPath, scriptContent, System.Text.Encoding.UTF8);
        return scriptPath;
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
            _logger.LogInformation("开始下载包: {Package}", packageName);
            progress?.Report(new DownloadProgress { Status = "正在获取下载地址..." });

            var packageInfo = await _npmClient.GetPackageInfoAsync(packageName, ct);
            if (packageInfo == null || string.IsNullOrEmpty(packageInfo.TarballUrl))
            {
                _logger.LogError("无法获取 {Package} 的下载地址", packageName);
                return false;
            }

            // 获取所有可用的下载地址（镜像源优先）
            var tarballUrls = _npmClient.GetTarballUrls(packageInfo.TarballUrl);
            _logger.LogInformation("获取到 {Count} 个下载地址", tarballUrls.Length);

            // 确保目录存在
            Directory.CreateDirectory(extractDir);

            var tempFile = Path.Combine(extractDir, "temp_download.tgz");

            // 依次尝试各个镜像源
            Exception? lastException = null;
            foreach (var tarballUrl in tarballUrls)
            {
                try
                {
                    _logger.LogInformation("尝试下载: {Url}", tarballUrl);
                    progress?.Report(new DownloadProgress { Status = "正在下载..." });

                    using var response = await _httpClient.GetAsync(tarballUrl, HttpCompletionOption.ResponseHeadersRead, ct);
                    
                    if (!response.IsSuccessStatusCode)
                    {
                        _logger.LogWarning("下载失败: {StatusCode} from {Url}", response.StatusCode, tarballUrl);
                        continue;
                    }

                    var totalBytes = response.Content.Headers.ContentLength ?? 0;
                    var downloadedBytes = 0L;

                    await using var contentStream = await response.Content.ReadAsStreamAsync(ct);
                    await using var fileStream = new FileStream(tempFile, FileMode.Create, FileAccess.Write, FileShare.None, 65536, true);

                    var buffer = new byte[65536];
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

                    // 下载成功，跳出循环
                    _logger.LogInformation("下载成功: {Url}", tarballUrl);
                    lastException = null;
                    break;
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
                {
                    lastException = ex;
                    _logger.LogWarning(ex, "从 {Url} 下载失败，尝试下一个镜像源", tarballUrl);
                }
            }

            if (lastException != null)
            {
                _logger.LogError(lastException, "所有镜像源都无法下载: {Package}", packageName);
                progress?.Report(new DownloadProgress { Status = "所有下载源都失败" });
                return false;
            }

            progress?.Report(new DownloadProgress { Status = "正在解压..." });
            _logger.LogInformation("下载完成，开始解压到: {Dir}", extractDir);

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
