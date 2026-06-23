using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using SkiaSharp;

namespace GimmeCapture.Services.Abstractions;

/// <summary>
/// Captures a tall "scrolling screenshot" of a region by auto-scrolling the window
/// underneath it, capturing frames, and stitching them into a single image.
/// </summary>
public interface IScrollingCaptureService
{
    /// <summary>
    /// Auto-scrolls the window under the centre of <paramref name="region"/> and returns the
    /// stitched image (physical pixels), or null if nothing could be captured.
    /// </summary>
    /// <param name="region">Selection rectangle in logical coordinates (same space as snip selection).</param>
    /// <param name="screenOffset">Physical screen offset of the capture surface.</param>
    /// <param name="visualScaling">DPI scaling factor.</param>
    Task<SKBitmap?> CaptureAsync(
        Rect region,
        PixelPoint screenOffset,
        double visualScaling,
        CancellationToken cancellationToken = default);
}
