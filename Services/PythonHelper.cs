using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using LuckyLilliaDesktop.Utils;

namespace LuckyLilliaDesktop.Services;

public interface IPythonHelper
{
    string PythonDir { get; }
    string PythonExe { get; }
    bool IsInstalled { get; }
    Task<bool> EnsureInstalledAsync(Action<long, long>? onProgress = null, CancellationToken ct = default);
    Task<bool> CreateVenvAsync(string targetDir, Action<string>? onOutput = null, CancellationToken ct = default);
    Task<bool> InstallRequirementsAsync(string venvDir, string requirementsFile, Action<string>? onOutput = null, CancellationToken ct = default);
    Task<bool> UvInstallRequirementsAsync(string targetDir, string requirementsFile, Action<string>? onOutput = null, CancellationToken ct = default);
    Task RunCommandAsync(string exe, string args, string workDir, Action<string>? onOutput = null, CancellationToken ct = default);
}

public class PythonHelper : IPythonHelper
{
    private readonly ILogger<PythonHelper> _logger;
    private readonly HttpClient _httpClient;

    private const string PythonDownloadUrl = "https://registry.npmmirror.com/-/binary/python/3.11.9/python-3.11.9-embed-amd64.zip";
    private const string GetPipUrl = "https://bootstrap.pypa.io/get-pip.py";
    private const string PipMirror = "https://pypi.tuna.tsinghua.edu.cn/simple";

    private static readonly string[] GhProxies = [
        "https://gh-proxy.com/",
        "https://ghproxy.net/",
        "https://mirror.ghproxy.com/"
    ];

    public string PythonDir => "bin/python";
    public string PythonExe => Path.GetFullPath(Path.Combine(PythonDir, "python" + PlatformHelper.ExecutableExtension));
    public bool IsInstalled
    {
        get
        {
            // Windows: 需要嵌入式 Python + uv（uv 可能位于 bin/uv 或 PythonDir/Scripts）
            if (PlatformHelper.IsWindows)
            {
                if (!File.Exists(PythonExe))
                    return false;

                return File.Exists(Path.GetFullPath("bin/uv/uv.exe")) ||
                       File.Exists(Path.Combine(PythonDir, "Scripts", "uv.exe")) ||
                       File.Exists(Path.Combine(PythonDir, "uv.exe"));
            }

            // macOS/Linux: 使用 bin/uv/uv
            return File.Exists(Path.GetFullPath("bin/uv/uv"));
        }
    }

    public PythonHelper(ILogger<PythonHelper> logger)
    {
        _logger = logger;
        _httpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        _httpClient.DefaultRequestHeaders.Add("User-Agent", "LuckyLilliaDesktop");
    }

