using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using LuckyLilliaDesktop.Services;
using Microsoft.Extensions.Logging;

namespace LuckyLilliaDesktop.Utils;

public interface IGitHubCLIHelper
{
    Task<bool> EnsureGitInstalledAsync(Action<long, long>? onProgress = null, CancellationToken ct = default);
    Task<bool> CloneFromGitHubAsync(string githubRepo, string targetDir, string? branch = null, CancellationToken ct = default);
    bool IsGitAvailable();
}

public class GitHubCLIHelper : IGitHubCLIHelper
{
    private readonly ILogger<GitHubCLIHelper> _logger;
    private readonly IGitHubHelper _gitHubHelper;
    private const string GitDir = "bin/git";
    private const string PortableGitVersion = "2.47.1";
    
    private string? _gitPath;

    public GitHubCLIHelper(ILogger<GitHubCLIHelper> logger, IGitHubHelper gitHubHelper)
    {
        _logger = logger;
        _gitHubHelper = gitHubHelper;
    }

    public bool IsGitAvailable()
    {
        if (_gitPath != null && File.Exists(_gitPath))
            return true;

        // 检查系统 Git
        if (CheckSystemGit())
            return true;

        // 检查本地 Git
        var localGit = Path.GetFullPath(Path.Combine(GitDir, "bin", "git.exe"));
        if (File.Exists(localGit))
        {
            _gitPath = localGit;
            return true;
        }

        return false;
    }

    public async Task<bool> EnsureGitInstalledAsync(Action<long, long>? onProgress = null, CancellationToken ct = default)
    {
        if (IsGitAvailable())
        {
            _logger.LogInformation("Git 已可用: {Path}", _gitPath ?? "系统 Git");
            return true;
        }

        _logger.LogInformation("开始下载 PortableGit");
        
        var fileName = $"PortableGit-{PortableGitVersion}-64-bit.7z.exe";
        var tempFile = Path.Combine(Path.GetTempPath(), fileName);

        string[] urls = [
            $"https://npmmirror.com/mirrors/git-for-windows/v{PortableGitVersion}.windows.1/{fileName}",
            $"https://github.com/git-for-windows/git/releases/download/v{PortableGitVersion}.windows.1/{fileName}"
        ];

        var downloadSuccess = await _gitHubHelper.DownloadWithFallbackAsync(urls, tempFile, onProgress, ct);
        if (!downloadSuccess)
        {
            _logger.LogError("下载 PortableGit 失败");
            return false;
        }

        try
        {
            _logger.LogInformation("开始解压 PortableGit");
            await SafeDeleteDirectoryAsync(GitDir);
            Directory.CreateDirectory(GitDir);

            // PortableGit 的 7z.exe 是自解压的
            var psi = new ProcessStartInfo
            {
                FileName = tempFile,
                Arguments = $"-o\"{Path.GetFullPath(GitDir)}\" -y",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using var process = Process.Start(psi);
            if (process == null)
            {
                _logger.LogError("启动 PortableGit 解压失败");
                return false;
            }

            await process.WaitForExitAsync(ct).ConfigureAwait(false);
            File.Delete(tempFile);

            if (process.ExitCode != 0)
            {
                _logger.LogError("PortableGit 解压失败，退出码: {Code}", process.ExitCode);
                return false;
            }

            _gitPath = Path.GetFullPath(Path.Combine(GitDir, "bin", "git.exe"));
            _logger.LogInformation("PortableGit 安装完成: {Path}", _gitPath);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "安装 PortableGit 失败");
            return false;
        }
    }

    public async Task<bool> CloneFromGitHubAsync(string githubRepo, string targetDir, string? branch = null, CancellationToken ct = default)
    {
        if (!IsGitAvailable())
        {
            _logger.LogWarning("Git 不可用，无法从 GitHub 克隆");
            return false;
        }

        // 测试 GitHub 连接
        var canConnectGitHub = await CanConnectGitHubAsync(ct);
        
        string url;
        if (canConnectGitHub)
        {
            url = $"https://github.com/{githubRepo}.git";
            _logger.LogInformation("使用 GitHub 直连");
        }
        else
        {
            // 使用镜像
            url = $"https://mirror.ghproxy.com/https://github.com/{githubRepo}.git";
            _logger.LogInformation("使用 GitHub 镜像");
        }

        return await CloneRepositoryAsync(url, targetDir, branch, ct);
    }

