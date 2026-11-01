using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia;
using GimmeCapture.Services.Abstractions;
using GimmeCapture.Services.Platforms.Windows;
using SkiaSharp;
using Xunit.Abstractions;

namespace GimmeCapture.Tests;

/// <summary>
/// The grab a frozen screenshot makes before its overlay shows, of the whole virtual screen at its physical size: the
/// service's, against the GDI+ CopyFromScreen it used before, in turn, and the pixels of the two compared. Opt-in
/// (FREEZE_GRAB_PROBE=1): it grabs the real screen, which should sit still meanwhile.
/// </summary>
[Collection(AvaloniaRendererCollection.Name)]
public sealed class FreezeGrabProbeTests
{
    private const int Runs = 15;
    private readonly ITestOutputHelper _output;

    public FreezeGrabProbeTests(ITestOutputHelper output) => _output = output;

    [DllImport("user32.dll")]
    private static extern bool SetProcessDpiAwarenessContext(IntPtr value);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [OptInFact("FREEZE_GRAB_PROBE=1")]
    public void TheFreezeGrab_ReadsWhatGdiPlusRead_Sooner()
    {
        // Physical pixels, as the app sees them (it is per-monitor DPI aware).
        SetProcessDpiAwarenessContext(new IntPtr(-4));
        int x = GetSystemMetrics(76), y = GetSystemMetrics(77), width = GetSystemMetrics(78), height = GetSystemMetrics(79);
        _output.WriteLine($"virtual screen {width}x{height} at ({x}, {y})");
        var service = new WindowsScreenCaptureService(new Mock<IWindowManager>().Object);
        var region = new Rect(0, 0, width, height);
        var offset = new PixelPoint(x, y);

        var now = new List<double>();
        var before = new List<double>();
        for (int i = 0; i < Runs; i++)
        {
            var watch = Stopwatch.StartNew();
            using var grabbed = service.CaptureScreenAsync(region, offset, 1.0).GetAwaiter().GetResult();
            now.Add(watch.Elapsed.TotalMilliseconds);
            watch.Restart();
            using var gdiPlus = GdiPlusGrab(x, y, width, height);
            before.Add(watch.Elapsed.TotalMilliseconds);
            if (i == 0)
            {
                // What the screen itself changes between two grabs of the same kind, against what differs between kinds.
                using var gdiPlusAgain = GdiPlusGrab(x, y, width, height);
                using var grabbedAgain = service.CaptureScreenAsync(region, offset, 1.0).GetAwaiter().GetResult();
                long total = (long)width * height;
                _output.WriteLine(
                    $"pixels the same: now/GDI+ {CountSame(grabbed, gdiPlus, out var box) * 100.0 / total:F3}% (differing within {box}), " +
                    $"GDI+/GDI+ {CountSame(gdiPlus, gdiPlusAgain, out var gdiBox) * 100.0 / total:F3}% (within {gdiBox}), " +
                    $"now/now {CountSame(grabbed, grabbedAgain, out var nowBox) * 100.0 / total:F3}% (within {nowBox}); " +
                    $"alpha {grabbed.GetPixel(width / 2, height / 2).Alpha}");
            }
        }

        _output.WriteLine($"now: median {Median(now):F0} ms (fastest {now.Min():F0}); GDI+: median {Median(before):F0} ms (fastest {before.Min():F0})");

        // What a frozen overlay holds of it: the grab and its display copy were both kept, 2 x 4 bytes a pixel.
        AvaloniaRenderer.EnsureRegistered();
        long held = PrivateBytes();
        var made = Stopwatch.StartNew();
        using (var backdrop = OverlayBackdrop.TryCreate(
            service.CaptureScreenAsync(region, offset, 1.0).GetAwaiter().GetResult(), 1.0, OverlayBackdrop.DefaultToDisplay))
        {
            double makeMs = made.Elapsed.TotalMilliseconds;
            long frozen = PrivateBytes() - held;
            double onePicture = (double)width * height * 4 / (1024 * 1024);
            _output.WriteLine($"a frozen backdrop holds {frozen / (1024.0 * 1024):F1} MB (one picture is {onePicture:F1}); grab and display copy took {makeMs:F0} ms");
            Assert.InRange(frozen, long.MinValue, (long)(onePicture * 1.5 * 1024 * 1024));
        }

        // As the backdrop was: the grab kept beside the copy shown.
        held = PrivateBytes();
        using (var grab = service.CaptureScreenAsync(region, offset, 1.0).GetAwaiter().GetResult())
        using (var shown = OverlayBackdrop.DefaultToDisplay(grab))
        {
            _output.WriteLine($"the grab beside its display copy, as kept before: {(PrivateBytes() - held) / (1024.0 * 1024):F1} MB");
        }

        // Where the screen holds still, the two must read the same: a window of the probe's own, kept on top.
        using var pattern = new System.Drawing.Bitmap(400, 300);
        for (int py = 0; py < 300; py++)
        {
            for (int px = 0; px < 400; px++)
            {
                pattern.SetPixel(px, py, System.Drawing.Color.FromArgb((px * 7) % 256, (py * 13) % 256, (px + py) % 256));
            }
        }

        using var form = new System.Windows.Forms.Form
        {
            FormBorderStyle = System.Windows.Forms.FormBorderStyle.None,
            StartPosition = System.Windows.Forms.FormStartPosition.Manual,
            Location = new System.Drawing.Point(100, 100),
            Size = new System.Drawing.Size(400, 300),
            TopMost = true,
            ShowInTaskbar = false,
            BackgroundImage = pattern,
            BackgroundImageLayout = System.Windows.Forms.ImageLayout.None,
        };
        form.Show();
        for (int i = 0; i < 10; i++)
        {
            System.Windows.Forms.Application.DoEvents();
            Thread.Sleep(30);
        }

        var inside = new Rect(form.Left + 20, form.Top + 20, 300, 200);
        using var ours = service.CaptureScreenAsync(inside, default, 1.0).GetAwaiter().GetResult();
        using var theirs = GdiPlusGrab((int)inside.X, (int)inside.Y, 300, 200);
        long equal = CountSame(ours, theirs, out var differing);
        form.Close();
        _output.WriteLine($"a still window: {equal} of {300 * 200} pixels the same (differing within {differing})");
        Assert.Equal(300 * 200, equal);
    }

