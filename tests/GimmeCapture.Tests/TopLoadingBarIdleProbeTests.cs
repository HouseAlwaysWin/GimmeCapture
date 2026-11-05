using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using GimmeCapture.Views.Controls;
using Xunit.Abstractions;

namespace GimmeCapture.Tests;

/// <summary>
/// How often the capture overlay's top loading bar keeps the process waking while it is hidden, as it is for most of an
/// overlay's life: it shows while an AI scan or a translation runs. Its animation is a style, and Avalonia only pauses a
/// hidden control's style animation, which keeps it on the animation clock, and with anything on that clock Avalonia
/// renders every 16 ms (<see cref="PinProcessingIdleProbeTests"/>, the same for a pin's processing bar). Opt-in
/// (TOP_LOADING_BAR_PROBE=1, and =software for the CPU renderer): it sets up Avalonia on the real platform, which a
/// process can do only once, and shows a window off screen for about a minute.
/// </summary>
public sealed class TopLoadingBarIdleProbeTests
{
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(8);

    private readonly ITestOutputHelper _output;

    public TopLoadingBarIdleProbeTests(ITestOutputHelper output) => _output = output;

    [OptInFact("TOP_LOADING_BAR_PROBE")]
    public void AHiddenLoadingBar_LeavesTheOverlayIdle()
    {
        bool software = Environment.GetEnvironmentVariable("TOP_LOADING_BAR_PROBE") == "software";
        AppBuilder.Configure<GimmeCapture.App>()
            .UsePlatformDetect()
            .With(GimmeCapture.Composition.StartupRenderer.Win32Options(software))
            .WithInterFont()
            .SetupWithoutStarting();
        // Avalonia's start-up keeps the UI thread waking for about half a minute (UiStallMonitorIdleProbeTests).
        PumpFor(TimeSpan.FromSeconds(30));

        var panel = new Panel();
        var window = new Window
        {
            Content = panel,
            Width = 1536,
            Height = 864,
            Position = new PixelPoint(-20000, -20000),
            ShowActivated = false,
            WindowDecorations = WindowDecorations.None,
        };
        window.Show();
        PumpFor(TimeSpan.FromSeconds(5));
        var empty = Measure("window without the bar", Window);

        // As the overlay builds it: hidden until a scan or a translation runs.
        var bar = new TopLoadingBar { Width = 1536, Height = 6, IsVisible = false };
        panel.Children.Add(bar);
        PumpFor(TimeSpan.FromSeconds(3));
        var hidden = Measure("bar hidden since it was built", Window);

        bar.IsVisible = true;
        PumpFor(TimeSpan.FromSeconds(1));
        var shown = Measure("bar showing", TimeSpan.FromSeconds(3));
        bar.IsVisible = false;
        PumpFor(TimeSpan.FromSeconds(3));
        var after = Measure("bar hidden after it showed", Window);

        window.Close();
        PumpFor(TimeSpan.FromMilliseconds(500));

        // The bar still runs while it shows.
        Assert.True(
            shown.Wakeups > empty.Wakeups + 50,
            $"The UI thread woke {shown.Wakeups:0.0} times a second with the bar showing, {empty.Wakeups:0.0} without it.");
        Assert.True(
            hidden.Wakeups <= empty.Wakeups + 5,
            $"The UI thread woke {hidden.Wakeups:0.0} times a second with the bar hidden, {empty.Wakeups:0.0} without it.");
        Assert.True(
            after.Wakeups <= empty.Wakeups + 5,
            $"The UI thread woke {after.Wakeups:0.0} times a second after the bar hid, {empty.Wakeups:0.0} without it.");
    }

    // The UI thread's rate, with the busier threads printed.
    private ThreadActivity.Rate Measure(string what, TimeSpan window)
    {
        uint uiThread = ThreadActivity.CurrentThreadId;
        var rows = ThreadActivity.Measure(() => PumpFor(window));
        _output.WriteLine($"{what}: {rows.Sum(t => t.Wakeups):0.0} wakeups/s, {rows.Sum(t => t.MegaCycles):0.00} M cycles/s");
        foreach (var row in rows.Where(r => r.MegaCycles > 0.05).Take(6))
        {
            string name = row.Thread == uiThread ? "UI thread" : ThreadActivity.Describe(row.Thread);
            _output.WriteLine($"  {row.Wakeups,7:0.0}/s {row.MegaCycles,7:0.000} M cycles/s  {name}{(row.New ? " (new)" : "")}");
        }

        return rows.FirstOrDefault(r => r.Thread == uiThread);
    }

    // Cancelled by a job on the UI thread: Avalonia's loop reads the token only between messages, so with nothing posted
    // it would sleep through a token cancelled from another thread (UiStallMonitorIdleProbeTests).
    private static void PumpFor(TimeSpan time)
    {
        using var cts = new CancellationTokenSource();
        using var end = new Timer(_ => Dispatcher.UIThread.Post(cts.Cancel), null, time, Timeout.InfiniteTimeSpan);
        Dispatcher.UIThread.MainLoop(cts.Token);
    }
}
