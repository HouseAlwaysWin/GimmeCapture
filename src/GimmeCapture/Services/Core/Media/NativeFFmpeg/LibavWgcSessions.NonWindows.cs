using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace GimmeCapture.Services.Core.Media.NativeFFmpeg;

// Non-Windows (net10.0) compile shims for the WinRT Windows.Graphics.Capture recorder sessions.
//
// The real LibavWgcMkvSession / LibavWgcCompositeMkvSession live in files that are excluded from the
// net10.0 head (they depend on the WinRT projection, see GimmeCapture.csproj). RecordingService news
// them up directly (no interface seam), so these shims exist purely so RecordingService compiles on
// Linux/macOS. They are NEVER instantiated at runtime: RecordingService.WgcAvailable is false off
// Windows (OperatingSystem.IsWindowsVersionAtLeast(10,0,19041) gate), so the WGC path is skipped and
// recording falls through to the (Windows-only) gdigrab path. Real Linux recording is Phase 2 —
// docs/LINUX_PORT_FEASIBILITY.md. This whole file is Compile-Removed on the net*-windows head.

internal sealed class LibavWgcMkvSession : IDisposable
{
    public bool PreferHardwareEncoder { get; set; } = true;
    public int VideoCrf { get; set; }
    public long VideoBitrate { get; set; }
    public bool TimedOutWaitingForFrame { get; private set; }
    public string? LastErrorMessage { get; private set; } = "WGC capture is not available on this platform.";
    public string? LastWarningMessage { get; private set; }
    public string? SelectedEncoderName { get; private set; }

    public Task<bool> StartAsync(string outputPath, IntPtr hwnd, int fps, bool drawMouse, bool useH265)
        => Task.FromResult(false);

    public Task StopAsync() => Task.CompletedTask;

    public void Dispose() { }
}

internal sealed class LibavWgcCompositeMkvSession : IDisposable
{
    public bool PreferHardwareEncoder { get; set; } = true;
    public int VideoCrf { get; set; }
    public long VideoBitrate { get; set; }
    public bool TimedOutWaitingForFrame { get; private set; }
    public string? LastErrorMessage { get; private set; } = "WGC capture is not available on this platform.";
    public string? LastWarningMessage { get; private set; }
    public string? SelectedEncoderName { get; private set; }

    public Task<bool> StartAsync(string outputPath, IReadOnlyList<IntPtr> hwnds, int fps, bool drawMouse, bool useH265)
        => Task.FromResult(false);

    public Task StopAsync() => Task.CompletedTask;

    public void Dispose() { }
}
