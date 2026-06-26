using System;
using System.Threading.Tasks;

namespace GimmeCapture.Services.Abstractions;

/// <summary>
/// Captures a single frame of a specific top-level window via the Windows Graphics Capture (WGC)
/// API and saves it as a PNG. This is the Step 2 "probe" that de-risks the WGC interop before it is
/// turned into a continuous recording source: WGC (DirectX/WinRT) captures GPU/DWM-composited windows
/// that the old gdigrab BitBlt path returned all-black for.
/// </summary>
public interface IWgcWindowCaptureProbe
{
    /// <summary>
    /// Whether Windows Graphics Capture is available on this machine
    /// (<c>GraphicsCaptureSession.IsSupported()</c>). Win10 2004 (19041) or newer.
    /// </summary>
    bool IsSupported { get; }

    /// <summary>
    /// Captures one frame of the window identified by <paramref name="hwnd"/> and writes it to
    /// <paramref name="outputPngPath"/> as a PNG. Returns true on success. Never throws — failures are
    /// logged and reported as <c>false</c> so a probe attempt can't break the picker.
    /// </summary>
    Task<bool> CaptureWindowToPngAsync(IntPtr hwnd, string outputPngPath);
}
