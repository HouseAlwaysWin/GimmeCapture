using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using GimmeCapture.Services.Core.Infrastructure;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Xunit.Abstractions;

namespace GimmeCapture.Tests;

/// <summary>
/// How often each of this process's threads wakes (its context switches) and how many CPU cycles it takes while
/// Avalonia's real Win32 loop sits idle, as the app does in the tray: with no window, then with a window like the tray
/// host (1x1, transparent, off-screen), each with <see cref="UiThreadStallMonitor"/> running and stopped. Then what the
/// monitor logs for the UI thread held for 600 ms, six times. Opt-in (STALL_MONITOR_PROBE=1): it sets up Avalonia on the
/// real platform, which a process can do only once, and replaces Serilog's logger to read the UiStall lines. Until 2026-11
/// the monitor ticked a 100 ms DispatcherTimer: 84-113 UI-thread wakeups a second here with no window, and 5-6 on its
/// watchdog. Probes posted by the watchdog took that to 19-21 and 16-18.
/// </summary>
public sealed class UiStallMonitorIdleProbeTests
{
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan Hold = TimeSpan.FromMilliseconds(600);

    private readonly ITestOutputHelper _output;

    public UiStallMonitorIdleProbeTests(ITestOutputHelper output) => _output = output;

    [OptInFact("STALL_MONITOR_PROBE=1")]
    public void IdleWakeups_AndWhatAHeldUiThreadReads()
    {
        var lines = new LineCapture();
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

        // The UI thread held at a random point between the monitor's beats.
        var random = new Random(7);
        var readings = new List<double>();
        for (int i = 0; i < 6; i++)
        {
            lines.Clear();
            PumpFor(TimeSpan.FromMilliseconds(300 + random.Next(200)));
            Dispatcher.UIThread.Post(() => Thread.Sleep(Hold), DispatcherPriority.Normal);
            PumpFor(TimeSpan.FromSeconds(1.2));
            var match = lines.Messages().Select(m => Regex.Match(m, @"did not pump for (\d+)ms")).FirstOrDefault(m => m.Success);
            readings.Add(match is null ? 0 : double.Parse(match.Groups[1].Value));
        }

        _output.WriteLine($"UI thread held {Hold.TotalMilliseconds:0} ms, six times: UiStall read {string.Join(", ", readings.Select(r => r.ToString("0")))} ms");

        UiThreadStallMonitor.Stop();
        host.Close();
        PumpFor(TimeSpan.FromMilliseconds(500));

        // A stall reads short by up to one beat of the monitor (100 ms), and never long.
        Assert.All(readings, r => Assert.InRange(r, Hold.TotalMilliseconds - 150, Hold.TotalMilliseconds + 50));
    }

    private void Measure(string what)
    {
        uint uiThread = GetCurrentThreadId();
        var before = Snapshot();
        var started = Stopwatch.GetTimestamp();
        PumpFor(Window);
        double seconds = Stopwatch.GetElapsedTime(started).TotalSeconds;
        var after = Snapshot();

        // Thread times advance in scheduler ticks (15.6 ms), so a thread that wakes for microseconds at a time reads as
        // using none; its cycle count is exact.
        var rows = after
            .Select(t =>
            {
                var was = before.TryGetValue(t.Key, out var b) ? b : default;
                return (Thread: t.Key, Wakeups: (t.Value.Switches - was.Switches) / seconds, MegaCycles: (t.Value.Cycles - was.Cycles) / 1e6 / seconds, New: !before.ContainsKey(t.Key));
            })
            .Where(t => t.Wakeups > 0.05)
            .OrderByDescending(t => t.Wakeups)
            .ToList();
        _output.WriteLine($"{what}: {rows.Sum(t => t.Wakeups):0.0} wakeups/s, {rows.Sum(t => t.MegaCycles):0.00} M cycles/s");
        foreach (var row in rows)
        {
            string name = row.Thread == uiThread ? "UI thread" : Describe(row.Thread);
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

    // Context switches and CPU cycles of each of this process's threads. The switches come from the snapshot of every
    // process (SYSTEM_PROCESS_INFORMATION, 256 bytes on 64-bit Windows, then one 80-byte SYSTEM_THREAD_INFORMATION a
    // thread).
    private static Dictionary<uint, (long Switches, long Cycles)> Snapshot()
    {
        int size = 4 << 20;
        while (true)
        {
            IntPtr buffer = Marshal.AllocHGlobal(size);
            try
            {
                int status = NtQuerySystemInformation(5, buffer, size, out int written);
                if (status == unchecked((int)0xC0000004))
                {
                    size = Math.Max(size * 2, written + (1 << 20));
                    continue;
                }

                Assert.Equal(0, status);
                long offset = 0;
                while (true)
                {
                    IntPtr entry = buffer + (int)offset;
                    if (Marshal.ReadInt64(entry, 80) == Environment.ProcessId)
                    {
                        var threads = new Dictionary<uint, (long, long)>();
                        int count = Marshal.ReadInt32(entry, 4);
                        for (int i = 0; i < count; i++)
                        {
                            IntPtr thread = entry + 256 + (i * 80);
                            uint id = (uint)Marshal.ReadInt64(thread, 48);
                            threads[id] = ((uint)Marshal.ReadInt32(thread, 64), Cycles(id));
                        }

                        return threads;
                    }

                    uint next = (uint)Marshal.ReadInt32(entry, 0);
                    Assert.NotEqual(0u, next);
                    offset += next;
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
    }

    // 0 for a thread gone since the snapshot.
    private static long Cycles(uint threadId)
    {
        IntPtr handle = OpenThread(0x0800, false, threadId);
        if (handle == IntPtr.Zero)
        {
            return 0;
        }

        try
        {
            return QueryThreadCycleTime(handle, out ulong cycles) ? (long)cycles : 0;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    private static string Describe(uint threadId)
    {
        IntPtr handle = OpenThread(0x0800, false, threadId);
        if (handle == IntPtr.Zero)
        {
            return "?";
        }

        try
        {
            if (GetThreadDescription(handle, out IntPtr text) < 0 || text == IntPtr.Zero)
            {
                return "?";
            }

            string name = Marshal.PtrToStringUni(text) ?? "";
            LocalFree(text);
            return name.Length > 0 ? name : "(no name)";
        }
        finally
        {
            CloseHandle(handle);
        }
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

    [DllImport("ntdll.dll")]
    private static extern int NtQuerySystemInformation(int infoClass, IntPtr buffer, int length, out int written);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenThread(uint access, bool inherit, uint threadId);

    [DllImport("kernel32.dll")]
    private static extern bool QueryThreadCycleTime(IntPtr thread, out ulong cycles);

    [DllImport("kernel32.dll")]
    private static extern int GetThreadDescription(IntPtr thread, out IntPtr description);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);
}
