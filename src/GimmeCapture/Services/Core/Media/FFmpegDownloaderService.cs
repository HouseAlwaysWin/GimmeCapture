using System;
using System.Threading.Tasks;
using ReactiveUI;
using GimmeCapture.Services.Core.Media.NativeFFmpeg;

namespace GimmeCapture.Services.Core.Media;

/// <summary>
/// Resolves FFmpeg DLL runtime availability under <see cref="FFmpegBundledPaths.NativeLibraryDirectory"/>.
/// </summary>
public class FFmpegDownloaderService : ReactiveObject
{
    private double _downloadProgress;
    public double DownloadProgress
    {
        get => _downloadProgress;
        set => this.RaiseAndSetIfChanged(ref _downloadProgress, value);
    }

    private string _lastErrorMessage = "";
    public string LastErrorMessage
    {
        get => _lastErrorMessage;
        set => this.RaiseAndSetIfChanged(ref _lastErrorMessage, value);
    }

    private bool _isDownloading;
    public bool IsDownloading
    {
        get => _isDownloading;
        set => this.RaiseAndSetIfChanged(ref _isDownloading, value);
    }

    public FFmpegDownloaderService(IAppSettingsService? settingsService = null) { }

    /// <summary>FFmpeg DLL runtime available (pure DLL mode).</summary>
    public bool IsFFmpegAvailable()
    {
        if (!FFmpegBundledPaths.HasBundledNativeDlls)
        {
            LastErrorMessage = $"Missing FFmpeg native DLLs under {FFmpegBundledPaths.NativeLibraryDirectory}.";
            return false;
        }

        if (FFmpegRuntime.IsInitialized)
        {
            return true;
        }

        var ok = FFmpegRuntime.TryInitialize(out var error);
        LastErrorMessage = ok ? string.Empty : (error ?? "FFmpeg native initialization failed.");
        return ok;
    }

    /// <summary>Compatibility API for legacy callsites that expected an ffmpeg.exe path.</summary>
    public Task<bool> EnsureFFmpegAsync()
    {
        IsDownloading = false;
        DownloadProgress = 100;
        return Task.FromResult(IsFFmpegAvailable());
    }
}
