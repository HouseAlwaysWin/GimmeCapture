using System;
using System.IO;

namespace GimmeCapture.Services.Core.Media.NativeFFmpeg;

/// <summary>BtbN GPL shared build native DLLs shipped under ffmpeg-lib/.</summary>
public static class FFmpegBundledPaths
{
    public static string NativeLibraryDirectory =>
        Path.Combine(AppContext.BaseDirectory, "ffmpeg-lib");

    public static bool HasBundledNativeDlls =>
        Directory.Exists(NativeLibraryDirectory)
        && Directory.GetFiles(NativeLibraryDirectory, "avcodec-*.dll").Length > 0;
}
