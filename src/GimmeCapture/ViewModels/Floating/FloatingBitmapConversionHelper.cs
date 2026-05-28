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
            if (!TryCopyToSkBitmap(bitmap, out var skBitmap, out error) || skBitmap == null)
            {
                return false;
            }

            using (skBitmap)
            {
                using var image = SKImage.FromBitmap(skBitmap);
                using var data = image.Encode(SKEncodedImageFormat.Png, 100);
                if (data == null)
                {
                    error = "Encoded bitmap bytes are empty.";
                    return false;
                }

                bytes = data.ToArray();
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

    public static SKBitmap? ToSkBitmap(Bitmap? avaloniaBitmap)
    {
        return TryCopyToSkBitmap(avaloniaBitmap, out var skBitmap, out _) ? skBitmap : null;
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
