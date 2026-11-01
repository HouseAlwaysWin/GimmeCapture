using System.Diagnostics;
using System.Reactive.Concurrency;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using GimmeCapture.Models;
using GimmeCapture.Services.Abstractions;
using GimmeCapture.Services.Core.AI;
using GimmeCapture.Services.Core.Infrastructure;
using GimmeCapture.Services.Interop;
using GimmeCapture.Services.Platforms.Desktop;
using GimmeCapture.ViewModels.Floating;
using GimmeCapture.Views.Floating;
using ReactiveUI.Reactive;
using Xunit.Abstractions;

namespace GimmeCapture.Tests;

/// <summary>
/// A mosaic dragged over a 4K pin for a few seconds in a real window, on the graphics card. Its preview reads the pin's
/// picture at every redraw, and reading a WriteableBitmap marks it changed, so the card may take the whole picture again
/// each frame. REDACTION_DRAG_PROBE=displayed reads the picture the pin shows, =copy reads a copy nothing shows, which
/// tells what that costs. On an RTX 4070 SUPER, nothing measurable: 34 frames a second against 30, the process at
/// 110-121% of a core against 114-116%, the UI thread at 26-32% against 28-31%. Opt-in: it sets up Avalonia on the real
/// platform, which a process can do only once, and puts a pin on screen.
/// </summary>
public sealed class RedactionDragProbeTests : IDisposable
{
    private static readonly TimeSpan DragTime = TimeSpan.FromSeconds(4);
    private readonly ITestOutputHelper _output;
    private readonly string _baseDir = Path.Combine(Path.GetTempPath(), "GimmeCapture.Tests", Guid.NewGuid().ToString("N"));

    public RedactionDragProbeTests(ITestOutputHelper output) => _output = output;

    [OptInFact("REDACTION_DRAG_PROBE")]
    public void DraggingAMosaicOverA4KPin()
    {
        bool fromDisplayed = Environment.GetEnvironmentVariable("REDACTION_DRAG_PROBE") == "displayed";
        AppBuilder.Configure<GimmeCapture.App>()
            .UsePlatformDetect()
            .With(GimmeCapture.Composition.StartupRenderer.Win32Options(false))
            .WithInterFont()
            .SetupWithoutStarting();
        IScheduler testScheduler = RxSchedulers.MainThreadScheduler;
        RxSchedulers.MainThreadScheduler = AvaloniaMainThreadScheduler.Instance;
        try
        {
            var settings = new AppSettingsService(_baseDir);
            var paths = new AIPathService(settings);
            var ai = new AIResourceService(settings, paths, new AIModelDownloader());
            var picture = CreatePicture(3840, 2160);
            var vm = new FloatingImageViewModel(
                picture, 1280, 720, Colors.Red, 2.0, false, false, new Mock<IClipboardService>().Object, ai,
                new SAM2RuntimeService(paths), settings, paths, new ResourceQueueService());
            var mosaic = new Annotation
            {
                Type = AnnotationType.Mosaic,
                StartPoint = new Point(200, 150),
                EndPoint = new Point(500, 350),
                DrawingModeSnapshot = fromDisplayed ? picture : CreatePicture(3840, 2160),
                DrawingModeReferenceSize = new Size(1280, 720),
                EffectSettings = new AnnotationEffectSettings { MosaicCellSize = 12 },
            };
            vm.Annotations.Add(mosaic);
            var window = new FloatingImageWindow { DataContext = vm, Position = new PixelPoint(150, 100) };
            window.Show();
            PumpFor(TimeSpan.FromSeconds(1.5));

            int frames = 0;
            using var done = new CancellationTokenSource();
            long started = Stopwatch.GetTimestamp();
            void OnFrame(TimeSpan _)
            {
                frames++;
                // The mosaic's corner goes 300 px right and back every second, a move a frame.
                double phase = Stopwatch.GetElapsedTime(started).TotalSeconds % 1.0;
                double reach = phase < 0.5 ? phase * 2 : (1 - phase) * 2;
                mosaic.EndPoint = new Point(500 + (300 * reach), 350 + (100 * reach));
                if (!done.IsCancellationRequested)
                {
                    window.RequestAnimationFrame(OnFrame);
                }
            }

            using var ui = ThreadCpuClock.ForCurrentThread();
            var uiBefore = ui?.Read();
            using var process = Process.GetCurrentProcess();
            var processBefore = process.TotalProcessorTime;
            var stop = new DispatcherTimer(DragTime, DispatcherPriority.Send, (sender, _) =>
            {
                ((DispatcherTimer)sender!).Stop();
                done.Cancel();
            });
            stop.Start();
            window.RequestAnimationFrame(OnFrame);
            Dispatcher.UIThread.MainLoop(done.Token);
            double seconds = Stopwatch.GetElapsedTime(started).TotalSeconds;
            process.Refresh();
            var processTime = process.TotalProcessorTime - processBefore;
            var uiTime = ui?.Read() - uiBefore;
            window.Close();
            PumpFor(TimeSpan.FromMilliseconds(500));

            _output.WriteLine(
                $"mosaic reading {(fromDisplayed ? "the picture the pin shows" : "a copy nothing shows")}, {seconds:0.0} s: " +
                $"{frames / seconds:0} frames/s, the process's CPU {processTime.TotalSeconds / seconds:P0} of a core, " +
                $"the UI thread's {(uiTime?.TotalSeconds ?? double.NaN) / seconds:P0}");
        }
        finally
        {
            RxSchedulers.MainThreadScheduler = testScheduler;
        }
    }

    // Cancelled by a job on the UI thread: Avalonia's loop reads the token only between messages, so with nothing posted
    // it would sleep through a token cancelled from another thread (UiStallMonitorIdleProbeTests, for hours).
    private static void PumpFor(TimeSpan time)
    {
        using var cts = new CancellationTokenSource();
        using var end = new Timer(_ => Dispatcher.UIThread.Post(cts.Cancel), null, time, Timeout.InfiniteTimeSpan);
        Dispatcher.UIThread.MainLoop(cts.Token);
    }

    private static WriteableBitmap CreatePicture(int width, int height)
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
                    row[x] = 0xFF000000u | (TestBitmaps.Noise(x / 8, y / 8) & 0x00FFFFFFu);
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
