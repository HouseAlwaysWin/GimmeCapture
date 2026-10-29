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
    public static WriteableBitmap Create(int width, int height, Func<int, int, uint> pixel)
    {
        EnsureRegistered();
        var bitmap = new WriteableBitmap(new PixelSize(width, height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        Fill(bitmap, pixel);
        return bitmap;
    }

    /// <summary>Writes <paramref name="pixel"/>(x, y) to every pixel of a BGRA <paramref name="bitmap"/>.</summary>
    public static unsafe void Fill(WriteableBitmap bitmap, Func<int, int, uint> pixel)
    {
        using var locked = bitmap.Lock();
        for (int y = 0; y < locked.Size.Height; y++)
        {
            uint* row = (uint*)(locked.Address + (y * locked.RowBytes));
            for (int x = 0; x < locked.Size.Width; x++)
            {
                row[x] = pixel(x, y);
            }
        }
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

/// <summary>
/// A pin's bitmap whose pixel copies a test can stop at the door. Work that starts by copying the pin, as a smart
/// selection's confirm does, then waits there, at a known point, while the test does something else.
/// </summary>
internal sealed class HeldCopyBitmap : WriteableBitmap
{
    private readonly object _lock = new();
    private TaskCompletionSource? _hold;
    private TaskCompletionSource _copyWaiting = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>A premultiplied BGRA bitmap, as <see cref="AvaloniaRenderer.Create"/> makes them.</summary>
    public HeldCopyBitmap(int width, int height, Func<int, int, uint> pixel)
        : base(new PixelSize(width, height), new Vector(96, 96), PixelFormat.Bgra8888, Avalonia.Platform.AlphaFormat.Premul)
    {
        AvaloniaRenderer.Fill(this, pixel);
    }

    /// <summary>Done once a copy held since the last <see cref="HoldCopies"/> is waiting at the door.</summary>
    public Task CopyWaiting
    {
        get
        {
            lock (_lock)
            {
                return _copyWaiting.Task;
            }
        }
    }

    /// <summary>Holds every copy from now on until <see cref="ReleaseCopies"/>.</summary>
    public void HoldCopies()
    {
        lock (_lock)
        {
            _copyWaiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    public void ReleaseCopies()
    {
        TaskCompletionSource? hold;
        lock (_lock)
        {
            hold = _hold;
            _hold = null;
        }

        hold?.TrySetResult();
    }

    public override void CopyPixels(PixelRect sourceRect, IntPtr buffer, int bufferSize, int stride)
    {
        TaskCompletionSource? hold;
        lock (_lock)
        {
            hold = _hold;
            if (hold != null)
            {
                _copyWaiting.TrySetResult();
            }
        }

        // Blocks whatever thread is copying, which is the point: it is the work being held.
        hold?.Task.Wait();
        base.CopyPixels(sourceRect, buffer, bufferSize, stride);
    }
}

/// <summary>The tests that need <see cref="AvaloniaRenderer"/>: run on their own, after the rest.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class AvaloniaRendererCollection
{
    public const string Name = "Avalonia renderer";
}
