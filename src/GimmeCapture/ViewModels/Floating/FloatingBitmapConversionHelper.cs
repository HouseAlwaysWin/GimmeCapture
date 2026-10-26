using System;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using SkiaSharp;
using System.Buffers;
using System.Runtime.InteropServices;

namespace GimmeCapture.ViewModels.Floating;

internal static class FloatingBitmapConversionHelper
{
    public static byte[] EncodeBitmapToPngBytes(Bitmap bitmap)
    {
        if (!TryEncodeBitmapToPngBytes(bitmap, out var bytes, out var error))
            throw new Exception(error ?? "Failed to encode bitmap.");
        return bytes;
    }

    // zlib level 1 with the Sub filter: a 4K screenshot encodes in ~0.33 s instead of ~2.3 s, for ~18% more bytes.
    private static readonly SKPngEncoderOptions FastPng = new(SKPngEncoderFilterFlags.Sub, 1);

    /// <summary>
    /// PNG bytes that only travel in memory (the clipboard, background removal), so they use the fast PNG settings.
    /// </summary>
    public static bool TryEncodeBitmapToPngBytes(Bitmap? bitmap, out byte[] bytes, out string? error)
        => TryEncodeBitmap(bitmap, SKEncodedImageFormat.Png, 100, out bytes, out error, fastPng: true);

