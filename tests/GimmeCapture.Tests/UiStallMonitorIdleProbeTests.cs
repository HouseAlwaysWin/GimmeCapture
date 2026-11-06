using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using GimmeCapture.Services.Core.Infrastructure;
using GimmeCapture.Services.Platforms.Avalonia;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Xunit.Abstractions;

namespace GimmeCapture.Tests;

/// <summary>
/// How often each of this process's threads wakes (its context switches) and how many CPU cycles it takes while
/// Avalonia's real Win32 loop sits idle, as the app does in the tray: with no window, then with a window like the tray
/// host (1x1, transparent, off-screen), each with <see cref="UiThreadStallMonitor"/> running and stopped, then told the
/// app is in the tray (<see cref="AppWindowsOnScreen"/>). Then what the monitor logs for the UI thread held for 600 ms,
/// six times each: in the tray, in the tray with a hotkey press just posted, and with a window shown. Opt-in
/// (STALL_MONITOR_PROBE=1): it sets up Avalonia on the real platform, which a process can do only once, and replaces
/// Serilog's logger to read the UiStall lines. Until 2026-11 the monitor ticked a 100 ms DispatcherTimer: 84-113
/// UI-thread wakeups a second here with no window, and 5-6 on its watchdog. Probes posted by the watchdog took that to
/// 19-21 and 16-18, and the tray's one-second beat, with the host window up, to 3 and 3 (27.6 and 22.0 at 100 ms). A
/// hold then read 0-570 ms with nothing pressed, 602-614 after a press, and 509-555 with a window shown.
/// </summary>
public sealed class UiStallMonitorIdleProbeTests
{
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan Hold = TimeSpan.FromMilliseconds(600);

    private readonly ITestOutputHelper _output;
    private LineCapture? _lines;

    public UiStallMonitorIdleProbeTests(ITestOutputHelper output) => _output = output;

    [OptInFact("STALL_MONITOR_PROBE=1")]
    public void IdleWakeups_AndWhatAHeldUiThreadReads()
    {
        var lines = new LineCapture();
        _lines = lines;
        Log.Logger = new LoggerConfiguration().MinimumLevel.Warning().WriteTo.Sink(lines).CreateLogger();
        AppBuilder.Configure<GimmeCapture.App>()
            .UsePlatformDetect()
            .With(GimmeCapture.Composition.StartupRenderer.Win32Options(false))
            .WithInterFont()
            .SetupWithoutStarting();
        // Avalonia's start-up keeps the UI thread waking for about half a minute, 16 times a second at first: measured
        // sooner, that would count as the monitor's.
        PumpFor(TimeSpan.FromSeconds(30));

        Measure("no window, monitor stopped");
        UiThreadStallMonitor.Start();
        Measure("no window, monitor running");
        UiThreadStallMonitor.Stop();

        var host = new Window
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
        };
        host.Show();
        // A window just shown keeps the UI thread waking too, still 16 times a second 10 s on: read the monitor's share
        // against the same window with it stopped.
        PumpFor(TimeSpan.FromSeconds(10));
        Measure("tray-host window, monitor stopped");
        UiThreadStallMonitor.Start();
        Measure("tray-host window, monitor running");

        // What the app tells the monitor (AppWindowsOnScreen): the tray's host is a stand-in, and with only it shown the
        // monitor beats once a second.
        var told = new List<bool>();
        using var windows = new AppWindowsOnScreen(window => ReferenceEquals(window, host), shown =>
        {
            told.Add(shown);
            UiThreadStallMonitor.SetWindowShown(shown);
        });
        Measure("tray-host window, monitor in the tray");

        // The UI thread held at a random point between the monitor's beats, in the tray: as a timer's work would, and as
        // a hotkey press would find it, its press posted from another thread just before the hold began.
        var traySilent = Holds(press: false);
        var trayPressed = Holds(press: true);

