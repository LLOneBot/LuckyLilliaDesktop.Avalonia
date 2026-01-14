using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

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
    public string PythonExe => Path.GetFullPath(Path.Combine(PythonDir, "python.exe"));
    public bool IsInstalled => File.Exists(Path.Combine(PythonDir, "python.exe"));

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
        var venvPython = Path.Combine(venvDir, "venv", "Scripts", "python.exe");

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
        var uvExe = Path.Combine(Path.GetFullPath(PythonDir), "Scripts", "uv.exe");
        if (!File.Exists(uvExe))
        {
            _logger.LogError("uv 未安装: {Path}", uvExe);
            return false;
        }

        // uv sync 自动创建虚拟环境并安装依赖
        await RunUvCommandAsync(uvExe, "sync", targetDir, onOutput, ct);
        
        _logger.LogInformation("uv 依赖安装完成");
        return true;
    }

    private async Task RunUvCommandAsync(string uvExe, string args, string workDir, Action<string>? onOutput, CancellationToken ct)
    {
        _logger.LogInformation("执行命令: {Exe} {Args}", uvExe, args);

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

        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        
        var tcs = new TaskCompletionSource<bool>();
        
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data != null)
            {
                onOutput?.Invoke(e.Data);
                _logger.LogInformation("{Output}", e.Data);
            }
        };
        
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data != null)
            {
                onOutput?.Invoke(e.Data);
                _logger.LogInformation("{Output}", e.Data);
            }
        };
        
        process.Exited += (_, _) => tcs.TrySetResult(true);
        
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        
        using (ct.Register(() => tcs.TrySetCanceled()))
        {
            await tcs.Task;
        }
        
        if (process.ExitCode != 0)
            _logger.LogWarning("uv 命令退出码: {ExitCode}", process.ExitCode);
    }

    private async Task ReadStreamAsync(StreamReader reader, Action<string>? onOutput, CancellationToken ct)
    {
        while (!reader.EndOfStream)
        {
            var line = await reader.ReadLineAsync(ct);
            if (line != null)
            {
                onOutput?.Invoke(line);
                _logger.LogDebug("{Output}", line);
            }
        }
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
