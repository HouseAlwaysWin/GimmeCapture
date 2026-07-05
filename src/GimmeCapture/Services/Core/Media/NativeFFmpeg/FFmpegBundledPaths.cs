using System;
using System.IO;

namespace GimmeCapture.Services.Core.Media.NativeFFmpeg;

/// <summary>BtbN GPL shared build native DLLs shipped under ffmpeg-lib/.</summary>
public static class FFmpegBundledPaths
{
    public static string NativeLibraryDirectory =>
        Path.Combine(AppContext.BaseDirectory, "ffmpeg-lib");

    // Windows ships avcodec-62.dll; Linux ships libavcodec.so.62; macOS libavcodec.*.dylib.
    public static bool HasBundledNativeDlls =>
        Directory.Exists(NativeLibraryDirectory)
        && Directory.GetFiles(NativeLibraryDirectory, NativeAvcodecGlob).Length > 0;

    private static string NativeAvcodecGlob =>
        OperatingSystem.IsWindows() ? "avcodec-*.dll"
        : OperatingSystem.IsMacOS() ? "libavcodec*.dylib"
        : "libavcodec.so*";
}
