using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using FFmpeg.AutoGen;

namespace GimmeCapture.Services.Core.Media.NativeFFmpeg;

/// <summary>
/// Loads FFmpeg GPL shared DLLs from &lt;BaseDirectory&gt;/ffmpeg-lib (see scripts/ensure-ffmpeg-libs.ps1).
/// </summary>
public static class FFmpegRuntime
{
    private static readonly object Gate = new();
    private static bool _initialized;

    /// <summary> libavformat build version string (after successful init).</summary>
    public static string? LibraryVersion { get; private set; }

    public static bool IsInitialized => _initialized;

    /// <summary>Returns false if ffmpeg-lib/*.dll missing or unloadable.</summary>
    public static bool TryInitialize(out string? errorMessage)
    {
        lock (Gate)
        {
            if (_initialized)
            {
                errorMessage = null;
                return true;
            }

            try
            {
                var libsDir = Path.Combine(AppContext.BaseDirectory, "ffmpeg-lib");
                if (!Directory.Exists(libsDir))
                {
                    errorMessage =
                        $"Missing native FFmpeg folder: {libsDir}. Run scripts/ensure-ffmpeg-libs.ps1 or copy BtbN win64-gpl-shared *.dll into ffmpeg-lib.";
                    return false;
                }

                // Windows ships avcodec-62.dll etc.; Linux/macOS ship libavcodec.so.62 / libavcodec.dylib.
                var required = new[] { "avutil", "avcodec", "avformat", "swscale", "swresample", "avdevice", "avfilter" };
                bool isWindows = OperatingSystem.IsWindows();
                string globPattern = isWindows ? "*.dll" : (OperatingSystem.IsMacOS() ? "lib*.dylib*" : "lib*.so*");
                var libs = Directory.GetFiles(libsDir, globPattern).Select(Path.GetFileName).ToArray();
                foreach (var prefix in required)
                {
                    bool present = isWindows
                        ? libs.Any(d => d != null && d.StartsWith(prefix + "-", StringComparison.OrdinalIgnoreCase))
                        : libs.Any(d => d != null && d.StartsWith("lib" + prefix + ".", StringComparison.OrdinalIgnoreCase));
                    if (!present)
                    {
                        string want = isWindows ? $"{prefix}-*.dll" : $"lib{prefix}.so*";
                        errorMessage =
                            $"Incomplete FFmpeg native bundle: expected {want} under {libsDir}. " +
                            (isWindows ? "Run scripts/ensure-ffmpeg-libs.ps1." : "Run scripts/ensure-ffmpeg-libs-linux.sh.");
                        return false;
                    }
                }

                ffmpeg.RootPath = libsDir;

                LibraryVersion = ffmpeg.av_version_info();

                ffmpeg.avdevice_register_all();
                ffmpeg.avformat_network_init();

                _initialized = true;
                errorMessage = null;
                Debug.WriteLine($"[FFmpegRuntime] Initialized. avutil: {LibraryVersion}");
                return true;
            }
            catch (Exception ex)
            {
                errorMessage = $"FFmpeg native init failed: {ex.Message}";
                Debug.WriteLine($"[FFmpegRuntime] {errorMessage}");
                return false;
            }
        }
    }

    /// <summary>Throws <see cref="InvalidOperationException"/> if not initialized.</summary>
    public static void EnsureInitialized()
    {
        if (!_initialized && !TryInitialize(out var errorMessage))
        {
            throw new InvalidOperationException(errorMessage ?? "FFmpegRuntime initialization failed.");
        }
    }
}
