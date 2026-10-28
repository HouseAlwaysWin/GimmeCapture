using System;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Skia;

namespace GimmeCapture.Tests;

/// <summary>
/// Avalonia's Skia renderer, which every Avalonia bitmap needs, and bitmaps built and read a whole pixel at a time.
/// </summary>
/// <remarks>
/// The renderer goes into Avalonia's service registry, which is process-wide and not thread-safe, so a test that uses it
/// belongs to <see cref="AvaloniaRendererCollection"/>: no other test runs, and reads the registry, meanwhile.
/// </remarks>
internal static class AvaloniaRenderer
{
    private static readonly Lazy<bool> Registered = new(() =>
    {
        SkiaPlatform.Initialize();
        return true;
    });

    public static void EnsureRegistered() => _ = Registered.Value;

    /// <summary>
    /// A premultiplied BGRA bitmap, the pin's form, with <paramref name="pixel"/>(x, y) at every pixel as the word it
    /// is in memory (0xAARRGGBB).
    /// </summary>
    public static unsafe WriteableBitmap Create(int width, int height, Func<int, int, uint> pixel)
    {
        EnsureRegistered();
        var bitmap = new WriteableBitmap(new PixelSize(width, height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        using var locked = bitmap.Lock();
        for (int y = 0; y < height; y++)
        {
            uint* row = (uint*)(locked.Address + (y * locked.RowBytes));
            for (int x = 0; x < width; x++)
            {
                row[x] = pixel(x, y);
            }
        }

        return bitmap;
    }

    /// <summary>The bitmap's pixels row after row, each as the word it is in memory (0xAARRGGBB for BGRA).</summary>
    public static unsafe uint[] ReadPixels(Bitmap bitmap)
    {
        PixelSize size = bitmap.PixelSize;
        var pixels = new uint[size.Width * size.Height];
        fixed (uint* buffer = pixels)
        {
            bitmap.CopyPixels(new PixelRect(0, 0, size.Width, size.Height), (IntPtr)buffer, pixels.Length * 4, size.Width * 4);
        }

        return pixels;
    }
}

/// <summary>The tests that need <see cref="AvaloniaRenderer"/>: run on their own, after the rest.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class AvaloniaRendererCollection
{
    public const string Name = "Avalonia renderer";
}