    public async Task<bool> EnsureInstalledAsync(Action<long, long>? onProgress = null, CancellationToken ct = default)
    {
        if (IsInstalled) return true;

        try
        {
            if (PlatformHelper.IsMacOS || PlatformHelper.IsLinux)
            {
                // macOS/Linux: 使用系统 Python3
                return await SetupSystemPython(ct);
            }
            else
            {
                // Windows: 下载嵌入式 Python
                return await SetupWindowsPython(onProgress, ct);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Python 安装失败");
            return false;
        }
    }

    private async Task<bool> EnsureUvInstalledAsync(CancellationToken ct, Action<string>? onOutput = null)
    {
        var uvDir = "bin/uv";
        var uvExeName = PlatformHelper.IsWindows ? "uv.exe" : "uv";
        var uvExePath = Path.Combine(uvDir, uvExeName);

        // 如果 uv 已经存在，直接返回
        if (File.Exists(uvExePath))
        {
            _logger.LogInformation("uv 已安装: {Path}", uvExePath);
            onOutput?.Invoke($"uv 已安装: {Path.GetFullPath(uvExePath)}");
            return true;
        }

        try
        {
            _logger.LogInformation("开始下载安装 uv...");
            onOutput?.Invoke("开始下载安装 uv...");
            Directory.CreateDirectory(uvDir);

            // 确定架构与平台
            var uvArch = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "aarch64" : "x86_64";

            string uvFileName;
            if (PlatformHelper.IsWindows)
            {
                // uv Windows release: uv-x86_64-pc-windows-msvc.zip / uv-aarch64-pc-windows-msvc.zip
                uvFileName = $"uv-{uvArch}-pc-windows-msvc.zip";
            }
            else
            {
                var uvPlatform = PlatformHelper.IsMacOS ? "apple-darwin" : "unknown-linux-gnu";
                uvFileName = $"uv-{uvArch}-{uvPlatform}.tar.gz";
            }

            var uvUrl = $"https://github.com/astral-sh/uv/releases/latest/download/{uvFileName}";
            string[] uvDownloadUrls = GetGitHubUrlsWithProxy(uvUrl);

            var tempDownload = Path.Combine(Path.GetTempPath(), PlatformHelper.IsWindows ? "uv.zip" : "uv.tar.gz");
            bool downloadSuccess = false;

            foreach (var url in uvDownloadUrls)
            {
                try
                {
                    _logger.LogInformation("尝试下载 uv: {Url}", url);
                    onOutput?.Invoke($"尝试下载 uv: {url}");
                    using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);

                    if (!response.IsSuccessStatusCode)
                    {
                        _logger.LogWarning("下载失败: {StatusCode}", response.StatusCode);
                        onOutput?.Invoke($"下载失败: {response.StatusCode}");
                        continue;
                    }

                    await using var contentStream = await response.Content.ReadAsStreamAsync(ct);
                    await using var fileStream = new FileStream(tempDownload, FileMode.Create, FileAccess.Write, FileShare.None);
                    await contentStream.CopyToAsync(fileStream, ct);

                    downloadSuccess = true;
                    _logger.LogInformation("uv 下载成功");
                    onOutput?.Invoke("uv 下载成功，正在处理...");
                    break;
                }
                catch (OperationCanceledException)
                {
                    _logger.LogInformation("下载 uv 被取消");
                    onOutput?.Invoke("下载 uv 被取消");
                    throw;
                }
                catch (HttpRequestException ex)
                {
                    _logger.LogWarning(ex, "从 {Url} 下载 uv 网络错误", url);
                    onOutput?.Invoke($"下载网络错误: {ex.Message}");
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "从 {Url} 下载 uv 失败", url);
                    onOutput?.Invoke($"下载失败: {ex.Message}");
                }
            }

            if (!downloadSuccess)
            {
                _logger.LogError("无法下载 uv");
                onOutput?.Invoke("无法下载 uv");
                return false;
            }

            var tempExtractDir = Path.Combine(Path.GetTempPath(), "uv-extract");
            if (Directory.Exists(tempExtractDir))
                Directory.Delete(tempExtractDir, true);
            Directory.CreateDirectory(tempExtractDir);

            if (PlatformHelper.IsWindows)
            {
                onOutput?.Invoke("正在解压 uv...");
                // 解压 zip
                ZipFile.ExtractToDirectory(tempDownload, tempExtractDir, true);

                // zip 内通常包含 uv.exe/uvx.exe
                var candidate1 = Path.Combine(tempExtractDir, "uv.exe");
                var candidate2 = Path.Combine(tempExtractDir, "uvx.exe");
                string? uvSource = File.Exists(candidate1) ? candidate1 : null;

                if (uvSource == null)
                {
                    // 兼容未来 zip 结构变化：递归查找 uv.exe
                    foreach (var file in Directory.EnumerateFiles(tempExtractDir, "uv.exe", SearchOption.AllDirectories))
                    {
                        uvSource = file;
                        break;
                    }
                }

                if (string.IsNullOrEmpty(uvSource) || !File.Exists(uvSource))
                {
                    _logger.LogError("找不到 uv.exe，解压目录: {Path}", tempExtractDir);
                    onOutput?.Invoke($"找不到 uv.exe，解压目录: {tempExtractDir}");
                    return false;
                }

                File.Copy(uvSource, uvExePath, true);

                // 清理临时文件
                File.Delete(tempDownload);
                Directory.Delete(tempExtractDir, true);

                _logger.LogInformation("uv 安装完成: {Path}", Path.GetFullPath(uvExePath));
                onOutput?.Invoke($"uv 安装完成: {Path.GetFullPath(uvExePath)}");
                return true;
            }
            else
            {
                onOutput?.Invoke("正在解压 uv...");
                // macOS/Linux: 解 tar.gz（依赖系统 tar）
                var tarPsi = new ProcessStartInfo
                {
                    FileName = "tar",
                    Arguments = $"-xzf \"{tempDownload}\" -C \"{tempExtractDir}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var tarProc = Process.Start(tarPsi);
                await tarProc!.WaitForExitAsync(ct);

                if (tarProc.ExitCode != 0)
                {
                    _logger.LogError("解压 uv 失败");
                    onOutput?.Invoke("解压 uv 失败");
                    return false;
                }

                var uvPlatform = PlatformHelper.IsMacOS ? "apple-darwin" : "unknown-linux-gnu";
                var uvExeSource = Path.Combine(tempExtractDir, $"uv-{uvArch}-{uvPlatform}", "uv");
                if (!File.Exists(uvExeSource))
                {
                    _logger.LogError("找不到 uv 可执行文件: {Path}", uvExeSource);
                    onOutput?.Invoke($"找不到 uv 可执行文件: {uvExeSource}");
                    return false;
                }

                File.Copy(uvExeSource, uvExePath, true);

                var chmodPsi = new ProcessStartInfo
                {
                    FileName = "chmod",
                    Arguments = $"+x \"{uvExePath}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var chmodProc = Process.Start(chmodPsi);
                await chmodProc!.WaitForExitAsync(ct);

                File.Delete(tempDownload);
                Directory.Delete(tempExtractDir, true);

                _logger.LogInformation("uv 安装完成: {Path}", Path.GetFullPath(uvExePath));
                onOutput?.Invoke($"uv 安装完成: {Path.GetFullPath(uvExePath)}");
                return true;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "安装 uv 失败");
            onOutput?.Invoke($"安装 uv 失败: {ex.Message}");
            return false;
        }
    }

    private async Task<bool> SetupSystemPython(CancellationToken ct)
    {
        try
        {
            // 确保 uv 已安装，uv 会自动管理 Python
            if (!await EnsureUvInstalledAsync(ct, null))
            {
                return false;
            }

            _logger.LogInformation("uv 安装完成，Python 将由 uv 自动管理");
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "安装 uv 失败");
            return false;
        }
    }

