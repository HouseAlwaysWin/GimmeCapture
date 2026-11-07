using System.Diagnostics;
using Avalonia;
using Avalonia.Threading;
using GimmeCapture.Views.Main;
using Xunit.Abstractions;

namespace GimmeCapture.Tests;

/// <summary>
/// What an overlay window built ahead of a capture costs while it waits, unseen: the time its build takes, which a
/// capture that finds one ready skips (the <c>window=</c> stage of <c>SnipOpen.Timings</c>, 32-57 ms warm in the
/// user's log), and the private bytes it holds. The real Win32 platform, drawing on the CPU as the user's does, and no
/// window is ever shown. Opt-in (OVERLAY_PREPARE_PROBE=1): it sets up Avalonia, which a process can do only once.
/// </summary>
public sealed class OverlayPrepareProbeTests
{
    private readonly ITestOutputHelper _output;

    public OverlayPrepareProbeTests(ITestOutputHelper output) => _output = output;

    [OptInFact("OVERLAY_PREPARE_PROBE=1")]
    public void AnOverlayWindowBuiltAhead_CostsItsBuildTime_AndSomeMemory()
    {
        AppBuilder.Configure<GimmeCapture.App>()
            .UsePlatformDetect()
            .With(GimmeCapture.Composition.StartupRenderer.Win32Options(true))
            .SetupWithoutStarting();

        // The first builds load and compile the window's code, as the app's idle warm-up does before any capture.
        var built = new List<SnipWindow>();
        for (int i = 0; i < 3; i++)
        {
            built.Add(Build(out _));
        }

        var times = new List<double>();
        var bytes = new List<long>();
        for (int i = 0; i < 6; i++)
        {
            long before = PrivateBytes();
            built.Add(Build(out double ms));
            times.Add(ms);
            bytes.Add(PrivateBytes() - before);
        }

        _output.WriteLine(
            $"build {string.Join(", ", times.Select(t => t.ToString("0.0")))} ms; held " +
            $"{string.Join(", ", bytes.Select(b => (b / 1048576.0).ToString("0.00")))} MB a window");
        foreach (var window in built)
        {
            window.Close();
        }

        Dispatcher.UIThread.RunJobs();
        Assert.True(times.Average() > 0);
    }

    private static SnipWindow Build(out double milliseconds)
    {
        var stopwatch = Stopwatch.StartNew();
        // A warm-up window: built like a real one, and closed without a real overlay's clean-up.
        var window = new SnipWindow { IsWarmUp = true };
        milliseconds = stopwatch.Elapsed.TotalMilliseconds;
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    private static long PrivateBytes()
    {
        GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        using var process = Process.GetCurrentProcess();
        return process.PrivateMemorySize64;
    }
}
