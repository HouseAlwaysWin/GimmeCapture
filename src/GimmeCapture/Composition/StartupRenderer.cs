using System;
using System.Diagnostics;
using Avalonia;
using GimmeCapture.Services.Core.Infrastructure;

namespace GimmeCapture.Composition;

/// <summary>
/// The renderer the Windows build starts with: the GPU (ANGLE on Direct3D 11, Avalonia's default) or, with
/// <see cref="Models.AppSettings.SoftwareRendering"/> on, the CPU. Avalonia picks it once, as its Win32 platform starts,
/// and asks for <see cref="Win32Options()"/> only then, which gives the settings read Main began as long as possible to
/// finish. Software rendering kept the commit size about 60 MB lower in the tray and 90-120 lower with the main window
/// open, on a desktop with an RTX 4070 SUPER as on a laptop whose GPU shares system memory (170 lower there after a
/// capture). It doubled the CPU of full-screen and video drawing on that laptop, and a pin being resized draws about 20
/// frames a second instead of 60 (docs/OPTIMIZATION_BACKLOG.md), so it is a setting, off by default.
/// </summary>
internal static class StartupRenderer
{
    private static Win32PlatformOptions? _win32Options;

    /// <summary>Whether this process draws on the CPU, as <see cref="Win32Options()"/> chose. False until it has.</summary>
    internal static bool IsSoftware { get; private set; }

    /// <summary>The Win32 platform's options, from the saved setting, decided the first time Avalonia asks.</summary>
    internal static Win32PlatformOptions Win32Options()
    {
        if (_win32Options == null)
        {
            IsSoftware = ReadSetting(SettingsPreload.Started ?? new AppSettingsService());
            _win32Options = Win32Options(IsSoftware);
        }

        return _win32Options;
    }

    /// <summary>Avalonia's defaults on the GPU, or the CPU's framebuffer alone, which every machine can draw with.</summary>
    internal static Win32PlatformOptions Win32Options(bool software) =>
        software
            ? new Win32PlatformOptions { RenderingMode = [Win32RenderingMode.Software] }
            : new Win32PlatformOptions();

    /// <summary>
    /// The saved setting: from the read Main began when it has finished, otherwise that one property read from the
    /// config, under a millisecond in a published build, rather than wait for the whole read.
    /// </summary>
    internal static bool ReadSetting(AppSettingsService settingsService)
    {
        var stopwatch = Stopwatch.StartNew();
        bool fromRead = settingsService.TryGetReadSoftwareRendering(TimeSpan.Zero, out bool software);
        if (!fromRead)
        {
            software = settingsService.PeekSavedSoftwareRendering();
        }

        AppLog.Information(
            "Startup.Renderer",
            $"{(software ? "software" : "GPU")}, from {(fromRead ? "the settings read" : "the config")} in {stopwatch.Elapsed.TotalMilliseconds:F1} ms");
        return software;
    }
}
