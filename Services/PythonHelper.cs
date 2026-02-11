using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
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

    public string PythonDir => "bin/python";
    public string PythonExe => Path.GetFullPath(Path.Combine(PythonDir, "python" + PlatformHelper.ExecutableExtension));
    public bool IsInstalled => File.Exists(Path.Combine(PythonDir, "python" + PlatformHelper.ExecutableExtension));

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

            _logger.LogInformation("Python + uv 安装完成");
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Python 安装失败");
            return false;
        }
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
        var scriptsDir = PlatformHelper.IsWindows ? "Scripts" : "bin";
        var uvExe = Path.Combine(Path.GetFullPath(PythonDir), scriptsDir, "uv" + PlatformHelper.ExecutableExtension);
        if (!File.Exists(uvExe))
        {
            _logger.LogError("uv 未安装: {Path}", uvExe);
            return false;
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
            var poetryInstallExitCode = await RunUvCommandAsync(uvExe, "tool install poetry", targetDir, onOutput, ct);
            if (poetryInstallExitCode != 0)
                _logger.LogWarning("Poetry 可能已安装，继续执行");

            // 配置 Poetry 使用本地 Python
            var pythonExe = Path.GetFullPath(PythonExe);
            onOutput?.Invoke("配置 Poetry Python 路径...");
            await RunUvCommandAsync(uvExe, $"tool run poetry env use \"{pythonExe}\"", targetDir, onOutput, ct);

            // 先执行 poetry lock
            onOutput?.Invoke("更新 Poetry lock 文件...");
            await RunUvCommandAsync(uvExe, "tool run poetry lock", targetDir, onOutput, ct);

            // 使用 poetry install
            onOutput?.Invoke("使用 Poetry 安装依赖...");
            var installExitCode = await RunUvCommandAsync(uvExe, "tool run poetry install", targetDir, onOutput, ct);
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
                var venvExitCode = await RunUvCommandAsync(uvExe, "venv --python 3.11", targetDir, onOutput, ct);
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
                var installExitCode = await RunUvCommandAsync(uvExe, "pip install -r requirements.txt", targetDir, onOutput, ct);
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
        catch (Exception ex)
        {
            _logger.LogError(ex, "下载文件失败: {Url}", url);
            return false;
        }
    }
}
