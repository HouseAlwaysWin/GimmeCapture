using System.Diagnostics;
using GimmeCapture.Services.Core.Infrastructure;

namespace GimmeCapture.Tests;

// The stall monitor's watchdog thread, without a dispatcher: each beat is timed as it comes, on the watchdog's own thread,
// and the tests judge those times, never a count read after an await: with the rest of the suite running, the test's
// continuation can come hundreds of milliseconds late. Until 2026-11 the watchdog beat every 100 ms whatever the app
// showed, which in the tray was most of the app's idle wakeups (about 44 of the 67 a second the user's copy woke, the UI
// thread's share of each probe included); it beats once a second there now.
public sealed class UiStallWatchdogTests
{
    private static readonly TimeSpan Window = TimeSpan.FromMilliseconds(2500);

    private sealed class Beats
    {
        private readonly object _gate = new();
        private readonly List<TimeSpan> _times = new();
        private readonly Stopwatch _clock = Stopwatch.StartNew();

        public void Beat()
        {
            lock (_gate)
            {
                _times.Add(_clock.Elapsed);
            }
        }

        public TimeSpan Now => _clock.Elapsed;

        public List<TimeSpan> Times
        {
            get
            {
                lock (_gate)
                {
                    return [.. _times];
                }
            }
        }
    }

    [Fact]
    public async Task InTheTray_ItBeatsOnceASecond_AndTenTimesWithAWindowShown()
    {
        var tray = new Beats();
        var inTray = new UiStallWatchdog(tray.Beat, windowShown: false);
        var shown = new Beats();
        var withWindow = new UiStallWatchdog(shown.Beat, windowShown: true);
        inTray.Start("test tray watchdog");
        withWindow.Start("test window watchdog");
        try
        {
            await OwnThread.Delay(Window + TimeSpan.FromMilliseconds(100));
        }
        finally
        {
            inTray.Stop();
            withWindow.Stop();
        }

        // In the first 2.5 s: beats at 1 s and 2 s, where the old watchdog beat 25 times.
        Assert.InRange(tray.Times.Count(t => t < Window), 1, 3);
        // 22-25 as Windows rounds each wait to its timer's tick; a busy machine makes it fewer.
        Assert.InRange(shown.Times.Count(t => t < Window), 10, 27);
    }

    [Fact]
    public async Task AWindowShown_StartsTheShortBeatAtOnce()
    {
        var beats = new Beats();
        var watchdog = new UiStallWatchdog(beats.Beat, windowShown: false);
        watchdog.Start("test watchdog");
        try
        {
            await OwnThread.Delay(TimeSpan.FromMilliseconds(200));
            TimeSpan shownAt = beats.Now;
            watchdog.WindowShown = true;
            await OwnThread.Delay(TimeSpan.FromMilliseconds(700));

            var after = beats.Times.Where(t => t >= shownAt).ToList();
            Assert.NotEmpty(after);
            // The first came as the window did, not at the tray beat's next second.
            Assert.True(after[0] - shownAt < TimeSpan.FromMilliseconds(400), $"first beat {(after[0] - shownAt).TotalMilliseconds:0} ms after the window");
            int soon = after.Count(t => t - shownAt < TimeSpan.FromMilliseconds(600));
            Assert.True(soon >= 2, $"{soon} beats in the 600 ms after the window");
        }
        finally
        {
            watchdog.Stop();
        }
    }

    [Fact]
    public async Task BackInTheTray_ItSlowsDown_AndStopEndsIt()
    {
        var beats = new Beats();
        var watchdog = new UiStallWatchdog(beats.Beat, windowShown: true);
        watchdog.Start("test watchdog");
        await OwnThread.Delay(TimeSpan.FromMilliseconds(300));
        TimeSpan trayAt = beats.Now;
        watchdog.WindowShown = false;
        await OwnThread.Delay(Window);
        watchdog.Stop();
        TimeSpan stoppedAt = beats.Now;
        await OwnThread.Delay(TimeSpan.FromMilliseconds(1200));

        // The short wait under way as the window went may end with a beat; from then on they come a second apart.
        var inTray = beats.Times.Where(t => t > trayAt && t <= stoppedAt).ToList();
        Assert.True(inTray.Count >= 2, $"{inTray.Count} beats in the tray");
        for (int i = 1; i < inTray.Count; i++)
        {
            var gap = inTray[i] - inTray[i - 1];
            Assert.True(gap > TimeSpan.FromMilliseconds(900), $"beats {gap.TotalMilliseconds:0} ms apart in the tray");
        }

        // One already past its wait as Stop came may still land; none after.
        Assert.DoesNotContain(beats.Times, t => t > stoppedAt + TimeSpan.FromMilliseconds(50));
    }
}