    private async Task<bool> CloneRepositoryAsync(string url, string targetDir, string? branch, CancellationToken ct)
    {
        try
        {
            var fullTargetDir = Path.GetFullPath(targetDir);
            
            // 如果目录已存在且是 git 仓库，使用 fetch + reset 覆盖
            if (Directory.Exists(Path.Combine(fullTargetDir, ".git")))
            {
                _logger.LogInformation("目标目录已存在 Git 仓库，使用 fetch + reset 覆盖: {Dir}", targetDir);
                var fetchArgs = "fetch origin --depth 1";
                var resetArgs = branch != null 
                    ? $"reset --hard origin/{branch}" 
                    : "reset --hard origin/HEAD";
                
                if (await RunGitCommandAsync(fetchArgs, fullTargetDir, ct) && 
                    await RunGitCommandAsync(resetArgs, fullTargetDir, ct))
                {
                    _logger.LogInformation("Git fetch + reset 成功: {Dir}", targetDir);
                    return true;
                }
                _logger.LogWarning("Git fetch + reset 失败，回退到重新克隆");
                await SafeDeleteDirectoryAsync(targetDir);
            }

            var args = branch != null 
                ? $"clone --depth 1 --branch {branch} \"{url}\" \"{fullTargetDir}\"" 
                : $"clone --depth 1 \"{url}\" \"{fullTargetDir}\"";

            _logger.LogInformation("执行 Git 克隆: {Url}", url);

            var psi = new ProcessStartInfo
            {
                FileName = _gitPath ?? "git",
                Arguments = args,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using var process = Process.Start(psi);
            if (process == null)
            {
                _logger.LogError("启动 Git 进程失败");
                return false;
            }

            var outputTask = Task.Run(async () =>
            {
                while (!process.StandardOutput.EndOfStream)
                {
                    var line = await process.StandardOutput.ReadLineAsync(ct).ConfigureAwait(false);
                    if (!string.IsNullOrEmpty(line))
                        _logger.LogDebug("Git: {Output}", line);
                }
            }, ct);

            var errorTask = Task.Run(async () =>
            {
                while (!process.StandardError.EndOfStream)
                {
                    var line = await process.StandardError.ReadLineAsync(ct).ConfigureAwait(false);
                    if (!string.IsNullOrEmpty(line))
                        _logger.LogDebug("Git: {Error}", line);
                }
            }, ct);

            await process.WaitForExitAsync(ct).ConfigureAwait(false);
            await Task.WhenAll(outputTask, errorTask).ConfigureAwait(false);

            if (process.ExitCode == 0)
            {
                _logger.LogInformation("Git 克隆成功: {Url} -> {Dir}", url, targetDir);
                return true;
            }

            _logger.LogWarning("Git 克隆失败，退出码: {Code}", process.ExitCode);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Git 克隆异常: {Url}", url);
            return false;
        }
    }

    private async Task<bool> RunGitCommandAsync(string args, string workDir, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = _gitPath ?? "git",
            Arguments = args,
            WorkingDirectory = workDir,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        using var process = Process.Start(psi);
        if (process == null) return false;

        await process.WaitForExitAsync(ct).ConfigureAwait(false);
        return process.ExitCode == 0;
    }

    private bool CheckSystemGit()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "git",
                Arguments = "--version",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true
            };

            using var process = Process.Start(psi);
            if (process == null) return false;

            process.WaitForExit(3000);
            if (process.ExitCode == 0)
            {
                _gitPath = "git";
                _logger.LogInformation("检测到系统 Git");
                return true;
            }
        }
        catch { }

        return false;
    }

    private async Task<bool> CanConnectGitHubAsync(CancellationToken ct)
    {
        try
        {
            using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            var response = await httpClient.GetAsync("https://github.com", ct).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
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
