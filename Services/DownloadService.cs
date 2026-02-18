using System;
using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using System.Linq;
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
    Task<bool> DownloadPmhqAsync(string version, IProgress<DownloadProgress>? progress = null, CancellationToken ct = default);
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
            ct,
            skipFiles: null,
            specificVersion: null);
    }

    public async Task<bool> DownloadPmhqAsync(string version, IProgress<DownloadProgress>? progress = null, CancellationToken ct = default)
    {
        _logger.LogInformation("开始下载 PMHQ 版本 {Version}...", version);
        return await DownloadAndExtractAsync(
            Constants.NpmPackages.Pmhq,
            Constants.DefaultPaths.PmhqDir,
            progress,
            ct,
            skipFiles: new[] { "package.json" },
            specificVersion: version);
    }

    public async Task<bool> DownloadLLBotAsync(IProgress<DownloadProgress>? progress = null, CancellationToken ct = default)
    {
        _logger.LogInformation("开始下载 LLBot...");
        return await DownloadAndExtractAsync(
            Constants.NpmPackages.LLBot,
            Constants.DefaultPaths.LLBotDir,
            progress,
            ct,
            skipFiles: null,
            specificVersion: null);
    }

    public async Task<bool> DownloadNodeAsync(IProgress<DownloadProgress>? progress = null, CancellationToken ct = default)
    {
        _logger.LogInformation("开始下载 Node.js...");
        return await DownloadAndExtractAsync(
            Constants.NpmPackages.Node,
            Constants.DefaultPaths.LLBotDir,
            progress,
            ct,
            skipFiles: new[] { "package.json" },
            specificVersion: null);
    }

    public async Task<bool> DownloadFFmpegAsync(IProgress<DownloadProgress>? progress = null, CancellationToken ct = default)
    {
        _logger.LogInformation("开始下载 FFmpeg...");
        return await DownloadAndExtractAsync(
            Constants.NpmPackages.FFmpeg,
            Constants.DefaultPaths.LLBotDir,
            progress,
            ct,
            skipFiles: new[] { "package.json" },
            specificVersion: null);
    }

    public async Task<bool> DownloadQQAsync(IProgress<DownloadProgress>? progress = null, CancellationToken ct = default)
    {
        try
        {
            _logger.LogInformation("开始下载 QQ...");
            progress?.Report(new DownloadProgress { Status = "正在下载 QQ..." });

            if (PlatformHelper.IsMacOS)
            {
                return await DownloadQQForMacOSAsync(progress, ct);
            }
            else if (PlatformHelper.IsWindows)
            {
                return await DownloadQQForWindowsAsync(progress, ct);
            }
            else
            {
                _logger.LogError("不支持的操作系统");
                return false;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "下载 QQ 失败");
            return false;
        }
    }

    private async Task<bool> DownloadQQForMacOSAsync(IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        var tempFile = Path.Combine(Path.GetTempPath(), "QQ-macos.zip");
        _logger.LogInformation("macOS QQ 原始下载地址: {Url}", Constants.QQDownloadUrl);
        _logger.LogInformation("临时文件: {Path}", tempFile);

        // 构建 GitHub 代理 URLs
        var downloadUrls = new[]
        {
            $"https://gh-proxy.com/{Constants.QQDownloadUrl}",
            $"https://ghproxy.net/{Constants.QQDownloadUrl}",
            $"https://mirror.ghproxy.com/{Constants.QQDownloadUrl}",
            Constants.QQDownloadUrl  // 直连作为最后的备选
        };

        _logger.LogInformation("获取到 {Count} 个下载地址（包括代理）", downloadUrls.Length);

        // 依次尝试各个下载地址
        Exception? lastException = null;
        foreach (var url in downloadUrls)
        {
            try
            {
                _logger.LogInformation("尝试下载 QQ: {Url}", url);
                progress?.Report(new DownloadProgress { Status = "正在下载 QQ..." });

                using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning("下载失败: {StatusCode} from {Url}", response.StatusCode, url);
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
                        Status = $"正在下载 QQ... {downloadedBytes / 1024 / 1024:F1} MB / {totalBytes / 1024 / 1024:F1} MB"
                    });
                }

                _logger.LogInformation("下载成功: {Url}", url);
                lastException = null;
                break;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                lastException = ex;
                _logger.LogWarning(ex, "从 {Url} 下载失败，尝试下一个镜像源", url);
            }
        }

        if (lastException != null)
        {
            _logger.LogError(lastException, "所有下载源都无法下载 macOS QQ");
            return false;
        }

        progress?.Report(new DownloadProgress { Status = "正在解压 QQ..." });
        _logger.LogInformation("QQ 下载完成，开始解压...");

        // 确保目录存在
        var qqDir = Constants.DefaultPaths.QQDir;
        if (Directory.Exists(qqDir))
        {
            Directory.Delete(qqDir, true);
        }
        Directory.CreateDirectory(qqDir);

        // 解压 zip 文件
        System.IO.Compression.ZipFile.ExtractToDirectory(tempFile, qqDir, true);

        try { File.Delete(tempFile); } catch { }

        // 给 QQ 可执行文件添加执行权限
        var qqExe = Constants.DefaultPaths.QQExe;
        if (File.Exists(qqExe))
        {
            var chmod = new System.Diagnostics.Process
            {
                StartInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "chmod",
                    Arguments = $"+x \"{qqExe}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            chmod.Start();
            await chmod.WaitForExitAsync(ct);

            _logger.LogInformation("macOS QQ 安装完成");
            return true;
        }
        else
        {
            _logger.LogError("QQ 可执行文件不存在: {Path}", qqExe);
            return false;
        }
    }

    private async Task<bool> DownloadQQForWindowsAsync(IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        var tempFile = Path.Combine(Path.GetTempPath(), "QQ_Setup.exe");
        _logger.LogInformation("Windows QQ 下载地址: {Url}", Constants.QQDownloadUrl);
        _logger.LogInformation("临时文件: {Path}", tempFile);

        using (var response = await _httpClient.GetAsync(Constants.QQDownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            response.EnsureSuccessStatusCode();

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

        _logger.LogInformation("Windows QQ 安装完成");
        return true;
    }

    public async Task<AppUpdateResult> DownloadAppUpdateAsync(IProgress<DownloadProgress>? progress = null, CancellationToken ct = default)
    {
        try
        {
            progress?.Report(new DownloadProgress { Status = "正在获取下载地址..." });

            var packageInfo = await _npmClient.GetPackageInfoAsync(Constants.NpmPackages.App, specificVersion: null, ct);
            if (packageInfo == null || string.IsNullOrEmpty(packageInfo.TarballUrl))
            {
                return new AppUpdateResult { Success = false, Error = "无法获取下载地址" };
            }

            _logger.LogInformation("NPM包: {Package}, 版本: {Version}, 来源: {Registry}",
                packageInfo.Name, packageInfo.Version, packageInfo.Registry);
            _logger.LogInformation("原始tarball地址: {Url}", packageInfo.TarballUrl);

            var tarballUrls = _npmClient.GetTarballUrls(packageInfo.TarballUrl);
            _logger.LogInformation("获取到 {Count} 个镜像下载地址: {Urls}",
                tarballUrls.Length, string.Join(" | ", tarballUrls));

            var tempDir = Path.Combine(Path.GetTempPath(), $"app_update_{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDir);
            _logger.LogInformation("临时目录: {TempDir}", tempDir);

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

            // 列出解压后的文件
            var extractedFiles = Directory.GetFileSystemEntries(tempDir);
            _logger.LogInformation("解压后 tempDir 内容 ({Count} 项):", extractedFiles.Length);
            foreach (var entry in extractedFiles)
            {
                var info = new FileInfo(entry);
                _logger.LogInformation("  {Name} ({Size} bytes)", Path.GetFileName(entry),
                    info.Exists ? info.Length : -1);
            }

            string? newExePath = null;

            // macOS: 查找 .app 目录
            if (PlatformHelper.IsMacOS)
            {
                var appBundles = Directory.GetDirectories(tempDir, "*.app");
                if (appBundles.Length > 0)
                {
                    newExePath = appBundles[0];
                    _logger.LogInformation("找到 .app bundle: {Path}", newExePath);
                }
                else
                {
                    // 深度搜索子目录
                    var deepSearch = Directory.GetDirectories(tempDir, "*.app", SearchOption.AllDirectories);
                    _logger.LogWarning("顶层未找到 .app bundle，深度搜索结果: {Count} 个", deepSearch.Length);
                    if (deepSearch.Length > 0)
                        newExePath = deepSearch[0];
                }
            }
            else
            {
                // Windows/Linux: 查找可执行文件
                var executablePattern = "*" + PlatformHelper.ExecutableExtension;
                foreach (var file in Directory.GetFiles(tempDir, executablePattern))
                {
                    newExePath = file;
                    break;
                }

                if (string.IsNullOrEmpty(newExePath))
                {
                    // 也搜索子目录，以防 package/ 没被正确展开
                    var deepSearch = Directory.GetFiles(tempDir, executablePattern, SearchOption.AllDirectories);
                    _logger.LogWarning("顶层未找到可执行文件，深度搜索结果: {Files}",
                        string.Join(", ", deepSearch.Select(Path.GetFileName)));
                    if (deepSearch.Length > 0)
                        newExePath = deepSearch[0];
                }
            }

            if (string.IsNullOrEmpty(newExePath))
            {
                Directory.Delete(tempDir, true);
                return new AppUpdateResult { Success = false, Error = "更新包中未找到可执行文件" };
            }

            if (PlatformHelper.IsMacOS && Directory.Exists(newExePath))
            {
                _logger.LogInformation("找到新版本 .app: {Path}", newExePath);
            }
            else
            {
                _logger.LogInformation("找到新版本exe: {Path} ({Size} bytes)",
                    newExePath, new FileInfo(newExePath).Length);
            }

            var currentExe = GetCurrentExePath();
            if (string.IsNullOrEmpty(currentExe))
            {
                Directory.Delete(tempDir, true);
                return new AppUpdateResult { Success = false, Error = "无法获取当前程序路径" };
            }
            _logger.LogInformation("当前程序路径: {CurrentExe}, 新版本路径: {NewExe}", currentExe, newExePath);

            var currentPid = Environment.ProcessId;
            _logger.LogInformation("当前PID: {Pid}, tempDir: {TempDir}", currentPid, tempDir);

            var scriptPath = CreateUpdateScript(newExePath, currentExe, currentPid, tempDir);

            _logger.LogInformation("更新脚本已生成: {Script}", scriptPath);

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
        if (PlatformHelper.IsWindows)
        {
            return CreateWindowsUpdateScript(newExePath, currentExePath, currentPid, tempDir);
        }
        else
        {
            return CreateUnixUpdateScript(newExePath, currentExePath, currentPid, tempDir);
        }
    }

    private string CreateWindowsUpdateScript(string newExePath, string currentExePath, int currentPid, string tempDir)
    {
        var currentDir = Path.GetDirectoryName(currentExePath)!;
        var currentExeName = Path.GetFileName(currentExePath);
        var scriptPath = Path.Combine(tempDir, "_update.bat");
        var logFile = Path.Combine(currentDir, "_update.log");

        var scriptContent = $"""
            @echo off
            chcp 65001 >nul

            set "CURRENT_DIR={currentDir}"
            set "CURRENT_EXE={currentExePath}"
            set "NEW_EXE={newExePath}"
            set "CURRENT_EXE_NAME={currentExeName}"
            set "TEMP_DIR={tempDir}"
            set "LOG_FILE={logFile}"

            cd /d "%CURRENT_DIR%"
            echo [%date% %time%] ====== 开始更新 ====== >> "%LOG_FILE%"
            echo [%date% %time%] PID={currentPid} >> "%LOG_FILE%"
            echo [%date% %time%] CURRENT_EXE=%CURRENT_EXE% >> "%LOG_FILE%"
            echo [%date% %time%] NEW_EXE=%NEW_EXE% >> "%LOG_FILE%"
            echo [%date% %time%] TEMP_DIR=%TEMP_DIR% >> "%LOG_FILE%"
            echo 正在更新应用程序，请稍候...
            echo.

            if not exist "%NEW_EXE%" (
                echo [%date% %time%] 错误: 新版本文件不存在 >> "%LOG_FILE%"
                echo 错误: 新版本文件不存在
                pause
                exit /b 1
            )
            echo [%date% %time%] 新版本文件存在，大小: >> "%LOG_FILE%"
            for %%A in ("%NEW_EXE%") do echo [%date% %time%]   %%~zA bytes >> "%LOG_FILE%"

            set count=0
            :wait_loop
            tasklist /FI "PID eq {currentPid}" 2>NUL | find /I "{currentPid}" >NUL
            if errorlevel 1 goto do_update
            set /a count=%count%+1
            if %count% geq 20 (
                echo [%date% %time%] 等待超时，强制终止 >> "%LOG_FILE%"
                echo 等待超时，尝试强制终止进程...
                taskkill /F /PID {currentPid} 2>NUL
                timeout /t 1 /nobreak >nul
                goto do_update
            )
            echo 等待程序退出... %count%/20
            timeout /t 1 /nobreak >nul
            goto wait_loop

            :do_update
            echo [%date% %time%] 程序已退出，开始更新 >> "%LOG_FILE%"

            echo 正在备份旧版本...
            if exist "%CURRENT_EXE%.bak" del /f /q "%CURRENT_EXE%.bak"
            if exist "%CURRENT_EXE%" (
                move /y "%CURRENT_EXE%" "%CURRENT_EXE%.bak"
                echo [%date% %time%] 备份完成 >> "%LOG_FILE%"
            ) else (
                echo [%date% %time%] 旧版本不存在，跳过备份 >> "%LOG_FILE%"
            )

            echo 正在安装新版本...
            copy /y "%NEW_EXE%" "%CURRENT_EXE%"

            if errorlevel 1 (
                echo [%date% %time%] copy 失败，errorlevel=%errorlevel% >> "%LOG_FILE%"
                echo 更新失败，正在恢复旧版本...
                if exist "%CURRENT_EXE%.bak" move /y "%CURRENT_EXE%.bak" "%CURRENT_EXE%"
                pause
                exit /b 1
            )

            echo [%date% %time%] copy 成功 >> "%LOG_FILE%"

            if not exist "%CURRENT_EXE%" (
                echo [%date% %time%] 错误: copy 后目标文件不存在 >> "%LOG_FILE%"
                echo 更新失败，文件未写入
                if exist "%CURRENT_EXE%.bak" move /y "%CURRENT_EXE%.bak" "%CURRENT_EXE%"
                pause
                exit /b 1
            )

            for %%A in ("%CURRENT_EXE%") do echo [%date% %time%] 新文件大小: %%~zA bytes >> "%LOG_FILE%"

            echo.
            echo 更新完成！正在启动新版本...
            timeout /t 2 /nobreak >nul

            cd /d "%CURRENT_DIR%"
            echo [%date% %time%] 启动: %CURRENT_EXE_NAME% >> "%LOG_FILE%"
            start "" "%CURRENT_EXE_NAME%"

            if errorlevel 1 (
                echo [%date% %time%] start 失败，errorlevel=%errorlevel% >> "%LOG_FILE%"
                pause
                exit /b 1
            )

            echo [%date% %time%] 启动命令已执行 >> "%LOG_FILE%"
            echo [%date% %time%] ====== 更新完成 ====== >> "%LOG_FILE%"

            timeout /t 3 /nobreak >nul
            (goto) 2>nul & rmdir /s /q "%TEMP_DIR%" 2>nul
            """;

        File.WriteAllText(scriptPath, scriptContent, new System.Text.UTF8Encoding(false));
        _logger.LogInformation("更新脚本已生成: {Path}, 日志文件: {LogFile}", scriptPath, logFile);
        return scriptPath;
    }

    private string CreateUnixUpdateScript(string newExePath, string currentExePath, int currentPid, string tempDir)
    {
        var currentDir = Path.GetDirectoryName(currentExePath)!;
        var currentExeName = Path.GetFileName(currentExePath);
        var scriptPath = Path.Combine(tempDir, "_update.sh");
        // 日志文件放在临时目录，避免备份后原目录不存在
        var logFile = Path.Combine(tempDir, "_update.log");

        // macOS 上如果是 .app，需要处理整个 .app bundle
        var isAppBundle = currentExePath.Contains(".app/Contents/MacOS/");
        string targetPath, sourcePath;

        if (isAppBundle)
        {
            // 提取当前 .app 路径
            var appIndex = currentExePath.IndexOf(".app/Contents/MacOS/");
            var currentAppPath = currentExePath[..(appIndex + 4)]; // 包含 .app

            // 新版本 newExePath 就是 .app 本身（因为前面的代码找到的是 .app 目录）
            targetPath = currentAppPath;
            sourcePath = newExePath;  // newExePath 已经是 .app 路径了
        }
        else
        {
            targetPath = currentExePath;
            sourcePath = newExePath;
        }

        var scriptContent = $$"""
            #!/bin/bash
            set -e

            CURRENT_DIR="{{currentDir}}"
            CURRENT_EXE="{{currentExePath}}"
            NEW_EXE="{{newExePath}}"
            TARGET_PATH="{{targetPath}}"
            SOURCE_PATH="{{sourcePath}}"
            TEMP_DIR="{{tempDir}}"
            LOG_FILE="{{logFile}}"
            PID={{currentPid}}

            log() {
                echo "[$(date '+%Y-%m-%d %H:%M:%S')] $1" | tee -a "$LOG_FILE"
            }

            log "====== 开始更新 ======"
            log "PID=$PID"
            log "CURRENT_EXE=$CURRENT_EXE"
            log "NEW_EXE=$NEW_EXE"
            log "TARGET_PATH=$TARGET_PATH"
            log "SOURCE_PATH=$SOURCE_PATH"
            log "TEMP_DIR=$TEMP_DIR"

            if [ ! -e "$SOURCE_PATH" ]; then
                log "错误: 新版本文件不存在"
                exit 1
            fi

            log "新版本文件存在，大小: $(du -h "$SOURCE_PATH" | cut -f1)"

            # 等待进程退出
            log "等待程序退出..."
            count=0
            while kill -0 $PID 2>/dev/null; do
                count=$((count + 1))
                if [ $count -ge 20 ]; then
                    log "等待超时，强制终止"
                    kill -9 $PID 2>/dev/null || true
                    sleep 1
                    break
                fi
                echo "等待程序退出... $count/20"
                sleep 1
            done

            log "程序已退出，开始更新"

            # 备份旧版本
            if [ -e "$TARGET_PATH" ]; then
                log "备份旧版本..."
                rm -rf "$TARGET_PATH.bak" 2>/dev/null || true
                mv "$TARGET_PATH" "$TARGET_PATH.bak"
                log "备份完成"
            else
                log "旧版本不存在，跳过备份"
            fi

            # 安装新版本
            log "正在安装新版本..."
            if [ -d "$SOURCE_PATH" ]; then
                # 复制目录（如 .app bundle）
                cp -R "$SOURCE_PATH" "$TARGET_PATH"
            else
                # 复制单个文件
                cp "$SOURCE_PATH" "$TARGET_PATH"
                chmod +x "$TARGET_PATH"
            fi

            if [ ! -e "$TARGET_PATH" ]; then
                log "错误: 复制后目标文件不存在"
                if [ -e "$TARGET_PATH.bak" ]; then
                    log "恢复旧版本..."
                    mv "$TARGET_PATH.bak" "$TARGET_PATH"
                fi
                exit 1
            fi

            log "复制成功，大小: $(du -h "$TARGET_PATH" | cut -f1)"

            # 启动新版本
            log "更新完成！正在启动新版本..."
            sleep 2

            cd "$CURRENT_DIR"
            log "启动: $CURRENT_EXE"

            if [ -d "$TARGET_PATH" ]; then
                # .app bundle - 使用 open 命令
                open "$TARGET_PATH" &
            else
                # 单个可执行文件
                nohup "$CURRENT_EXE" > /dev/null 2>&1 &
            fi

            log "启动命令已执行"
            log "====== 更新完成 ======"

            sleep 3
            rm -rf "$TEMP_DIR" 2>/dev/null || true
            """;

        File.WriteAllText(scriptPath, scriptContent, new System.Text.UTF8Encoding(false));

        // 添加执行权限
        if (PlatformHelper.IsMacOS || PlatformHelper.IsLinux)
        {
            var chmod = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "chmod",
                Arguments = $"+x \"{scriptPath}\"",
                UseShellExecute = false,
                CreateNoWindow = true
            });
            chmod?.WaitForExit();
        }

        _logger.LogInformation("更新脚本已生成: {Path}, 日志文件: {LogFile}", scriptPath, logFile);
        return scriptPath;
    }


    /// <summary>
    /// 获取当前程序的真实 exe 路径。
    /// PublishSingleFile 发布后 Environment.ProcessPath 返回正确路径；
    /// dotnet run 开发环境下返回 dotnet.exe，此时回退到 AppContext.BaseDirectory + AssemblyName。
    /// </summary>
    private static string? GetCurrentExePath()
    {
        var processPath = Environment.ProcessPath;

        // 检测是否在 dotnet run 环境下（ProcessPath 指向 dotnet.exe）
        if (!string.IsNullOrEmpty(processPath) &&
            !Path.GetFileNameWithoutExtension(processPath)
                 .Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            return processPath;
        }

        // 回退：用 BaseDirectory + 程序集名称拼出 exe 路径
        var assemblyName = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Name;
        if (string.IsNullOrEmpty(assemblyName)) return null;

        var exePath = Path.Combine(AppContext.BaseDirectory, assemblyName + PlatformHelper.ExecutableExtension);
        return File.Exists(exePath) ? exePath : null;
    }


    private async Task<bool> DownloadAndExtractAsync(
        string packageName,
        string extractDir,
        IProgress<DownloadProgress>? progress,
        CancellationToken ct,
        string[]? skipFiles = null,
        string? specificVersion = null)
    {
        try
        {
            _logger.LogInformation("开始下载包: {Package}", packageName);
            progress?.Report(new DownloadProgress { Status = "正在获取下载地址..." });

            var packageInfo = await _npmClient.GetPackageInfoAsync(packageName, specificVersion, ct);
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
