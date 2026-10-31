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

    /// <summary>A dark disc and bar on a lit gradient, scaled to the size, so a model has a subject to find and
    /// background removal's shortcuts for a solid background do not apply.</summary>
    public static SKBitmap Scene(int width, int height)
    {
        var scene = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(scene);
        using var background = new SKPaint
        {
            Shader = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(width, height),
                [new SKColor(250, 220, 170), new SKColor(90, 140, 210)], SKShaderTileMode.Clamp),
        };
        canvas.DrawRect(new SKRect(0, 0, width, height), background);
        canvas.Scale(width / 320f, height / 320f);
        using var subject = new SKPaint { Color = new SKColor(40, 30, 30), IsAntialias = true };
        canvas.DrawCircle(160, 150, 80, subject);
        canvas.DrawRect(new SKRect(140, 220, 180, 300), subject);
        return scene;
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
