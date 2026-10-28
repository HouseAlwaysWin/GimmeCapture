using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using SkiaSharp;

namespace GimmeCapture.ViewModels.Floating;

/// <summary>
/// The pixel work of smart selection on a pin: the coloured preview of SAM2's mask after each click, and the mask
/// applied to the pin's pixels when the selection is confirmed.
/// </summary>
/// <remarks>
/// Every size and row stride is read once per call. Each is a call into Skia, and the loops these replaced, in
/// <see cref="FloatingImageViewModel"/>, read them at every pixel: on a 4K pin that was 0.2 s of every click (on the
/// UI thread) and 0.35 s of the confirm. <c>InteractiveMaskPixelsTests</c> hold both to the plain per-pixel rules.
/// </remarks>
internal static class InteractiveMaskPixels
{
    /// <summary>
    /// The preview of <paramref name="mask"/> (Gray8): <paramref name="overlayColor"/> where the mask is above 127,
    /// transparent elsewhere, as unpremultiplied BGRA. The returned bitmap is the caller's to dispose.
    /// </summary>
    /// <remarks>
    /// Declared as what its words are: the canvas draws the point markers by the declared form, and the hand-off to
    /// Avalonia converts by it. Declared RGBA, red markers showed blue; declared premultiplied, the overlay drew
    /// brighter than its alpha.
    /// </remarks>
    public static SKBitmap CreateOverlay(SKBitmap mask, SKColor overlayColor)
    {
        int width = mask.Width;
        int height = mask.Height;
        var overlay = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Unpremul);
        uint overlayWord = (uint)overlayColor.Blue
            | ((uint)overlayColor.Green << 8)
            | ((uint)overlayColor.Red << 16)
            | ((uint)overlayColor.Alpha << 24);

        IntPtr maskPixels = mask.GetPixels();
        int maskRowBytes = mask.RowBytes;
        IntPtr overlayPixels = overlay.GetPixels();
        int overlayRowBytes = overlay.RowBytes;
        ForEachRowBand(height, (firstRow, endRow) =>
        {
            for (int y = firstRow; y < endRow; y++)
            {
                ReadOnlySpan<byte> maskRow = Row<byte>(maskPixels, maskRowBytes, y, width);
                Span<uint> overlayRow = Row<uint>(overlayPixels, overlayRowBytes, y, width);
                for (int x = 0; x < overlayRow.Length; x++)
                {
                    overlayRow[x] = maskRow[x] > 127 ? overlayWord : 0u;
                }
            }
        });

        return overlay;
    }

    /// <summary>
    /// A copy of the pin's premultiplied BGRA <paramref name="source"/> without the selected pixels: those where
    /// <paramref name="mask"/> (Gray8) is above 127, or, with <paramref name="keepSelected"/>, those where it is not.
    /// The returned bitmap is premultiplied, like the pixels it is copied from, and the caller's to dispose.
    /// </summary>
    /// <remarks>
    /// A removed pixel is all zero: its colour kept at alpha 0 would be drawn added to what lies behind, not hidden.
    /// The mask is normally the pin's own size. One of another size is stretched over the pin: pixel (x, y) reads
    /// mask pixel (x * maskWidth / width, y * maskHeight / height), rounded down.
    /// </remarks>
    public static SKBitmap ApplyToImage(SKBitmap source, SKBitmap mask, bool keepSelected)
    {
        int width = source.Width;
        int height = source.Height;
        int maskWidth = mask.Width;
        int maskHeight = mask.Height;
        var result = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
        double maskScaleX = (double)maskWidth / width;
        double maskScaleY = (double)maskHeight / height;

        // A pin column reads the same mask column on every row: work them out once.
        var maskColumns = new int[width];
        for (int x = 0; x < width; x++)
        {
            maskColumns[x] = Math.Clamp((int)(x * maskScaleX), 0, maskWidth - 1);
        }

        IntPtr sourcePixels = source.GetPixels();
        int sourceRowBytes = source.RowBytes;
        IntPtr maskPixels = mask.GetPixels();
        int maskRowBytes = mask.RowBytes;
        IntPtr resultPixels = result.GetPixels();
        int resultRowBytes = result.RowBytes;
        ForEachRowBand(height, (firstRow, endRow) =>
        {
            for (int y = firstRow; y < endRow; y++)
            {
                int maskY = Math.Clamp((int)(y * maskScaleY), 0, maskHeight - 1);
                ReadOnlySpan<byte> maskRow = Row<byte>(maskPixels, maskRowBytes, maskY, maskWidth);
                ReadOnlySpan<uint> sourceRow = Row<uint>(sourcePixels, sourceRowBytes, y, width);
                Span<uint> resultRow = Row<uint>(resultPixels, resultRowBytes, y, width);
                for (int x = 0; x < resultRow.Length; x++)
                {
                    bool removed = (maskRow[maskColumns[x]] > 127) != keepSelected;
                    resultRow[x] = removed ? 0u : sourceRow[x];
                }
            }
        });

        return result;
    }

    /// <summary>Runs <paramref name="body"/> over bands of rows [first, end) that together cover
    /// [0, <paramref name="rows"/>), on every core.</summary>
    /// <remarks>Both passes are memory-bound, and no row depends on another, so the bytes are the same either way.
    /// </remarks>
    private static void ForEachRowBand(int rows, Action<int, int> body)
    {
        if (rows <= 0)
        {
            return;
        }

        Parallel.ForEach(Partitioner.Create(0, rows), band => body(band.Item1, band.Item2));
    }

    private static unsafe Span<T> Row<T>(IntPtr pixels, int rowBytes, int row, int length) where T : unmanaged =>
        new((byte*)pixels + ((nint)row * rowBytes), length);
}
