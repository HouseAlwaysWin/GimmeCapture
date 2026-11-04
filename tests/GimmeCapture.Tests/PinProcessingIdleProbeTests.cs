using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using GimmeCapture.Services.Abstractions;
using GimmeCapture.ViewModels.Floating;
using GimmeCapture.Views.Floating;
using Xunit.Abstractions;

namespace GimmeCapture.Tests;

/// <summary>
/// How often an image pin keeps the process waking while it sits idle: as first shown, while its processing overlay
/// (the bar a background removal or a smart selection shows) is up, and after it has gone. The bar animates through a
/// style, and Avalonia only pauses a hidden control's style animation: paused, it stays on the UI thread's animation
/// clock, which renders every 16 ms while anything is on it. So once the overlay had shown, the UI thread woke 332 times
/// a second instead of 13 and took 18.2 M cycles a second instead of 2.4, until the pin closed (GPU rendering, a 640x360
/// pin, Ryzen 7 5700X). The bar is now indeterminate only while the overlay shows: 9.1-11.9 wakeups and 1.9-2.3 M
/// cycles a second after it, three runs, and 372 wakeups while it shows. Opt-in (PIN_PROCESSING_PROBE=1): it sets up
/// Avalonia on the real platform, which a process can do only once, and shows a pin off screen for about a minute.
/// </summary>
public sealed class PinProcessingIdleProbeTests : IDisposable
{
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(10);

    private readonly ITestOutputHelper _output;
    private readonly string _baseDir = Path.Combine(Path.GetTempPath(), "GimmeCapture.Tests", Guid.NewGuid().ToString("N"));

    public PinProcessingIdleProbeTests(ITestOutputHelper output) => _output = output;

    [OptInFact("PIN_PROCESSING_PROBE=1")]
    public void APinWhoseProcessingOverlayCameAndWent_SitsAsIdleAsBefore()
    {
        AppBuilder.Configure<GimmeCapture.App>()
            .UsePlatformDetect()
            .With(GimmeCapture.Composition.StartupRenderer.Win32Options(false))
            .WithInterFont()
            .SetupWithoutStarting();
        // Avalonia's start-up keeps the UI thread waking for about half a minute (UiStallMonitorIdleProbeTests).
        PumpFor(TimeSpan.FromSeconds(30));

        var settings = new AppSettingsService(_baseDir);
        var paths = new AIPathService(settings);
        var ai = new AIResourceService(settings, paths, new AIModelDownloader());
        using var image = CreateImage(640, 360);
        var vm = new FloatingImageViewModel(
            image, 640, 360, Colors.Red, 2.0, false, false, Mock.Of<IClipboardService>(), ai,
            new SAM2RuntimeService(paths), settings, Mock.Of<IBackgroundRemovalService>(), new ResourceQueueService());
        var window = new FloatingImageWindow { DataContext = vm, Position = new PixelPoint(-20000, -20000), ShowActivated = false };
        window.Show();
        // A window just shown keeps the UI thread waking for a while too.
        PumpFor(TimeSpan.FromSeconds(10));
        var before = Measure("pin as shown", Window);

        vm.IsProcessing = true;
        PumpFor(TimeSpan.FromSeconds(1));
        var during = Measure("pin while its processing overlay shows", TimeSpan.FromSeconds(3));
        vm.IsProcessing = false;
        PumpFor(TimeSpan.FromSeconds(5));
        var after = Measure("pin after its processing overlay came and went", Window);

        window.Close();
        PumpFor(TimeSpan.FromMilliseconds(500));

        // The bar still runs while it shows.
        Assert.True(
            during.Wakeups > before.Wakeups + 50,
            $"The UI thread woke {during.Wakeups:0.0} times a second with the overlay up, {before.Wakeups:0.0} before it.");
        Assert.True(
            after.Wakeups <= before.Wakeups + 5,
            $"The UI thread woke {after.Wakeups:0.0} times a second after the overlay, {before.Wakeups:0.0} before it.");
    }

    // The UI thread's rate, with every thread's printed.
    private ThreadActivity.Rate Measure(string what, TimeSpan window)
    {
        uint uiThread = ThreadActivity.CurrentThreadId;
        var rows = ThreadActivity.Measure(() => PumpFor(window));
        _output.WriteLine($"{what}: {rows.Sum(t => t.Wakeups):0.0} wakeups/s, {rows.Sum(t => t.MegaCycles):0.00} M cycles/s");
        foreach (var row in rows)
        {
            string name = row.Thread == uiThread ? "UI thread" : ThreadActivity.Describe(row.Thread);
            _output.WriteLine($"  {row.Wakeups,7:0.0}/s {row.MegaCycles,7:0.000} M cycles/s  {name}{(row.New ? " (new)" : "")}");
        }

        return rows.FirstOrDefault(r => r.Thread == uiThread);
    }

    // Cancelled by a job on the UI thread: Avalonia's loop reads the token only between messages, so with nothing posted
    // it would sleep through a token cancelled from another thread (UiStallMonitorIdleProbeTests, for hours).
    private static void PumpFor(TimeSpan time)
    {
        using var cts = new CancellationTokenSource();
        using var end = new Timer(_ => Dispatcher.UIThread.Post(cts.Cancel), null, time, Timeout.InfiniteTimeSpan);
        Dispatcher.UIThread.MainLoop(cts.Token);
    }

    private static WriteableBitmap CreateImage(int width, int height)
    {
        var bitmap = new WriteableBitmap(new PixelSize(width, height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        using var buffer = bitmap.Lock();
        unsafe
        {
            for (int y = 0; y < height; y++)
            {
                uint* row = (uint*)((byte*)buffer.Address + (y * buffer.RowBytes));
                for (int x = 0; x < width; x++)
                {
                    row[x] = 0xFF000000u | TestBitmaps.Noise(x / 8, y / 8) & 0x00FFFFFFu;
                }
            }
        }

        return bitmap;
    }

    public void Dispose()
    {
        try { Directory.Delete(_baseDir, recursive: true); } catch { /* best effort temp cleanup */ }
    }
}
