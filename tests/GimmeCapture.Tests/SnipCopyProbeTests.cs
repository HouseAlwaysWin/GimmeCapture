using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia;
using GimmeCapture.Services.Abstractions;
using GimmeCapture.Services.Platforms.Windows;
using GimmeCapture.ViewModels.Floating;
using SkiaSharp;
using Xunit.Abstractions;

namespace GimmeCapture.Tests;

/// <summary>
/// What an overlay's copy waits for before it closes, on a grab of the real desktop: the PNG encodes, the GDI+
/// decode for CF_BITMAP and the history file, as the copy did them before and does them now, in turn. Opt-in
/// (SNIP_COPY_PROBE=1): it grabs the real screen. SNIP_COPY_PROBE=clipboard writes a picture to the real clipboard
/// instead, replacing what it holds, and reads back what an app pasting it gets.
/// </summary>
public sealed class SnipCopyProbeTests
{
    private const int Runs = 8;
    private readonly ITestOutputHelper _output;

    public SnipCopyProbeTests(ITestOutputHelper output) => _output = output;

    [DllImport("user32.dll")]
    private static extern bool SetProcessDpiAwarenessContext(IntPtr value);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [OptInFact("SNIP_COPY_PROBE=1")]
    public void ACopy_EncodesOnce_AndWaitsOnlyForTheClipboard()
    {
        SetProcessDpiAwarenessContext(new IntPtr(-4));
        int x = GetSystemMetrics(76), y = GetSystemMetrics(77), width = GetSystemMetrics(78), height = GetSystemMetrics(79);
        var service = new WindowsScreenCaptureService(new Mock<IWindowManager>().Object);
        using var desktop = service.CaptureScreenAsync(new Rect(0, 0, width, height), new PixelPoint(x, y), 1.0)
            .GetAwaiter().GetResult();
        _output.WriteLine($"desktop {width}x{height}");

        string folder = Path.Combine(Path.GetTempPath(), "GimmeCapture.SnipCopyProbe." + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var before = new List<(double Clipboard, double Decode, double History, long ClipboardBytes, long FileBytes)>();
            var now = new List<(double Clipboard, double Decode, double History, long ClipboardBytes, long FileBytes)>();
            for (int i = 0; i < Runs; i++)
            {
                before.Add(Before(desktop, Path.Combine(folder, $"before{i}.png")));
                now.Add(Now(desktop, Path.Combine(folder, $"now{i}.png")));
            }

            Report("before", before);
            Report("now", now);

            // The history file is the clipboard's PNG, and it holds the picture as it was.
            using var reread = SKBitmap.Decode(Path.Combine(folder, "now0.png"));
            Assert.Equal(desktop.Width, reread.Width);
            Assert.Equal(desktop.Height, reread.Height);
            Assert.True(desktop.GetPixelSpan().SequenceEqual(reread.GetPixelSpan()));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [OptInFact("SNIP_COPY_PROBE=clipboard")]
    public async Task TheCopy_PutsThePngAndABitmapOfIt_OnTheClipboard()
    {
        using var picture = new SKBitmap(64, 48);
        picture.Erase(SKColors.CornflowerBlue);
        picture.SetPixel(1, 2, SKColors.Red);
        using var encoded = FloatingBitmapConversionHelper.EncodeFastPng(picture);
        var service = new WindowsScreenCaptureService(new Mock<IWindowManager>().Object);

        Assert.True(await service.CopyPngToClipboardAsync(encoded!.ToArray()));

        // Read as another app would, from what the clipboard holds once the write has flushed it.
        byte[]? pastedPng = null;
        System.Drawing.Size bitmapSize = default;
        System.Drawing.Color bitmapPixel = default;
        var reader = new Thread(() =>
        {
            var data = System.Windows.Forms.Clipboard.GetDataObject();
            if (data?.GetData("PNG") is MemoryStream stream)
            {
                pastedPng = stream.ToArray();
            }

            if (data?.GetData(System.Windows.Forms.DataFormats.Bitmap) is System.Drawing.Bitmap bitmap)
            {
                using (bitmap)
                {
                    bitmapSize = bitmap.Size;
                    bitmapPixel = bitmap.GetPixel(1, 2);
                }
            }
        });
        reader.SetApartmentState(ApartmentState.STA);
        reader.Start();
        reader.Join();

        Assert.NotNull(pastedPng);
        using var pasted = SKBitmap.Decode(pastedPng);
        Assert.Equal((64, 48), (pasted.Width, pasted.Height));
        Assert.Equal(SKColors.Red, pasted.GetPixel(1, 2));
        Assert.Equal(SKColors.CornflowerBlue, pasted.GetPixel(30, 20));
        Assert.Equal(new System.Drawing.Size(64, 48), bitmapSize);
        Assert.Equal(System.Drawing.Color.Red.ToArgb(), bitmapPixel.ToArgb());
    }

    private static (double, double, double, long, long) Before(SKBitmap bitmap, string historyPath)
    {
        var watch = Stopwatch.StartNew();
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        using var ms = new MemoryStream();
        data.AsStream().CopyTo(ms);
        double encode = watch.Elapsed.TotalMilliseconds;

        watch.Restart();
        ms.Position = 0;
        using (new System.Drawing.Bitmap(ms))
        {
        }

        double decode = watch.Elapsed.TotalMilliseconds;

        watch.Restart();
        using (var fs = File.OpenWrite(historyPath))
        {
            bitmap.Encode(fs, SKEncodedImageFormat.Png, 100);
        }

        return (encode, decode, watch.Elapsed.TotalMilliseconds, ms.Length, new FileInfo(historyPath).Length);
    }

    private static (double, double, double, long, long) Now(SKBitmap bitmap, string historyPath)
    {
        var watch = Stopwatch.StartNew();
        using var data = FloatingBitmapConversionHelper.EncodeFastPng(bitmap);
        byte[] png = data!.ToArray();
        double encode = watch.Elapsed.TotalMilliseconds;

        watch.Restart();
        using (new System.Drawing.Bitmap(new MemoryStream(png)))
        {
        }

        double decode = watch.Elapsed.TotalMilliseconds;

        watch.Restart();
        File.WriteAllBytes(historyPath, png);
        return (encode, decode, watch.Elapsed.TotalMilliseconds, png.Length, new FileInfo(historyPath).Length);
    }

    private void Report(string label, List<(double Clipboard, double Decode, double History, long ClipboardBytes, long FileBytes)> runs)
    {
        static double Median(IEnumerable<double> values)
        {
            var sorted = values.OrderBy(v => v).ToArray();
            return sorted[sorted.Length / 2];
        }

        _output.WriteLine(
            $"{label}: clipboard encode {Median(runs.Select(r => r.Clipboard)):F0} ms, GDI+ decode {Median(runs.Select(r => r.Decode)):F0} ms, " +
            $"history file {Median(runs.Select(r => r.History)):F0} ms; clipboard PNG {runs[0].ClipboardBytes / 1024} KB, " +
            $"history file {runs[0].FileBytes / 1024} KB (medians of {runs.Count})");
    }
}