    /// <summary>
    /// Encodes an Avalonia bitmap to the given SkiaSharp format (Png/Jpeg/Webp…) at the given quality (0-100,
    /// ignored for lossless Png). Used by the pin's Save flow so the toolbar's chosen output format is honored.
    /// <paramref name="fastPng"/> trades ~18% size for ~7× speed on PNG; meant for bytes that never become the
    /// user's file.
    /// </summary>
    public static bool TryEncodeBitmap(
        Bitmap? bitmap, SKEncodedImageFormat format, int quality, out byte[] bytes, out string? error, bool fastPng = false)
    {
        bytes = Array.Empty<byte>();
        error = null;
        if (bitmap == null)
        {
            error = "Bitmap is null.";
            return false;
        }

        try
        {
            if (!TryCopyToSkBitmap(bitmap, out var skBitmap, out error) || skBitmap == null)
            {
                return false;
            }

            using (skBitmap)
            {
                SKBitmap encodeSource = skBitmap;
                SKBitmap? flattened = null;
                // JPEG has no alpha channel: composite a transparent pin over white so it doesn't export with a
                // black background (a premultiplied transparent pixel is RGB 0,0,0). PNG/WebP keep alpha as-is.
                if (format == SKEncodedImageFormat.Jpeg && skBitmap.AlphaType != SKAlphaType.Opaque)
                {
                    flattened = new SKBitmap(skBitmap.Width, skBitmap.Height, SKColorType.Bgra8888, SKAlphaType.Opaque);
                    using (var canvas = new SKCanvas(flattened))
                    {
                        canvas.Clear(SKColors.White);
                        canvas.DrawBitmap(skBitmap, 0, 0);
                    }
                    encodeSource = flattened;
                }

                try
                {
                    using var data = format == SKEncodedImageFormat.Png && fastPng
                        ? EncodeFastPng(encodeSource)
                        : EncodeWithQuality(encodeSource, format, quality);
                    if (data == null)
                    {
                        error = "Encoded bitmap bytes are empty.";
                        return false;
                    }

                    bytes = data.ToArray();
                }
                finally
                {
                    flattened?.Dispose();
                }
            }
            if (bytes.Length == 0)
            {
                error = "Encoded bitmap bytes are empty.";
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            error = $"Bitmap encode failed: {ex.Message}";
            return false;
        }
    }

    internal static SKData? EncodeFastPng(SKBitmap source)
    {
        using var pixmap = source.PeekPixels();
        return pixmap?.Encode(FastPng);
    }

    private static SKData? EncodeWithQuality(SKBitmap source, SKEncodedImageFormat format, int quality)
    {
        using var image = SKImage.FromBitmap(source);
        return image.Encode(format, quality);
    }

    public static SKBitmap? ToSkBitmap(Bitmap? avaloniaBitmap)
    {
        return TryCopyToSkBitmap(avaloniaBitmap, out var skBitmap, out _) ? skBitmap : null;
    }

    /// <summary>
    /// Returns a new bitmap rotated by <paramref name="rotationDegrees"/> (0/90/180/270, clockwise) and/or
    /// mirrored. Dimensions swap for 90°/270°. Returns null on conversion failure.
    /// </summary>
    public static Bitmap? TransformBitmap(Bitmap? source, int rotationDegrees, bool flipHorizontal, bool flipVertical)
    {
        if (source == null)
        {
            return null;
        }

        using SKBitmap? sk = ToSkBitmap(source);
        if (sk == null)
        {
            return null;
        }

        bool swap = rotationDegrees == 90 || rotationDegrees == 270;
        int dstW = swap ? sk.Height : sk.Width;
        int dstH = swap ? sk.Width : sk.Height;

        using var dst = new SKBitmap(dstW, dstH, sk.ColorType, sk.AlphaType);
        using (var canvas = new SKCanvas(dst))
        {
            canvas.Clear(SKColors.Transparent);
            // Rotate / flip about the centre, then draw the source centred.
            canvas.Translate(dstW / 2f, dstH / 2f);
            canvas.RotateDegrees(rotationDegrees);
            canvas.Scale(flipHorizontal ? -1f : 1f, flipVertical ? -1f : 1f);
            canvas.Translate(-sk.Width / 2f, -sk.Height / 2f);
            canvas.DrawBitmap(sk, 0, 0);
        }

        return TryCreateDetachedBitmapFromSkBitmap(dst, out Bitmap? result, out _) ? result : null;
    }

    public static bool TryCopyToSkBitmap(Bitmap? bitmap, out SKBitmap? skBitmap, out string? error)
    {
        skBitmap = null;
        error = null;
        if (bitmap == null)
        {
            error = "Bitmap is null.";
            return false;
        }

        int width = bitmap.PixelSize.Width;
        int height = bitmap.PixelSize.Height;
        if (width <= 0 || height <= 0)
        {
            error = "Bitmap has invalid dimensions.";
            return false;
        }

        var result = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
        byte[] rented = ArrayPool<byte>.Shared.Rent(width * height * 4);

        try
        {
            var handle = GCHandle.Alloc(rented, GCHandleType.Pinned);
            try
            {
                bitmap.CopyPixels(
                    new PixelRect(0, 0, width, height),
                    handle.AddrOfPinnedObject(),
                    width * height * 4,
                    width * 4);
            }
            finally
            {
                handle.Free();
            }

            unsafe
            {
                byte* srcBase = (byte*)Marshal.UnsafeAddrOfPinnedArrayElement(rented, 0);
                byte* dstBase = (byte*)result.GetPixels();
                for (int row = 0; row < height; row++)
                {
                    Buffer.MemoryCopy(
                        srcBase + (row * width * 4),
                        dstBase + (row * result.RowBytes),
                        result.RowBytes,
                        width * 4);
                }
            }

            skBitmap = result;
            return true;
        }
        catch (Exception ex)
        {
            result.Dispose();
            error = $"Bitmap to SKBitmap copy failed: {ex.Message}";
            return false;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    public static bool TryCreateDetachedBitmapFromSkBitmap(SKBitmap? skBitmap, out Bitmap? bitmap, out string? error)
    {
        bitmap = null;
        error = null;
        if (skBitmap == null)
        {
            error = "SKBitmap is null.";
            return false;
        }

        try
        {
            var result = new WriteableBitmap(
                new PixelSize(skBitmap.Width, skBitmap.Height),
                new Vector(96, 96),
                PixelFormat.Bgra8888,
                AlphaFormat.Premul);

            using var locked = result.Lock();
            unsafe
            {
                byte* srcBase = (byte*)skBitmap.GetPixels();
                byte* dstBase = (byte*)locked.Address;
                int rows = Math.Min(skBitmap.Height, locked.Size.Height);
                int bytesPerRow = Math.Min(skBitmap.RowBytes, locked.RowBytes);

                for (int row = 0; row < rows; row++)
                {
                    Buffer.MemoryCopy(
                        srcBase + (row * skBitmap.RowBytes),
                        dstBase + (row * locked.RowBytes),
                        locked.RowBytes,
                        bytesPerRow);
                }
            }

            bitmap = result;
            return true;
        }
        catch (Exception ex)
        {
            error = $"Detached bitmap creation from SKBitmap failed: {ex.Message}";
            return false;
        }
    }
}
