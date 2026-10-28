using System;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace GimmeCapture.ViewModels.Shared;

/// <summary>
/// Draws decoded BGRA video frames into the WriteableBitmap a preview shows, cropped and then rotated clockwise on
/// the way (the same turn as <c>FloatingBitmapConversionHelper.TransformBitmap</c> and the encoder). The bitmap is
/// reused for as long as the frame size holds, instead of a new one per frame that nothing disposed. Drawing into a
/// bitmap the view already shows does not repaint it, so the owner then has the view invalidate its Image (the
/// pinned video's <c>RequestRedraw</c> does the same).
/// </summary>
internal static class VideoFrameBitmap
{
    /// <summary>
    /// Draws <paramref name="region"/> of a <paramref name="width"/>×<paramref name="height"/> frame, rotated by
    /// <paramref name="rotation"/>, into <paramref name="target"/> when it has the size that comes out, else into a new
    /// bitmap. Returns the bitmap drawn into, or null when <paramref name="bgra"/> holds no whole frame or the region
    /// misses it. A new bitmap is the caller's to show, after which it disposes the one that was shown. UI thread.
    /// </summary>
    public static unsafe WriteableBitmap? Draw(
        WriteableBitmap? target, ReadOnlySpan<byte> bgra, int width, int height, PixelRect region, int rotation)
    {
        if (width <= 0 || height <= 0 || bgra.Length < width * height * 4)
        {
            return null;
        }

        region = region.Intersect(new PixelRect(0, 0, width, height));
        if (region.Width <= 0 || region.Height <= 0)
        {
            return null;
        }

        PixelSize size = OutputSize(region, rotation);
        bool reuse = target != null && target.PixelSize == size;
        WriteableBitmap bitmap = reuse
            ? target!
            : new WriteableBitmap(size, new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        try
        {
            using ILockedFramebuffer locked = bitmap.Lock();
            var destination = new Span<byte>((void*)locked.Address, locked.RowBytes * size.Height);
            CopyRotated(bgra, width, region, rotation, destination, locked.RowBytes);
        }
        catch
        {
            if (!reuse)
            {
                bitmap.Dispose();
            }

            throw;
        }

        return bitmap;
    }

    /// <summary>The size <paramref name="region"/> comes out at: 90° and 270° swap its sides.</summary>
    internal static PixelSize OutputSize(PixelRect region, int rotation) =>
        rotation is 90 or 270
            ? new PixelSize(region.Height, region.Width)
            : new PixelSize(region.Width, region.Height);

    /// <summary>
    /// Copies <paramref name="region"/> of a tightly packed BGRA frame <paramref name="width"/> pixels wide into
    /// <paramref name="destination"/>, whose rows are <paramref name="destinationRowBytes"/> apart, turned clockwise by
    /// <paramref name="rotation"/> (0, 90, 180 or 270; anything else copies it as it is). The region must lie inside
    /// the frame.
    /// </summary>
    internal static void CopyRotated(
        ReadOnlySpan<byte> bgra, int width, PixelRect region, int rotation, Span<byte> destination, int destinationRowBytes)
    {
        ReadOnlySpan<uint> source = MemoryMarshal.Cast<byte, uint>(bgra);
        PixelSize size = OutputSize(region, rotation);
        int left = region.X;
        int top = region.Y;
        int right = region.X + region.Width - 1;
        int bottom = region.Y + region.Height - 1;

        for (int y = 0; y < size.Height; y++)
        {
            Span<uint> row = MemoryMarshal.Cast<byte, uint>(destination.Slice(y * destinationRowBytes, size.Width * 4));
            switch (rotation)
            {
                case 90:
                    // Output row y is source column left + y, read from the bottom up.
                    for (int x = 0; x < row.Length; x++)
                    {
                        row[x] = source[((bottom - x) * width) + left + y];
                    }

                    break;
                case 180:
                    // Output row y is source row bottom - y, read from the right.
                    int sourceRowEnd = ((bottom - y) * width) + right;
                    for (int x = 0; x < row.Length; x++)
                    {
                        row[x] = source[sourceRowEnd - x];
                    }

                    break;
                case 270:
                    // Output row y is source column right - y, read from the top down.
                    for (int x = 0; x < row.Length; x++)
                    {
                        row[x] = source[((top + x) * width) + right - y];
                    }

                    break;
                default:
                    source.Slice(((top + y) * width) + left, row.Length).CopyTo(row);
                    break;
            }
        }
    }
}
