using Microsoft.Extensions.Logging;
using System;
using System.Diagnostics;
using System.IO;

namespace LuckyLilliaDesktop.Utils;

public static class FFmpegHelper
{
    public static string? FindFFmpegInPath()
    {
        var pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(pathEnv)) return null;

        var paths = pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        
        foreach (var path in paths)
        {
            try
            {
                var ffmpegPath = Path.Combine(path, "ffmpeg.exe");
                if (File.Exists(ffmpegPath))
                {
                    return ffmpegPath;
                }
            }
            catch { }
        }
        
        return null;
    }

    public static bool CheckFFmpegExists()
    {
        var systemFFmpeg = FindFFmpegInPath();
        if (!string.IsNullOrEmpty(systemFFmpeg))
            return true;

        return File.Exists(Constants.DefaultPaths.FFmpegExe);
    }

    public static bool CheckFFprobeExists()
    {
        var pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrEmpty(pathEnv))
        {
            var paths = pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
            
            foreach (var path in paths)
            {
                try
                {
                    var ffprobePath = Path.Combine(path, "ffprobe.exe");
                    if (File.Exists(ffprobePath))
                    {
                        return true;
                    }
                }
                catch { }
            }
        }

        return File.Exists(Constants.DefaultPaths.FFprobeExe);
    }
}