    private async Task<bool> SetupWindowsPython(Action<long, long>? onProgress, CancellationToken ct)
    {
        var tempZip = Path.Combine(Path.GetTempPath(), "python-embed.zip");

        if (!await DownloadFileAsync(PythonDownloadUrl, tempZip, onProgress, ct))
            return false;

        Directory.CreateDirectory(PythonDir);
        await Task.Run(() => ZipFile.ExtractToDirectory(tempZip, PythonDir, true), ct);
        File.Delete(tempZip);

        // 启用 site-packages
        var pthFile = Path.Combine(PythonDir, "python311._pth");
        if (File.Exists(pthFile))
        {
            var content = await File.ReadAllTextAsync(pthFile, ct);
            content = content.Replace("#import site", "import site");
            await File.WriteAllTextAsync(pthFile, content, ct);
        }

        // 安装 pip
        var getPipPath = Path.Combine(PythonDir, "get-pip.py");
        if (!await DownloadFileAsync(GetPipUrl, getPipPath, null, ct))
            return false;

        await RunCommandAsync(PythonExe, "get-pip.py", Path.GetFullPath(PythonDir), null, ct);
        File.Delete(getPipPath);

        // 安装 uv
        await RunCommandAsync(PythonExe, $"-m pip install uv -i {PipMirror}", Path.GetFullPath(PythonDir), null, ct);

        // 兼容旧逻辑：把 pip 安装出来的 uv.exe 复制到 bin/uv/uv.exe
        var uvFromPip = Path.Combine(PythonDir, "Scripts", "uv.exe");
        if (!File.Exists(uvFromPip))
        {
            // 有些环境 scripts 会直接落在根目录
            var uvFromRoot = Path.Combine(PythonDir, "uv.exe");
            if (File.Exists(uvFromRoot))
                uvFromPip = uvFromRoot;
        }

        if (!File.Exists(uvFromPip))
        {
            _logger.LogError("uv 安装后未找到可执行文件: {Path}", uvFromPip);
            return false;
        }

        try
        {
            var uvDir = Path.GetFullPath("bin/uv");
            Directory.CreateDirectory(uvDir);
            var uvTarget = Path.Combine(uvDir, "uv.exe");
            File.Copy(uvFromPip, uvTarget, overwrite: true);
            _logger.LogInformation("uv 已就绪: {Path}", uvTarget);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "复制 uv.exe 到 bin/uv 失败，将尝试直接使用 PythonDir/Scripts/uv.exe");
        }