    // WindowsScreenCaptureService.CaptureScreenAsync before it read into a DIB section.
    private static unsafe SKBitmap GdiPlusGrab(int x, int y, int width, int height)
    {
        using var bitmap = new System.Drawing.Bitmap(width, height);
        using (var g = System.Drawing.Graphics.FromImage(bitmap))
        {
            g.CopyFromScreen(x, y, 0, 0, new System.Drawing.Size(width, height));
        }

        var skBitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
        var data = bitmap.LockBits(
            new System.Drawing.Rectangle(0, 0, width, height),
            System.Drawing.Imaging.ImageLockMode.ReadOnly,
            System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        try
        {
            for (int row = 0; row < height; row++)
            {
                Buffer.MemoryCopy(
                    (byte*)data.Scan0 + ((long)row * data.Stride),
                    (byte*)skBitmap.GetPixels() + ((long)row * skBitmap.RowBytes),
                    skBitmap.RowBytes,
                    width * 4);
            }
        }
        finally
        {
            bitmap.UnlockBits(data);
        }

        return skBitmap;
    }

    private static unsafe long CountSame(SKBitmap a, SKBitmap b, out string differing)
    {
        int width = a.Width, height = a.Height;
        uint* pa = (uint*)a.GetPixels();
        uint* pb = (uint*)b.GetPixels();
        long same = 0;
        int left = int.MaxValue, top = int.MaxValue, right = -1, bottom = -1;
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                long i = ((long)y * width) + x;
                if (pa[i] == pb[i])
                {
                    same++;
                    continue;
                }

                left = Math.Min(left, x);
                top = Math.Min(top, y);
                right = Math.Max(right, x);
                bottom = Math.Max(bottom, y);
            }
        }

        differing = right < 0 ? "nothing" : $"({left}, {top})-({right}, {bottom})";
        return same;
    }

    private static long PrivateBytes()
    {
        GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        using var process = Process.GetCurrentProcess();
        return process.PrivateMemorySize64;
    }

    private static double Median(List<double> values)
    {
        var sorted = values.Order().ToList();
        return sorted[sorted.Count / 2];
    }
}
