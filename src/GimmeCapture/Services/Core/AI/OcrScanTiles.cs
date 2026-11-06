using System.Collections.Generic;
using System.Threading;
using Avalonia;
using GimmeCapture.Services.Core.Interfaces;
using SkiaSharp;

namespace GimmeCapture.Services.Core.AI;

/// <summary>
/// The parts of an overlay scan's capture that are read apart: one per screen when the overlay spans several. Detection
/// brings a capture's longer side down to 1280 px, so two screens side by side were read at a third of their pixels. On
/// two screens drawn with UI text at 11-24 px (1920x1080 and 2048x1160, <c>OcrScanScreensProbeTests</c>), read as one
/// capture 4% of the lines got a box of their own and 86% were covered by one; read screen by screen, 62% and 100%,
/// detection taking 290-320 ms instead of 60-70. One screen, the usual case, is read whole as before.
/// </summary>
internal static class OcrScanTiles
{
    // A screen narrower or lower than this in the capture is not worth a detection of its own.
    private const int MinimumSide = 32;

    /// <summary>
    /// Each screen's part of a capture of <paramref name="viewport"/> that is <paramref name="width"/> x
    /// <paramref name="height"/> pixels, with <paramref name="screens"/> in the viewport's coordinates. Empty, for the
    /// capture to be read whole, unless at least two screens lie in it.
    /// </summary>
    internal static List<SKRectI> ForScreens(IReadOnlyList<Rect>? screens, Rect viewport, int width, int height)
    {
        var tiles = new List<SKRectI>();
        if (screens is not { Count: > 1 } || viewport.Width <= 0 || viewport.Height <= 0 || width <= 0 || height <= 0)
        {
            return tiles;
        }

        double scaleX = width / viewport.Width;
        double scaleY = height / viewport.Height;
        var capture = new SKRectI(0, 0, width, height);
        foreach (var screen in screens)
        {
            var tile = SKRectI.Round(new SKRect(
                (float)((screen.X - viewport.X) * scaleX),
                (float)((screen.Y - viewport.Y) * scaleY),
                (float)((screen.Right - viewport.X) * scaleX),
                (float)((screen.Bottom - viewport.Y) * scaleY)));
            tile = SKRectI.Intersect(tile, capture);
            // A mirrored display has the same bounds as the one it mirrors.
            if (tile.Width >= MinimumSide && tile.Height >= MinimumSide && !tiles.Contains(tile))
            {
                tiles.Add(tile);
            }
        }

        if (tiles.Count < 2)
        {
            tiles.Clear();
        }

        return tiles;
    }

    /// <summary>
    /// The text boxes in <paramref name="capture"/>, in its pixels: those of each of <paramref name="tiles"/>, read one
    /// after another, or of the whole capture when there are none.
    /// </summary>
    internal static List<SKRectI> Detect(IOCREngine engine, SKBitmap capture, IReadOnlyList<SKRectI> tiles, CancellationToken ct)
    {
        if (tiles.Count == 0)
        {
            return engine.DetectText(capture, ct);
        }

        var boxes = new List<SKRectI>();
        foreach (var tile in tiles)
        {
            ct.ThrowIfCancellationRequested();
            // A view of the capture's own pixels: nothing is copied.
            using var part = new SKBitmap();
            if (!capture.ExtractSubset(part, tile))
            {
                continue;
            }

            foreach (var box in engine.DetectText(part, ct))
            {
                boxes.Add(new SKRectI(box.Left + tile.Left, box.Top + tile.Top, box.Right + tile.Left, box.Bottom + tile.Top));
            }
        }

        return boxes;
    }
}
