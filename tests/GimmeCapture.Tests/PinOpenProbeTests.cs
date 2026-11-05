using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using GimmeCapture.Services.Abstractions;
using GimmeCapture.Services.Interop;
using GimmeCapture.ViewModels.Floating;
using GimmeCapture.Views.Floating;
using SkiaSharp;
using Xunit.Abstractions;

namespace GimmeCapture.Tests;

/// <summary>
/// How long a pin takes to open the way the capture overlay opens one from a frozen capture, in a real Win32 window off
/// screen: the crop copied into an Avalonia bitmap, the view model, the window, <c>Show</c>, and the first frame drawn,
/// each with the UI thread's CPU. The overlay stays up until the pin has drawn (<c>ExecutePinCaptureAsync</c>), so
/// this is how long a frozen overlay lingers after the pin is asked for. With the tray's host window up, as the app runs
/// in the tray, the first pin of the process took 0.93-1.04 s in this build, which is not ReadyToRun, later ones 37-73
/// ms at 1280x720 and 3968x1160, on the GPU and the CPU alike (2026-11-05). PIN_OPEN_WARMUP=app runs the app's warm-up
/// first (<c>SnipWindowFactory.ScheduleWarmUp</c>): on the GPU it only builds a pin, after which the first took 0.56 s
/// and nothing was kept; on the CPU it also draws one off screen, after which the first took 0.13 s and 0.5-1.3 MB was
/// kept. =build and =show do those by hand: shown on the GPU, a pin kept 26-28 MB. Opt-in (PIN_OPEN_PROBE=1): it sets
/// up Avalonia on the real platform, which a process can do only once. PIN_OPEN_RENDERER=software draws on the CPU.
/// </summary>
public sealed class PinOpenProbeTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly string _baseDir = Path.Combine(Path.GetTempPath(), "GimmeCapture.Tests", Guid.NewGuid().ToString("N"));

    public PinOpenProbeTests(ITestOutputHelper output) => _output = output;

    [OptInFact("PIN_OPEN_PROBE=1")]
    public void OpeningAPin_AsTheOverlayDoes()
    {
        bool software = Environment.GetEnvironmentVariable("PIN_OPEN_RENDERER") == "software";
        AppBuilder.Configure<GimmeCapture.App>()
            .UsePlatformDetect()
            .With(GimmeCapture.Composition.StartupRenderer.Win32Options(software))
            .WithInterFont()
            .SetupWithoutStarting();
        GimmeCapture.Composition.StartupRenderer.IsSoftware = software;
        _output.WriteLine(software ? "Software rendering" : "GPU rendering");
        // As the app runs in the tray: its host window shown, so the renderer's own set-up is paid before anything here.
        new Window
        {
            Width = 1,
            Height = 1,
            Opacity = 0,
            Background = Brushes.Transparent,
            ShowInTaskbar = false,
            ShowActivated = false,
            CanResize = false,
            WindowDecorations = WindowDecorations.None,
            TransparencyLevelHint = [WindowTransparencyLevel.Transparent],
            WindowStartupLocation = WindowStartupLocation.Manual,
            Position = new PixelPoint(-32000, -32000),
        }.Show();
        PumpFor(TimeSpan.FromSeconds(2));

        var settings = new AppSettingsService(_baseDir);
        var paths = new AIPathService(settings);
        var ai = new AIResourceService(settings, paths, new AIModelDownloader());
        var sam2 = new SAM2RuntimeService(paths);
        using var cpu = ThreadCpuClock.ForCurrentThread();

        // PIN_OPEN_WARMUP=build builds a pin's view model and window first and throws them away; =show also shows
        // the window off screen until it has drawn; =app runs the app's own warm-up (SnipWindowFactory.ScheduleWarmUp).
        string warmUp = Environment.GetEnvironmentVariable("PIN_OPEN_WARMUP") ?? "none";
        long privateBefore = PrivateBytes();
        if (warmUp == "app")
        {
            long warmStarted = Stopwatch.GetTimestamp();
            // The design dependencies: settings in a temporary folder, no hotkeys, no start-up registration.
            var mainVm = new GimmeCapture.ViewModels.Main.MainWindowViewModel();
            var factory = GimmeCapture.Composition.RuntimeServiceFactory.CreateSnipWindowFactory();
            int pinsOpened = 0, pinsClosed = 0;
            Window.WindowOpenedEvent.AddClassHandler<FloatingImageWindow>((_, _) => pinsOpened++);
            Window.WindowClosedEvent.AddClassHandler<FloatingImageWindow>((_, _) => pinsClosed++);
            factory.ScheduleWarmUp(mainVm, TimeSpan.Zero);
            // The overlay's warm-up, then the pin's: drawing on the CPU shown until it has drawn and closed after, on the
            // GPU only built.
            var giveUp = Stopwatch.StartNew();
            while ((software ? pinsClosed == 0 : giveUp.Elapsed < TimeSpan.FromSeconds(2)) && giveUp.Elapsed < TimeSpan.FromSeconds(15))
            {
                PumpFor(TimeSpan.FromMilliseconds(20));
            }

            Assert.Equal(software ? (1, 1) : (0, pinsClosed), (pinsOpened, pinsClosed));
            _output.WriteLine(
                $"warm-up (app) {Ms(warmStarted, Stopwatch.GetTimestamp())} ms; private bytes {(PrivateBytes() - privateBefore) / 1e6:+0.0;-0.0} MB after it");
            PumpFor(TimeSpan.FromSeconds(1));
        }
        else if (warmUp != "none")
        {
            long warmStarted = Stopwatch.GetTimestamp();
            var warmVm = new FloatingImageViewModel(
                ToAvaloniaBitmap(new SKBitmap(1, 1)), 1, 1, Colors.Red, 2.0, false, false, Mock.Of<IClipboardService>(), ai, sam2,
                settings, Mock.Of<IBackgroundRemovalService>(), new ResourceQueueService());
            var warmWindow = new FloatingImageWindow { DataContext = warmVm, Position = new PixelPoint(-20000, -20000), ShowActivated = false };
            if (warmUp == "show")
            {
                bool warmDrawn = false;
                warmWindow.Show();
                warmWindow.RequestAnimationFrame(_ => warmWindow.RequestAnimationFrame(_ => warmDrawn = true));
                while (!warmDrawn)
                {
                    PumpFor(TimeSpan.FromMilliseconds(5));
                }
            }

            warmWindow.Close();
            warmVm.Dispose();
            PumpFor(TimeSpan.FromSeconds(1));
            _output.WriteLine(
                $"warm-up ({warmUp}) {Ms(warmStarted, Stopwatch.GetTimestamp())} ms; private bytes {(PrivateBytes() - privateBefore) / 1e6:+0.0;-0.0} MB after it");
        }
        foreach (var (width, height) in new[] { (1280, 720), (1280, 720), (1280, 720), (3968, 1160), (3968, 1160), (3968, 1160) })
        {
            using var crop = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
            crop.Erase(new SKColor(0x30, 0x60, 0xA0));

            long started = Stopwatch.GetTimestamp();
            var c0 = cpu?.Read();
            var picture = ToAvaloniaBitmap(crop);
            long copied = Stopwatch.GetTimestamp();
            var c1 = cpu?.Read();
            var vm = new FloatingImageViewModel(
                picture, width, height, Colors.Red, 2.0, false, false, Mock.Of<IClipboardService>(), ai, sam2, settings,
                Mock.Of<IBackgroundRemovalService>(), new ResourceQueueService());
            long built = Stopwatch.GetTimestamp();
            var c2 = cpu?.Read();
            var padding = vm.WindowPadding;
            var window = new FloatingImageWindow
            {
                DataContext = vm,
                Position = new PixelPoint(-20000, -20000),
                Width = vm.DisplayWidth + padding.Left + padding.Right,
                Height = vm.DisplayHeight + padding.Top + padding.Bottom,
                ShowActivated = false,
            };
            long made = Stopwatch.GetTimestamp();
            var c3 = cpu?.Read();
            window.Show();
            long shown = Stopwatch.GetTimestamp();
            var c4 = cpu?.Read();
            long drawn = 0;
            TimeSpan? c5 = null;
            window.RequestAnimationFrame(_ => window.RequestAnimationFrame(_ =>
            {
                drawn = Stopwatch.GetTimestamp();
                c5 = cpu?.Read();
            }));
            var giveUp = Stopwatch.StartNew();
            while (drawn == 0 && giveUp.Elapsed < TimeSpan.FromSeconds(10))
            {
                PumpFor(TimeSpan.FromMilliseconds(5));
            }

            _output.WriteLine(
                $"{width}x{height}: copy {Ms(started, copied)} ms ({CpuMs(c0, c1)}), view model {Ms(copied, built)} ({CpuMs(c1, c2)}), " +
                $"window {Ms(built, made)} ({CpuMs(c2, c3)}), show {Ms(made, shown)} ({CpuMs(c3, c4)}), " +
                $"first frame {Ms(shown, drawn)} ({CpuMs(c4, c5)}), total {Ms(started, drawn)} ms");
            Assert.NotEqual(0, drawn);

            window.Close();
            vm.Dispose();
            PumpFor(TimeSpan.FromMilliseconds(500));
        }
    }

    // As the overlay's ToAvaloniaBitmap copies a crop.
    private static WriteableBitmap ToAvaloniaBitmap(SKBitmap source)
    {
        var bitmap = new WriteableBitmap(new PixelSize(source.Width, source.Height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        using var locked = bitmap.Lock();
        unsafe
        {
            Buffer.MemoryCopy((void*)source.GetPixels(), (void*)locked.Address, locked.RowBytes * locked.Size.Height, source.RowBytes * source.Height);
        }

        return bitmap;
    }

    private static string Ms(long from, long to) => Stopwatch.GetElapsedTime(from, to).TotalMilliseconds.ToString("0");

    private static long PrivateBytes()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        using var process = Process.GetCurrentProcess();
        return process.PrivateMemorySize64;
    }

    private static string CpuMs(TimeSpan? from, TimeSpan? to) =>
        from is { } a && to is { } b ? (b - a).TotalMilliseconds.ToString("0") : "?";

    // Cancelled by a job on the UI thread: Avalonia's loop reads the token only between messages (UiStallMonitorIdleProbeTests).
    private static void PumpFor(TimeSpan time)
    {
        using var cts = new CancellationTokenSource();
        using var end = new Timer(_ => Dispatcher.UIThread.Post(cts.Cancel), null, time, Timeout.InfiniteTimeSpan);
        Dispatcher.UIThread.MainLoop(cts.Token);
    }

    public void Dispose()
    {
        try { Directory.Delete(_baseDir, recursive: true); } catch { /* best effort temp cleanup */ }
    }
}
