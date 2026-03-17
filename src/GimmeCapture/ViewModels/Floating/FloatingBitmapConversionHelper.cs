using System;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using SkiaSharp;

namespace GimmeCapture.ViewModels.Floating;

internal static class FloatingBitmapConversionHelper
{
    public static byte[] EncodeBitmapToPngBytes(Bitmap bitmap)
    {
        if (!TryEncodeBitmapToPngBytes(bitmap, out var bytes, out var error))
            throw new Exception(error ?? "Failed to encode bitmap.");
        return bytes;
    }

    public static bool TryEncodeBitmapToPngBytes(Bitmap? bitmap, out byte[] bytes, out string? error)
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
        using var ms = new System.IO.MemoryStream();
        bitmap.Save(ms);
            bytes = ms.ToArray();
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

    public static SKBitmap? ToSkBitmap(Bitmap? avaloniaBitmap)
    {
        if (avaloniaBitmap == null) return null;
        try
        {
            using var ms = new System.IO.MemoryStream();
            avaloniaBitmap.Save(ms);
            ms.Seek(0, System.IO.SeekOrigin.Begin);
            return SKBitmap.Decode(ms);
        }
        catch
        {
            return null;
        }
    }

    public static Bitmap CreateDetachedBitmapFromEncodedBytes(byte[] encodedBytes)
    {
        if (!TryCreateDetachedBitmapFromEncodedBytes(encodedBytes, out var bitmap, out var error))
            throw new Exception(error ?? "Failed to create detached bitmap.");
        return bitmap!;
    }

    public static bool TryCreateDetachedBitmapFromEncodedBytes(byte[]? encodedBytes, out Bitmap? bitmap, out string? error)
    {
        bitmap = null;
        error = null;
        if (encodedBytes == null || encodedBytes.Length == 0)
        {
            error = "Encoded bitmap bytes are empty.";
            return false;
        }

        try
        {
            using var ms = new System.IO.MemoryStream(encodedBytes);
            using var tempBitmap = new Bitmap(ms);

            var result = new WriteableBitmap(
                tempBitmap.PixelSize,
                tempBitmap.Dpi,
                PixelFormat.Bgra8888,
                AlphaFormat.Premul);

            using var locked = result.Lock();
            tempBitmap.CopyPixels(
                new PixelRect(0, 0, tempBitmap.PixelSize.Width, tempBitmap.PixelSize.Height),
                locked.Address,
                locked.RowBytes * locked.Size.Height,
                locked.RowBytes);

            bitmap = result;
            return true;
        }
        catch (Exception ex)
        {
            error = $"Detached bitmap creation failed: {ex.Message}";
            return false;
        }
    }
}
