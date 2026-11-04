using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using GimmeCapture.Services.Interop;
using GimmeCapture.ViewModels.Main;
using GimmeCapture.Views.Main;
using Xunit.Abstractions;

namespace GimmeCapture.Tests;

/// <summary>
/// How long the main window takes to open in a real Win32 window off screen, in the stages the app's
/// <c>Startup.Timings</c> line names: built (its constructor), shown (<c>Show</c>, which lays it out and raises Opened)
/// and drawn (two animation frames later), each with the UI thread's CPU. The first open of the process stands for a
/// launch from the taskbar, later ones for the tray reopening a window released while hidden; this build is not
/// ReadyToRun as the published one is, so the first open also compiles what the published app would not. Measured
/// 2026-11-04 (Ryzen 7 5700X, GPU rendering, three fresh processes): the first open 1283-1302 ms (built 550-569, shown
/// 396-400, drawn 332-337), every later one 45-123 ms. Traced, the first went on what Avalonia does once a process:
/// control templates built on first use, type initializers, fonts loaded and shaped, and the first frame, the UI thread
/// waiting while the render thread drew it. An empty theme for the drawn-decorations layer Avalonia builds for a window
/// whose client area is extended, no inset shadows on the colour swatches and a 64 px window icon together changed
/// nothing (1306-1328 ms). Opt-in (MAIN_WINDOW_OPEN_PROBE=1): it sets up Avalonia on the real platform, which a process
/// can do only once.
/// MAIN_WINDOW_OPEN_PROBE_OPENS sets how many opens (6), MAIN_WINDOW_OPEN_RENDERER=software draws on the CPU, and
/// MAIN_WINDOW_OPEN_PROBE_HOLD keeps the process that many seconds more, for a trace to end before it does.
/// </summary>
public sealed class MainWindowOpenProbeTests
{
    private readonly ITestOutputHelper _output;

    public MainWindowOpenProbeTests(ITestOutputHelper output) => _output = output;

    [OptInFact("MAIN_WINDOW_OPEN_PROBE=1")]
    public void OpeningTheMainWindow()
    {
        bool software = Environment.GetEnvironmentVariable("MAIN_WINDOW_OPEN_RENDERER") == "software";
        int opens = int.TryParse(Environment.GetEnvironmentVariable("MAIN_WINDOW_OPEN_PROBE_OPENS"), out int count) ? count : 6;
        AppBuilder.Configure<GimmeCapture.App>()
            .UsePlatformDetect()
            .With(GimmeCapture.Composition.StartupRenderer.Win32Options(software))
            .WithInterFont()
            .SetupWithoutStarting();

        // The design dependencies: settings in a temporary folder, no hotkeys, no start-up registration.
        var vm = new MainWindowViewModel();
        PumpUntil(() => vm.InitialSettingsLoadTask.IsCompleted, TimeSpan.FromSeconds(30));
        PumpFor(TimeSpan.FromSeconds(2));

        using var cpu = ThreadCpuClock.ForCurrentThread();
        for (int open = 1; open <= opens; open++)
        {
            long started = Stopwatch.GetTimestamp();
            var cpuStarted = cpu?.Read();
            var window = new MainWindow(vm, null)
            {
                WindowStartupLocation = WindowStartupLocation.Manual,
                Position = new PixelPoint(-20000, -20000),
                ShowActivated = false,
            };
            long built = Stopwatch.GetTimestamp();
            var cpuBuilt = cpu?.Read();
            window.Show();
            long shown = Stopwatch.GetTimestamp();
            var cpuShown = cpu?.Read();
            bool drawnYet = false;
            long drawn = 0;
            TimeSpan? cpuDrawn = null;
            // The first callback comes as the first frame starts; the second once that frame has been drawn.
            window.RequestAnimationFrame(_ => window.RequestAnimationFrame(_ =>
            {
                drawn = Stopwatch.GetTimestamp();
                cpuDrawn = cpu?.Read();
                drawnYet = true;
            }));
            PumpUntil(() => drawnYet, TimeSpan.FromSeconds(10));

            _output.WriteLine(
                $"open {open} at {SinceProcessStart(started):0} ms: built {Ms(started, built)} ms (cpu {CpuMs(cpuStarted, cpuBuilt)}), " +
                $"shown {Ms(built, shown)} ({CpuMs(cpuBuilt, cpuShown)}), drawn {Ms(shown, drawn)} ({CpuMs(cpuShown, cpuDrawn)}), " +
                $"total {Ms(started, drawn)} ms");
            Assert.True(drawnYet, $"Open {open} drew no frame.");

            window.Release();
            PumpFor(TimeSpan.FromSeconds(1));
            GC.Collect();
            GC.WaitForPendingFinalizers();
            PumpFor(TimeSpan.FromMilliseconds(500));
        }

        // A trace of this process must be stopped while it still runs, or its file ends unreadable.
        if (int.TryParse(Environment.GetEnvironmentVariable("MAIN_WINDOW_OPEN_PROBE_HOLD"), out int hold))
        {
            PumpFor(TimeSpan.FromSeconds(hold));
        }
    }

    private static string Ms(long from, long to) => Stopwatch.GetElapsedTime(from, to).TotalMilliseconds.ToString("0");

    // To find an open in a trace of the test host, whose times count from about when the process started.
    private static double SinceProcessStart(long timestamp) =>
        (DateTime.Now - Stopwatch.GetElapsedTime(timestamp) - Process.GetCurrentProcess().StartTime).TotalMilliseconds;

    private static string CpuMs(TimeSpan? from, TimeSpan? to) =>
        from is { } a && to is { } b ? (b - a).TotalMilliseconds.ToString("0") : "?";

    private static void PumpUntil(Func<bool> done, TimeSpan limit)
    {
        var giveUp = Stopwatch.StartNew();
        while (!done() && giveUp.Elapsed < limit)
        {
            PumpFor(TimeSpan.FromMilliseconds(5));
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
}
