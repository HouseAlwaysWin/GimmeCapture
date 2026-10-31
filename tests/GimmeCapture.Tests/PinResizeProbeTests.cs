using System.Diagnostics;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using GimmeCapture.Services.Abstractions;
using GimmeCapture.Services.Interop;
using GimmeCapture.ViewModels.Floating;
using GimmeCapture.Views.Floating;
using Xunit.Abstractions;

namespace GimmeCapture.Tests;

/// <summary>
/// Drags a pin's corner in a real window for a few seconds, with a mouse move every 2 ms as a 500 Hz mouse sends them
/// (Windows keeps at most one waiting, so a move is posted only once the last one was taken), and reports how the UI
/// thread kept up: frames drawn, moves taken, its CPU time, the window-region rebuilds and how late a background job
/// got, which is what the app's <c>UiStall</c> lines measure. Each move resized the window and rebuilt its region
/// several times: an opaque 1280x720 pin's drag kept the UI thread 96-98% busy with 32 rebuilds a frame and held a
/// background job up to 3 s, and a cut-out one drew 34-41 frames a second. Now the newest move is applied once a
/// frame, the region rebuilt once or twice after it, and a cut-out pin's opaque part placed instead of rebuilt: 18-27%
/// busy, 60 frames, background jobs at most 45 ms late (Ryzen 7 5700X, a 60 Hz screen at 100%). Drawn on the CPU
/// (PIN_RESIZE_RENDERER=software, as "Use less memory" draws), the same drags ran at 19-20 frames a second with a gen2
/// GC about every other frame, background jobs at most 63 ms late. Opt-in (PIN_RESIZE_PROBE=1): it sets up Avalonia on
/// the real platform, which a process can do only once, and puts two pins on screen.
/// </summary>
public sealed class PinResizeProbeTests : IDisposable
{
    private static readonly TimeSpan DragTime = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MoveInterval = TimeSpan.FromMilliseconds(2);
    private static readonly TimeSpan BackgroundTick = TimeSpan.FromMilliseconds(100);

    private readonly ITestOutputHelper _output;
    private readonly string _baseDir = Path.Combine(Path.GetTempPath(), "GimmeCapture.Tests", Guid.NewGuid().ToString("N"));

    public PinResizeProbeTests(ITestOutputHelper output) => _output = output;

    [OptInFact("PIN_RESIZE_PROBE=1")]
    public void DraggingAPinsCorner_KeepsTheUiThreadUpWithThePointer()
    {
        // PIN_RESIZE_RENDERER=software draws on the CPU, as "Use less memory" does (StartupRenderer).
        bool software = Environment.GetEnvironmentVariable("PIN_RESIZE_RENDERER") == "software";
        _output.WriteLine(software ? "Software rendering" : "GPU rendering");
        AppBuilder.Configure<GimmeCapture.App>()
            .UsePlatformDetect()
            .With(GimmeCapture.Composition.StartupRenderer.Win32Options(software))
            .WithInterFont()
            .SetupWithoutStarting();

        var opaque = Drag(cutOut: false);
        var cutOut = Drag(cutOut: true);

        foreach (var drag in new[] { opaque, cutOut })
        {
            // One rebuild a frame, and a second while the pin runs past the bottom of the screen, as it does here: the
            // toolbar then moves up every frame, which asks for another.
            Assert.True(drag.Steps > 0 && drag.Frames > 0, "The drag never ran.");
            Assert.True(drag.Rebuilds <= (drag.Frames * 2) + 5, $"{drag.Rebuilds} region rebuilds for {drag.Frames} frames.");
            Assert.Equal(0, drag.LateTicks);
        }
    }

    private sealed record DragResult(int Steps, int Frames, int Rebuilds, int LateTicks);

