using System;
using GimmeCapture.Services.Abstractions;
using GimmeCapture.Services.Core.AI;
using GimmeCapture.Services.Core.Infrastructure;
using GimmeCapture.Services.Core.Media;

namespace GimmeCapture.ViewModels.Main;

/// <summary>
/// The app services the capture overlay uses, handed to it instead of reached through the main window. The FFmpeg
/// downloader is created only when a recording first asks for it, as it was before, so a screenshot never pays for it.
/// </summary>
public sealed class SnipWindowServices
{
    private readonly Lazy<FFmpegDownloaderService> _ffmpegDownloader;

    public SnipWindowServices(
        IGlobalHotkeyService hotkeys,
        HotkeyRouterService hotkeyRouter,
        IAppSettingsService settings,
        CaptureHistoryService captureHistory,
        AIResourceService aiResources,
        OcrRuntimeService ocrRuntime,
        Func<FFmpegDownloaderService> ffmpegDownloader)
    {
        Hotkeys = hotkeys ?? throw new ArgumentNullException(nameof(hotkeys));
        HotkeyRouter = hotkeyRouter ?? throw new ArgumentNullException(nameof(hotkeyRouter));
        SettingsService = settings ?? throw new ArgumentNullException(nameof(settings));
        CaptureHistory = captureHistory ?? throw new ArgumentNullException(nameof(captureHistory));
        AIResources = aiResources ?? throw new ArgumentNullException(nameof(aiResources));
        OcrRuntime = ocrRuntime ?? throw new ArgumentNullException(nameof(ocrRuntime));
        ArgumentNullException.ThrowIfNull(ffmpegDownloader);
        _ffmpegDownloader = new Lazy<FFmpegDownloaderService>(ffmpegDownloader);
    }

    /// <summary>Registers the temporary global hotkeys the overlay needs while it does not hold focus.</summary>
    public IGlobalHotkeyService Hotkeys { get; }

    /// <summary>Resolves which overlay action a pressed global hotkey stands for.</summary>
    public HotkeyRouterService HotkeyRouter { get; }

    public IAppSettingsService SettingsService { get; }

    public CaptureHistoryService CaptureHistory { get; }

    public AIResourceService AIResources { get; }

    public OcrRuntimeService OcrRuntime { get; }

    public FFmpegDownloaderService FfmpegDownloader => _ffmpegDownloader.Value;
}
