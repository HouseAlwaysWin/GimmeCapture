using System;
using GimmeCapture.Models;

namespace GimmeCapture.Services.Core.Media;

/// <summary>
/// Pure display→source crop math shared by the video editors: maps a selection rectangle drawn on the
/// (uniform-stretched) preview into a source-pixel crop, snapped to even dimensions (yuv420p) and clamped
/// in bounds. Mirrors the Pin window's <c>SelectionToPixelCropRect</c>; kept UI-free so it is unit-testable
/// (takes plain doubles rather than Avalonia types).
/// </summary>
public static class VideoCropMath
{
    /// <summary>
    /// Convert a display-space selection into a source-pixel <see cref="VideoEditCrop"/>. Returns null when
    /// the source is degenerate or the selection collapses to less than 2×2 source pixels (i.e. no crop).
    /// </summary>
    public static VideoEditCrop? SelectionToCrop(
        double selX, double selY, double selWidth, double selHeight,
        double displayWidth, double displayHeight, double sourceWidth, double sourceHeight)
    {
        int videoW = Math.Max(0, (int)Math.Round(sourceWidth));
        int videoH = Math.Max(0, (int)Math.Round(sourceHeight));
        if (videoW < 2 || videoH < 2)
        {
            return null;
        }

        double scaleX = displayWidth > 0 ? sourceWidth / displayWidth : 1.0;
        double scaleY = displayHeight > 0 ? sourceHeight / displayHeight : 1.0;

        int x = Math.Clamp((int)Math.Round(selX * scaleX), 0, videoW - 2);
        int y = Math.Clamp((int)Math.Round(selY * scaleY), 0, videoH - 2);

        // Even width/height (yuv420p), clamped so x+w / y+h stay in bounds.
        int w = (int)Math.Round(selWidth * scaleX);
        int h = (int)Math.Round(selHeight * scaleY);
        w = Math.Min(w, videoW - x);
        h = Math.Min(h, videoH - y);
        w = (w / 2) * 2;
        h = (h / 2) * 2;

        if (w < 2 || h < 2)
        {
            return null;
        }

        return new VideoEditCrop(x, y, w, h);
    }
}
