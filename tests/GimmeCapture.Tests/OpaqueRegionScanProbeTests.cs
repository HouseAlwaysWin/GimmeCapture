using System.Buffers;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using GimmeCapture.Services.Core.Interaction;
using Xunit.Abstractions;

namespace GimmeCapture.Tests;

/// <summary>
/// The scan a pin runs on the UI thread whenever its picture changes, to let clicks through its see-through parts, on a
/// 4K picture opaque as a capture is and cut out as after a background removal: its time against the scan before it
/// (the whole picture copied into a pooled array, then read a pixel at a time), each warmed up first, and what each
/// leaves behind. Opt-in (OPAQUE_SCAN_PROBE=1): it times work and reads the private bytes of the whole test process,
/// so it is best run on its own.
/// </summary>
[Collection(AvaloniaRendererCollection.Name)]
public sealed class OpaqueRegionScanProbeTests
{
    private const int Width = 3840;
    private const int Height = 2160;
    private const int WarmUps = 5;
    private const int Runs = 31;
    private readonly ITestOutputHelper _output;

    public OpaqueRegionScanProbeTests(ITestOutputHelper output) => _output = output;

    [OptInFact("OPAQUE_SCAN_PROBE=1")]
    public void AScanOfA4KPicture_IsQuick_AndKeepsNothing()
    {
        using var opaque = AvaloniaRenderer.Create(Width, Height, static (x, y) =>
            0xFF000000u | ((uint)(x * 7 % 256) << 16) | ((uint)(y * 13 % 256) << 8) | (uint)((x + y) % 256));
        // A subject in the middle, its background removed: an ellipse with a ragged edge.
        using var cutOut = AvaloniaRenderer.Create(Width, Height, static (x, y) =>
        {
            double dx = (x - (Width / 2.0)) / (Width * 0.3), dy = (y - (Height / 2.0)) / (Height * 0.4);
            return (dx * dx) + (dy * dy) + (((x * 31) ^ (y * 17)) % 7 * 0.004) <= 1 ? 0xFF406080u : 0x00000000u;
        });
        var pictures = new[] { ("opaque", opaque), ("cut out", cutOut) };

        // The new scan alone first, so the pooled array the previous one rents is not yet in the process.
        long before = PrivateMegabytes();
        _output.WriteLine($"now: first scan of the process {TimeOnce(OpaqueRegionScanner.Scan, opaque):F1} ms");
        foreach (var (_, picture) in pictures)
        {
            for (int i = 0; i < WarmUps; i++)
            {
                OpaqueRegionScanner.Scan(picture);
            }
        }

        long leftBehind = PrivateMegabytes() - before;
        _output.WriteLine($"now: left behind private bytes +{leftBehind} MB");
        // The previous scan's array, which the shared pool keeps, is 32 MiB at 4K.
        Assert.InRange(leftBehind, long.MinValue, 8);
        before = PrivateMegabytes();
        _output.WriteLine($"previous: first scan of the process {TimeOnce(PreviousScan, opaque):F1} ms");
        foreach (var (_, picture) in pictures)
        {
            for (int i = 0; i < WarmUps; i++)
            {
                PreviousScan(picture);
            }
        }

        _output.WriteLine($"previous: left behind private bytes +{PrivateMegabytes() - before} MB");

        // Then the two in turn, so whatever else the machine does slows both alike.
        foreach (var (name, picture) in pictures)
        {
            var now = new List<double>();
            var previous = new List<double>();
            for (int i = 0; i < Runs; i++)
            {
                var watch = Stopwatch.StartNew();
                var nowRects = OpaqueRegionScanner.Scan(picture);
                now.Add(watch.Elapsed.TotalMilliseconds);
                watch.Restart();
                var previousRects = PreviousScan(picture);
                previous.Add(watch.Elapsed.TotalMilliseconds);
                Assert.Equal(previousRects, nowRects);
            }

            now.Sort();
            previous.Sort();
            _output.WriteLine(
                $"{name}: now median {now[Runs / 2]:F1} ms (fastest {now[0]:F1}), previous median {previous[Runs / 2]:F1} ms (fastest {previous[0]:F1})");
        }
    }

    private static double TimeOnce(Func<Bitmap, List<PixelRect>> scan, Bitmap picture)
    {
        var watch = Stopwatch.StartNew();
        scan(picture);
        return watch.Elapsed.TotalMilliseconds;
    }

    // FloatingImageWindow's scan before it read the picture in bands: the whole picture into a pooled array, read a
    // pixel at a time.
    private static List<PixelRect> PreviousScan(Bitmap bitmap)
    {
        int width = bitmap.PixelSize.Width, height = bitmap.PixelSize.Height, stride = width * 4;
        byte[] rented = ArrayPool<byte>.Shared.Rent(stride * height);
        var handle = GCHandle.Alloc(rented, GCHandleType.Pinned);
        try
        {
            bitmap.CopyPixels(new PixelRect(0, 0, width, height), handle.AddrOfPinnedObject(), stride * height, stride);
        }
        finally
        {
            handle.Free();
        }

        var result = new List<PixelRect>();
        var active = new Dictionary<(int Start, int Length), int>();
        var next = new Dictionary<(int Start, int Length), int>();
        for (int y = 0; y < height; y++)
        {
            ReadOnlySpan<byte> row = rented.AsSpan(y * stride, width * 4);
            int x = 0;
            while (x < width)
            {
                if (row[(x * 4) + 3] <= OpaqueRegionScanner.TransparentAlpha)
                {
                    x++;
                    continue;
                }

                int start = x++;
                while (x < width && row[(x * 4) + 3] > OpaqueRegionScanner.TransparentAlpha)
                {
                    x++;
                }

                var key = (start, x - start);
                next[key] = active.Remove(key, out int top) ? top : y;
            }

            foreach (var ((start, length), top) in active)
            {
                result.Add(new PixelRect(start, top, length, y - top));
            }

            (active, next) = (next, active);
            next.Clear();
        }

        foreach (var ((start, length), top) in active)
        {
            result.Add(new PixelRect(start, top, length, height - top));
        }

        ArrayPool<byte>.Shared.Return(rented);
        return result;
    }

    private static long PrivateMegabytes()
    {
        GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        using var process = Process.GetCurrentProcess();
        return process.PrivateMemorySize64 / (1024 * 1024);
    }
}