    private DragResult Drag(bool cutOut)
    {
        var settings = new AppSettingsService(_baseDir);
        var paths = new AIPathService(settings);
        var ai = new AIResourceService(settings, paths, new AIModelDownloader());
        using var image = CreateImage(1280, 720, cutOut);
        var vm = new FloatingImageViewModel(
            image, 1280, 720, Colors.Red, 2.0, false, false, new Mock<IClipboardService>().Object, ai,
            new SAM2RuntimeService(paths), settings, paths, new ResourceQueueService());
        var window = new FloatingImageWindow { DataContext = vm, Position = new PixelPoint(200, 150) };
        window.Show();
        PumpFor(TimeSpan.FromSeconds(1));

        double scaling = window.RenderScaling;
        var corner = new Point(
            window.Position.X + (window.Bounds.Width * scaling),
            window.Position.Y + (window.Bounds.Height * scaling));
        double startWidth = vm.DisplayWidth;
        int rebuildsBefore = window.WindowRegionRebuilds;
        var rebuildTimeBefore = window.WindowRegionRebuildTime;

        using var cpu = ThreadCpuClock.ForCurrentThread();
        var cpuBefore = cpu?.Read();
        int gen0 = GC.CollectionCount(0), gen2 = GC.CollectionCount(2);
        int steps = 0, frames = 0, lateTicks = 0;
        var maxLate = TimeSpan.Zero;
        var totalLate = TimeSpan.Zero;
        int movePending = 0;
        long started = Stopwatch.GetTimestamp();
        using var done = new CancellationTokenSource();

        window.BeginResize(FloatingWindowBase.ResizeDirection.BottomRight, corner);

        // The corner goes 400 px right and 250 down and back, every 1.2 s.
        void Move()
        {
            Volatile.Write(ref movePending, 0);
            double phase = Stopwatch.GetElapsedTime(started).TotalSeconds / 1.2 % 1.0;
            double reach = phase < 0.5 ? phase * 2 : (1 - phase) * 2;
            window.ResizeToward(new Point(corner.X + (400 * reach), corner.Y + (250 * reach)), keepAspectRatio: false);
            steps++;
        }

        var mouse = new Thread(() =>
        {
            long next = Stopwatch.GetTimestamp();
            long interval = (long)(MoveInterval.TotalSeconds * Stopwatch.Frequency);
            while (!done.IsCancellationRequested)
            {
                next += interval;
                while (Stopwatch.GetTimestamp() < next)
                {
                    Thread.SpinWait(50);
                }

                if (Interlocked.Exchange(ref movePending, 1) == 0)
                {
                    Dispatcher.UIThread.Post(Move, DispatcherPriority.Input);
                }
            }
        }) { IsBackground = true, Name = "Probe mouse" };

        void OnFrame(TimeSpan _)
        {
            frames++;
            if (!done.IsCancellationRequested)
            {
                window.RequestAnimationFrame(OnFrame);
            }
        }

        long lastTick = Stopwatch.GetTimestamp();
        var background = new DispatcherTimer(BackgroundTick, DispatcherPriority.Background, (_, _) =>
        {
            long now = Stopwatch.GetTimestamp();
            var late = Stopwatch.GetElapsedTime(lastTick, now) - BackgroundTick;
            lastTick = now;
            totalLate += late > TimeSpan.Zero ? late : TimeSpan.Zero;
            maxLate = late > maxLate ? late : maxLate;
            lateTicks += late >= TimeSpan.FromMilliseconds(250) ? 1 : 0;
        });

        var stop = new DispatcherTimer(DragTime, DispatcherPriority.Send, (sender, _) =>
        {
            ((DispatcherTimer)sender!).Stop();
            done.Cancel();
        });

        background.Start();
        stop.Start();
        mouse.Start();
        window.RequestAnimationFrame(OnFrame);
        Dispatcher.UIThread.MainLoop(done.Token);
        var elapsed = Stopwatch.GetElapsedTime(started);
        background.Stop();
        mouse.Join();

        var cpuTime = cpu?.Read() - cpuBefore;
        int rebuilds = window.WindowRegionRebuilds - rebuildsBefore;
        var rebuildTime = window.WindowRegionRebuildTime - rebuildTimeBefore;
        window.EndResize();
        double endWidth = vm.DisplayWidth;
        window.Close();
        PumpFor(TimeSpan.FromMilliseconds(500));

        double seconds = elapsed.TotalSeconds;
        _output.WriteLine(
            $"{(cutOut ? "Cut-out" : "Opaque")} 1280x720 pin at {scaling:0.##}x, {seconds:0.0} s: " +
            $"{steps / seconds:0} moves/s taken, {frames / seconds:0} frames/s, UI thread CPU {(cpuTime?.TotalSeconds ?? double.NaN) / seconds:P0}, " +
            $"{rebuilds} region rebuilds ({(double)rebuilds / Math.Max(1, frames):0.0} a frame, {rebuildTime.TotalMilliseconds / Math.Max(1, rebuilds):0.00} ms each, " +
            $"{rebuildTime.TotalSeconds / seconds:P0} of the time), background job late by {maxLate.TotalMilliseconds:0} ms at most, " +
            $"{totalLate.TotalSeconds / seconds:P0} of the time, {lateTicks} ticks 250 ms late or more; " +
            $"GCs {GC.CollectionCount(0) - gen0} gen0, {GC.CollectionCount(2) - gen2} gen2. Width {startWidth:0} -> {endWidth:0}.");
        return new DragResult(steps, frames, rebuilds, lateTicks);
    }

    private static void PumpFor(TimeSpan time)
    {
        using var cts = new CancellationTokenSource(time);
        Dispatcher.UIThread.MainLoop(cts.Token);
    }

    // Opaque, or opaque only inside an ellipse, as a pin whose background was removed: one run a row, each row's
    // different, so about 700 rectangles in the window region.
    private static WriteableBitmap CreateImage(int width, int height, bool cutOut)
    {
        var bitmap = new WriteableBitmap(new PixelSize(width, height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        using var buffer = bitmap.Lock();
        unsafe
        {
            for (int y = 0; y < height; y++)
            {
                uint* row = (uint*)((byte*)buffer.Address + (y * buffer.RowBytes));
                double dy = (y - (height / 2.0)) / (height / 2.0);
                for (int x = 0; x < width; x++)
                {
                    double dx = (x - (width / 2.0)) / (width / 2.0);
                    bool inside = !cutOut || (dx * dx) + (dy * dy) <= 1;
                    row[x] = inside ? 0xFF000000u | TestBitmaps.Noise(x / 8, y / 8) & 0x00FFFFFFu : 0u;
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
