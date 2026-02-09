using System.IO;
using FFmpeg.AutoGen;

namespace LuciLink.Client;

public static class FFmpegBinariesHelper
{
    public static void RegisterFFmpegBinaries()
    {
        // Try to find ffmpeg in PATH or common locations
        string? ffmpegPath = FindFFmpegPath();
        if (ffmpegPath != null)
        {
            ffmpeg.RootPath = ffmpegPath;
        }
    }

    private static string? FindFFmpegPath()
    {
        // Check local directory first
        var local = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ffmpeg");
        if (Directory.Exists(local)) return local;

        // Check common installation paths
        // e.g. C:\Program Files\Ffmpeg\bin
        // ...
        
        // Return null to let FFmpeg.AutoGen try default loading from PATH
        return null;
    }
}
