using Microsoft.Extensions.Logging;
using System;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace LuckyLilliaDesktop.Utils;

public static class NodeHelper
{
    public static string? FindNodeInPath()
    {
        var nodeExe = FindExecutableInPath("node.exe");
        if (nodeExe != null) return nodeExe;
        
        return FindExecutableInPath("node");
    }
    
    private static string? FindExecutableInPath(string executable)
    {
        var pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(pathEnv)) return null;
        
        var paths = pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        
        foreach (var path in paths)
        {
            try
            {
                var fullPath = Path.Combine(path, executable);
                if (File.Exists(fullPath))
                {
                    return fullPath;
                }
            }
            catch { }
        }
        
        return null;
    }
    
    public static async Task<int?> GetNodeVersionAsync(string nodePath, ILogger? logger = null)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = nodePath,
                    Arguments = "--version",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                }
            };
            
            process.Start();
            var output = await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();
            
            if (process.ExitCode == 0)
            {
                // 版本格式: v22.0.0 或 v18.17.1
                var versionStr = output.Trim();
                var match = Regex.Match(versionStr, @"v(\d+)");
                if (match.Success && int.TryParse(match.Groups[1].Value, out var majorVersion))
                {
                    logger?.LogInformation("Node.js版本: {Version} (主版本: {Major})", versionStr, majorVersion);
                    return majorVersion;
                }
            }
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "获取Node.js版本失败");
        }
        
        return null;
    }
    
    public static async Task<bool> CheckNodeVersionValidAsync(string nodePath, int minVersion = 22, ILogger? logger = null)
    {
        var version = await GetNodeVersionAsync(nodePath, logger);
        return version >= minVersion;
    }
}