        // A window of the app's: shown, hidden, shown, closed.
        var other = new Window
        {
            Width = 1,
            Height = 1,
            Opacity = 0,
            Background = Brushes.Transparent,
            ShowInTaskbar = false,
            ShowActivated = false,
            WindowDecorations = WindowDecorations.None,
            TransparencyLevelHint = [WindowTransparencyLevel.Transparent],
            WindowStartupLocation = WindowStartupLocation.Manual,
            Position = new PixelPoint(-32000, -32000),
        };
        other.Show();
        PumpFor(TimeSpan.FromMilliseconds(200));
        var withWindow = Holds(press: false);
        other.Hide();
        PumpFor(TimeSpan.FromMilliseconds(200));
        other.Show();
        PumpFor(TimeSpan.FromMilliseconds(200));
        other.Close();
        PumpFor(TimeSpan.FromMilliseconds(200));

        _output.WriteLine($"UI thread held {Hold.TotalMilliseconds:0} ms, six times each. UiStall read, in the tray: {Describe(traySilent)} ms; in the tray after a press: {Describe(trayPressed)} ms; with a window shown: {Describe(withWindow)} ms");
        _output.WriteLine($"Told: {string.Join(", ", told)}");

        UiThreadStallMonitor.Stop();
        host.Close();
        PumpFor(TimeSpan.FromMilliseconds(500));

        // A stall reads short by up to one beat of the monitor (100 ms), and never long; a press's own probe reads it
        // from the press.
        Assert.All(withWindow, r => Assert.InRange(r, Hold.TotalMilliseconds - 150, Hold.TotalMilliseconds + 50));
        Assert.All(trayPressed, r => Assert.InRange(r, Hold.TotalMilliseconds - 50, Hold.TotalMilliseconds + 50));
        Assert.Equal([false, true, false, true, false], told);
    }

    private static string Describe(List<double> readings) => string.Join(", ", readings.Select(r => r.ToString("0")));

    private List<double> Holds(bool press)
    {
        var random = new Random(7);
        var readings = new List<double>();
        for (int i = 0; i < 6; i++)
        {
            _lines!.Clear();
            PumpFor(TimeSpan.FromMilliseconds(300 + random.Next(200)));
            Dispatcher.UIThread.Post(() => Thread.Sleep(Hold), DispatcherPriority.Normal);
            if (press)
            {
                // From another thread, as the hotkey thread posts it.
                var thread = new Thread(UiThreadStallMonitor.NotifyInput);
                thread.Start();
                thread.Join();
            }

            PumpFor(TimeSpan.FromSeconds(1.2));
            var match = _lines.Messages().Select(m => Regex.Match(m, @"did not pump for (\d+)ms")).FirstOrDefault(m => m.Success);
            readings.Add(match is null ? 0 : double.Parse(match.Groups[1].Value));
        }

        return readings;
    }

    private void Measure(string what)
    {
        uint uiThread = ThreadActivity.CurrentThreadId;
        var rows = ThreadActivity.Measure(() => PumpFor(Window));
        _output.WriteLine($"{what}: {rows.Sum(t => t.Wakeups):0.0} wakeups/s, {rows.Sum(t => t.MegaCycles):0.00} M cycles/s");
        foreach (var row in rows)
        {
            string name = row.Thread == uiThread ? "UI thread" : ThreadActivity.Describe(row.Thread);
            _output.WriteLine($"  {row.Wakeups,7:0.0}/s {row.MegaCycles,7:0.000} M cycles/s  {name}{(row.New ? " (new)" : "")}");
        }
    }

    // Cancelled by a job on the UI thread. Avalonia's loop reads the token only between messages, so a token cancelled
    // from another thread while nothing is posted left it waiting for whatever came next: twice it was a UAC prompt, 10 min
    // and 2 h 44 min later, with the monitor stopped.
    private static void PumpFor(TimeSpan time)
    {
        using var cts = new CancellationTokenSource();
        using var end = new Timer(_ => Dispatcher.UIThread.Post(cts.Cancel), null, time, Timeout.InfiniteTimeSpan);
        Dispatcher.UIThread.MainLoop(cts.Token);
    }

    private sealed class LineCapture : ILogEventSink
    {
        private readonly List<string> _messages = new();

        public void Emit(LogEvent logEvent)
        {
            lock (_messages)
            {
                _messages.Add(logEvent.RenderMessage());
            }
        }

        public void Clear()
        {
            lock (_messages)
            {
                _messages.Clear();
            }
        }

        public List<string> Messages()
        {
            lock (_messages)
            {
                return [.. _messages];
            }
        }
    }
}
