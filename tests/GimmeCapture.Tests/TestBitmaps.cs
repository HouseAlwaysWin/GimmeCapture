using System;
using System.Runtime.InteropServices;
using SkiaSharp;

namespace GimmeCapture.Tests;

/// <summary>Bitmaps for the pixel-loop tests, built and read a whole pixel at a time.</summary>
internal static class TestBitmaps
{
    /// <summary>The byte <see cref="Create{T}"/> fills each row's padding with.</summary>
    public const byte Padding = 0xCD;

    /// <summary>
    /// A bitmap with <paramref name="pixel"/>(x, y) at every pixel, as one <typeparamref name="T"/> (byte for Gray8,
    /// uint for a 32-bit type, read as the little-endian word it is in memory). With <paramref name="padBytes"/>,
    /// each row is that much longer than its pixels and the rest is <see cref="Padding"/>, so code that steps
    /// through rows by their width instead of their stride reads the wrong pixels.
    /// </summary>
    public static SKBitmap Create<T>(
        int width, int height, SKColorType colorType, SKAlphaType alphaType, Func<int, int, T> pixel, int padBytes = 0)
        where T : unmanaged
    {
        var info = new SKImageInfo(width, height, colorType, alphaType);
        if (info.BytesPerPixel != Marshal.SizeOf<T>())
        {
            throw new ArgumentException($"{colorType} pixels are not {Marshal.SizeOf<T>()} bytes.", nameof(colorType));
        }

        var bitmap = new SKBitmap(info, info.RowBytes + padBytes);
        Span<byte> bytes = bitmap.GetPixelSpan();
        bytes.Fill(Padding);
        int rowBytes = bitmap.RowBytes;
        for (int y = 0; y < height; y++)
        {
            Span<T> row = MemoryMarshal.Cast<byte, T>(bytes.Slice(y * rowBytes, width * info.BytesPerPixel));
            for (int x = 0; x < width; x++)
            {
                row[x] = pixel(x, y);
            }
        }

        return bitmap;
    }

    /// <summary>The bitmap's pixels row after row, without the padding at the end of each row.</summary>
    public static T[] ReadPixels<T>(SKBitmap bitmap) where T : unmanaged
    {
        ReadOnlySpan<byte> bytes = bitmap.GetPixelSpan();
        int width = bitmap.Width;
        int rowBytes = bitmap.RowBytes;
        var pixels = new T[width * bitmap.Height];
        for (int y = 0; y < bitmap.Height; y++)
        {
            MemoryMarshal.Cast<byte, T>(bytes.Slice(y * rowBytes, width * Marshal.SizeOf<T>())).CopyTo(pixels.AsSpan(y * width));
        }

        return pixels;
    }

    /// <summary>A repeatable, well-mixed 32-bit value for each pixel.</summary>
    public static uint Noise(int x, int y, int seed = 0)
    {
        uint hash = unchecked((uint)((x * 73856093) ^ (y * 19349663) ^ (seed * 83492791)));
        hash ^= hash >> 13;
        hash = unchecked(hash * 0x5BD1E995u);
        return hash ^ (hash >> 15);
    }
}
