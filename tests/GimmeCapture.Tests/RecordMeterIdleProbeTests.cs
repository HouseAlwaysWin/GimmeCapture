using System.Diagnostics;
using Avalonia;
using Avalonia.Threading;
using GimmeCapture.Models;
using GimmeCapture.Services.Core.Media;
using GimmeCapture.ViewModels.Main;
using Xunit.Abstractions;

namespace GimmeCapture.Tests;

/// <summary>
/// What the capture overlay's audio meter costs while it runs, in Record mode and through a whole recording: the UI
/// thread's wakeups and cycles with an overlay's view model in Record mode against the same view model in Screenshot
/// mode, the property changes it raises, and how long its first reading takes on an STA thread such as the app's UI
/// thread, where the audio devices' COM objects used to be made. In silence on 2026-11-05, read from a DispatcherTimer
/// on the UI thread: 63.1 UI-thread wakeups and 5.2 M cycles a second, 47.4 property changes, a first reading of 58 ms
/// (14 ms once the audio DLLs were cached). Read on <c>AudioLevelMeter</c>'s own thread: the UI thread out of the
/// busiest eight, no property changes, the meter's thread 16.5-16.9 wakeups at 1.3-1.5 M cycles. Opt-in
/// (RECORD_METER_PROBE=1): it sets up Avalonia on the real platform, which a process can do only once.
/// </summary>
public sealed class RecordMeterIdleProbeTests
{
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(10);

    private readonly ITestOutputHelper _output;

    public RecordMeterIdleProbeTests(ITestOutputHelper output) => _output = output;

    [OptInFact("RECORD_METER_PROBE=1")]
    public void TheRecordModeMeter_WhileTheOverlayWaits()
    {
        // Cold, as the first tick finds it, on an STA thread like the app's UI thread.
        double firstMs = 0, laterMs = 0;
        var sta = new Thread(() =>
        {
            using var meter = new AudioLevelMonitorService();
            var watch = Stopwatch.StartNew();
            meter.TryRead(true, null, out _, out _);
            firstMs = watch.Elapsed.TotalMilliseconds;
            watch.Restart();
            for (int i = 0; i < 50; i++)
            {
                meter.TryRead(true, null, out _, out _);
            }

            laterMs = watch.Elapsed.TotalMilliseconds / 50;
        });
        sta.SetApartmentState(ApartmentState.STA);
        sta.Start();
        sta.Join();
        _output.WriteLine($"meter on an STA thread: first reading {firstMs:0.0} ms, then {laterMs:0.00} ms each");

        AppBuilder.Configure<GimmeCapture.App>()
            .UsePlatformDetect()
            .With(GimmeCapture.Composition.StartupRenderer.Win32Options(false))
            .WithInterFont()
            .SetupWithoutStarting();
        // Avalonia's start-up keeps the UI thread waking for about half a minute (UiStallMonitorIdleProbeTests).
        PumpFor(TimeSpan.FromSeconds(30));

        using var vm = new SnipWindowViewModel();
        int changes = 0;
        vm.PropertyChanged += (_, _) => changes++;
        var screenshot = Measure("screenshot mode");
        int screenshotChanges = changes;

        vm.CurrentMode = SnipMode.Recording;
        PumpFor(TimeSpan.FromSeconds(2));
        changes = 0;
        var record = Measure("record mode");
        _output.WriteLine(
            $"property changes: {screenshotChanges / Window.TotalSeconds:0.0}/s in screenshot mode, " +
            $"{changes / Window.TotalSeconds:0.0}/s in record mode; output level now {vm.OutputAudioLevel:0.00}");

        vm.CurrentMode = SnipMode.Screenshot;
        PumpFor(TimeSpan.FromMilliseconds(500));
        Assert.True(screenshot.Wakeups >= 0 && record.Wakeups >= 0);
    }

    private ThreadActivity.Rate Measure(string what)
    {
        uint uiThread = ThreadActivity.CurrentThreadId;
        var rows = ThreadActivity.Measure(() => PumpFor(Window));
        _output.WriteLine($"{what}: {rows.Sum(t => t.Wakeups):0.0} wakeups/s, {rows.Sum(t => t.MegaCycles):0.00} M cycles/s");
        foreach (var row in rows.Take(8))
        {
            string name = row.Thread == uiThread ? "UI thread" : ThreadActivity.Describe(row.Thread);
            _output.WriteLine($"  {row.Wakeups,7:0.0}/s {row.MegaCycles,7:0.000} M cycles/s  {name}{(row.New ? " (new)" : "")}");
        }

        return rows.FirstOrDefault(r => r.Thread == uiThread);
    }

    // Cancelled by a job on the UI thread: Avalonia's loop reads the token only between messages (UiStallMonitorIdleProbeTests).
    private static void PumpFor(TimeSpan time)
    {
        using var cts = new CancellationTokenSource();
        using var end = new Timer(_ => Dispatcher.UIThread.Post(cts.Cancel), null, time, Timeout.InfiniteTimeSpan);
        Dispatcher.UIThread.MainLoop(cts.Token);
    }
}