        _logger.LogInformation("Python + uv 安装完成");
        return true;
    }

    private string? TryResolveUvExecutablePathWindows()
    {
        var uvInBin = Path.GetFullPath("bin/uv/uv.exe");
        if (File.Exists(uvInBin))
            return uvInBin;

        var uvInScripts = Path.Combine(PythonDir, "Scripts", "uv.exe");
        if (File.Exists(uvInScripts))
        {
            // 尝试复制到 bin/uv，保持调用方兼容
            try
            {
                var uvDir = Path.GetFullPath("bin/uv");
                Directory.CreateDirectory(uvDir);
                File.Copy(uvInScripts, uvInBin, overwrite: true);
                return uvInBin;
            }
            catch
            {
                return uvInScripts;
            }
        }

        var uvInRoot = Path.Combine(PythonDir, "uv.exe");
        if (File.Exists(uvInRoot))
            return uvInRoot;

        return null;
    }

    public async Task<bool> CreateVenvAsync(string targetDir, Action<string>? onOutput = null, CancellationToken ct = default)
    {
        var venvPath = Path.Combine(targetDir, "venv");

        await RunCommandAsync(PythonExe, $"-m pip install virtualenv -i {PipMirror}", targetDir, onOutput, ct);
        await RunCommandAsync(PythonExe, "-m virtualenv venv", targetDir, onOutput, ct);

        return Directory.Exists(venvPath);
    }

    public async Task<bool> InstallRequirementsAsync(string venvDir, string requirementsFile, Action<string>? onOutput = null, CancellationToken ct = default)
    {
        var scriptsDir = PlatformHelper.IsWindows ? "Scripts" : "bin";
        var venvPython = Path.Combine(venvDir, "venv", scriptsDir, "python" + PlatformHelper.ExecutableExtension);

        if (!File.Exists(venvPython))
        {
            _logger.LogError("虚拟环境 Python 不存在: {Path}", venvPython);
            return false;
        }

        if (!File.Exists(requirementsFile))
        {
            _logger.LogWarning("requirements.txt 不存在，跳过依赖安装");
            return true;
        }

        await RunCommandAsync(venvPython, $"-m pip install -r \"{requirementsFile}\" -i {PipMirror}", venvDir, onOutput, ct);
        _logger.LogInformation("依赖安装完成");
        return true;
    }

    public async Task<bool> UvInstallRequirementsAsync(string targetDir, string requirementsFile, Action<string>? onOutput = null, CancellationToken ct = default)
    {
        // 使用 bin/uv/uv
        string uvCommand;

        if (PlatformHelper.IsWindows)
        {
            var uvExe = TryResolveUvExecutablePathWindows();
            if (string.IsNullOrEmpty(uvExe) || !File.Exists(uvExe))
            {
                // Windows 也按 macOS 逻辑：缺 uv 就自动下载
                onOutput?.Invoke("未检测到 uv，正在下载...");
                if (!await EnsureUvInstalledAsync(ct, onOutput))
                {
                    var expected = Path.GetFullPath("bin/uv/uv.exe");
                    _logger.LogError("uv 未安装: {Path}", expected);
                    onOutput?.Invoke($"uv 未安装: {expected}");
                    return false;
                }

                uvExe = TryResolveUvExecutablePathWindows();
            }

            if (string.IsNullOrEmpty(uvExe) || !File.Exists(uvExe))
            {
                var expected = Path.GetFullPath("bin/uv/uv.exe");
                _logger.LogError("uv 未安装: {Path}", expected);
                onOutput?.Invoke($"uv 未安装: {expected}");
                return false;
            }

            uvCommand = uvExe;
        }
        else
        {
            // macOS/Linux: 确保 uv 已安装
            if (!await EnsureUvInstalledAsync(ct, onOutput))
            {
                _logger.LogError("无法安装 uv");
                onOutput?.Invoke("无法安装 uv");
                return false;
            }

            var uvExe = Path.GetFullPath("bin/uv/uv");
            if (!File.Exists(uvExe))
            {
                _logger.LogError("uv 未安装: {Path}", uvExe);
                onOutput?.Invoke($"uv 未安装: {uvExe}");
                return false;
            }
            uvCommand = uvExe;
        }

        // 检查是否是 Poetry 项目
        var pyprojectFile = Path.Combine(targetDir, "pyproject.toml");
        var isPoetryProject = false;
        if (File.Exists(pyprojectFile))
        {
            var content = await File.ReadAllTextAsync(pyprojectFile, ct);
            isPoetryProject = content.Contains("[tool.poetry]");
        }

        if (isPoetryProject)
        {
            // 安装 poetry
            onOutput?.Invoke("安装 Poetry...");
            var poetryInstallExitCode = await RunUvCommandAsync(uvCommand, "tool install poetry", targetDir, onOutput, ct);
            if (poetryInstallExitCode != 0)
                _logger.LogWarning("Poetry 可能已安装，继续执行");

            // 先执行 poetry lock 更新锁文件
            onOutput?.Invoke("更新 Poetry lock 文件...");
            await RunUvCommandAsync(uvCommand, "tool run poetry lock", targetDir, onOutput, ct);

            // 使用 poetry install
            onOutput?.Invoke("使用 Poetry 安装依赖...");
            var installExitCode = await RunUvCommandAsync(uvCommand, "tool run poetry install", targetDir, onOutput, ct);
            if (installExitCode != 0)
            {
                _logger.LogError("poetry install 失败，退出码: {ExitCode}", installExitCode);
                return false;
            }
        }
        else
        {
            // 创建虚拟环境
            var venvPath = Path.Combine(targetDir, ".venv");
            if (!Directory.Exists(venvPath))
            {
                var venvExitCode = await RunUvCommandAsync(uvCommand, "venv --python 3.11", targetDir, onOutput, ct);
                if (venvExitCode != 0)
                {
                    _logger.LogError("uv venv 创建失败，退出码: {ExitCode}", venvExitCode);
                    return false;
                }
            }

            // 使用 requirements.txt 安装依赖
            var reqFile = Path.Combine(targetDir, "requirements.txt");
            if (File.Exists(reqFile))
            {
                var installExitCode = await RunUvCommandAsync(uvCommand, "pip install -r requirements.txt", targetDir, onOutput, ct);
                if (installExitCode != 0)
                {
                    _logger.LogError("uv pip install 失败，退出码: {ExitCode}", installExitCode);
                    return false;
                }
            }
        }

        _logger.LogInformation("依赖安装完成");
        return true;
    }

    private async Task<int> RunUvCommandAsync(string uvExe, string args, string workDir, Action<string>? onOutput, CancellationToken ct)
    {
        _logger.LogInformation("执行命令: {Exe} {Args} in {WorkDir}", uvExe, args, workDir);

        var psi = new ProcessStartInfo
        {
            FileName = uvExe,
            Arguments = args,
            WorkingDirectory = workDir,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        
        psi.Environment["UV_INDEX_URL"] = PipMirror;
        psi.Environment["UV_PYTHON_INSTALL_MIRROR"] = "https://gh-proxy.com/https://github.com/astral-sh/python-build-standalone/releases/download";

        using var process = new Process { StartInfo = psi };
        
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data != null)
            {
                onOutput?.Invoke(e.Data);
                _logger.LogInformation("[uv] {Output}", e.Data);
            }
        };
        
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data != null)
            {
                onOutput?.Invoke(e.Data);
                _logger.LogInformation("[uv] {Output}", e.Data);
            }
        };
        
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        
        await process.WaitForExitAsync(ct);
        
        _logger.LogInformation("uv 命令完成，退出码: {ExitCode}", process.ExitCode);
        return process.ExitCode;
    }

    public async Task RunCommandAsync(string exe, string args, string workDir, Action<string>? onOutput = null, CancellationToken ct = default)
    {
        _logger.LogInformation("执行命令: {Exe} {Args}", exe, args);

        var psi = new ProcessStartInfo
        {
            FileName = exe,
            Arguments = args,
            WorkingDirectory = workDir,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        using var process = Process.Start(psi);
        if (process == null) return;

        var outputTask = Task.Run(async () =>
        {
            while (!process.StandardOutput.EndOfStream)
            {
                var line = await process.StandardOutput.ReadLineAsync(ct);
                if (line != null)
                {
                    onOutput?.Invoke(line);
                    _logger.LogDebug("{Output}", line);
                }
            }
        }, ct);

        var errorTask = Task.Run(async () =>
        {
            while (!process.StandardError.EndOfStream)
            {
                var line = await process.StandardError.ReadLineAsync(ct);
                if (line != null)
                {
                    onOutput?.Invoke(line);
                    _logger.LogWarning("{Error}", line);
                }
            }
        }, ct);

        await Task.WhenAll(outputTask, errorTask);
        await process.WaitForExitAsync(ct);
    }

    private async Task<bool> DownloadFileAsync(string url, string destPath, Action<long, long>? onProgress, CancellationToken ct)
    {
        try
        {
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
            _logger.LogInformation("下载被取消: {Url}", url);
            throw;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "下载网络错误: {Url}", url);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "下载文件失败: {Url}", url);
            return false;
        }
    }

    private static string[] GetGitHubUrlsWithProxy(string githubUrl)
    {
        if (!githubUrl.StartsWith("https://github.com"))
            return [githubUrl];

        var urls = new List<string>();
        foreach (var proxy in GhProxies)
        {
            urls.Add($"{proxy}{githubUrl}");
        }
        urls.Add(githubUrl); // 最后添加原始 URL
        return urls.ToArray();
    }
}
