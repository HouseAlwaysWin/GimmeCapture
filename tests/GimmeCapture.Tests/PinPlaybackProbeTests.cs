using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Threading;
using GimmeCapture.Services.Interop;
using GimmeCapture.ViewModels.Floating;
using GimmeCapture.Views.Floating;
using Xunit.Abstractions;

namespace GimmeCapture.Tests;

/// <summary>
/// What a pinned recording costs while it plays, thread by thread, in a real Win32 window off screen: the pin of a
/// recording the user made, shown pixel for pixel and muted, as the overlay opens one after a recording. On a 1450x900
/// HEVC recording drawn on the GPU (2026-11-06): the process 1032-1055 M cycles/s, Avalonia's render thread 469-496, the
/// decode thread 283-287, which converting NV12 through YUV420P (LibavBgraConverter) took to 188-212. The toolbar's
/// drop shadow, redrawn for every move of the playhead, then cost the render thread 150 more than without it; drawn by a
/// GlowDecorator, the process took 814-823, and with the decode thread writing frames into the bitmap the UI thread
/// took 40-43 instead of 77. PIN_PLAYBACK_VARIANT=paused, paused-no-toolbar or no-toolbar measures those;
/// PIN_TIMELINE_FPS sets the playhead's rate, PIN_PLAYBACK_SECONDS the length measured. Opt-in (PIN_PLAYBACK_PROBE=1,
/// PIN_PLAYBACK_CLIP=a video file; PIN_PLAYBACK_RENDERER=software draws on the CPU): it sets up Avalonia on the real
/// platform, which a process can do only once.
/// </summary>
public sealed class PinPlaybackProbeTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _baseDir = Path.Combine(Path.GetTempPath(), "GimmeCapture.Tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_baseDir, true); } catch { /* best effort */ }
    }

    [OptInFact("PIN_PLAYBACK_PROBE=1", "PIN_PLAYBACK_CLIP")]
    public void APinnedRecordingPlaying_ThreadByThread()
    {
        string clip = Environment.GetEnvironmentVariable("PIN_PLAYBACK_CLIP")!;
        bool software = Environment.GetEnvironmentVariable("PIN_PLAYBACK_RENDERER") == "software";
        AppBuilder.Configure<GimmeCapture.App>()
            .UsePlatformDetect()
            .With(GimmeCapture.Composition.StartupRenderer.Win32Options(software))
            .WithInterFont()
            .SetupWithoutStarting();
        GimmeCapture.Composition.StartupRenderer.IsSoftware = software;

        // As the app runs in the tray: its host window shown, so the renderer's own set-up is paid before the pin.
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
        PumpFor(TimeSpan.FromSeconds(2));

        var (width, height) = ProbeSize(clip);
        double scaling = host.RenderScaling;
        var settings = new AppSettingsService(_baseDir);
        if (Environment.GetEnvironmentVariable("PIN_TIMELINE_FPS") is { } timelineFps)
        {
            settings.Settings.PlaybackTimelineFps = int.Parse(timelineFps);
        }

        var vm = new FloatingVideoViewModel(
            clip, width, height, width / scaling, height / scaling, Colors.Red, 2.0, false, false,
            Moq.Mock.Of<GimmeCapture.Services.Abstractions.IClipboardService>(), settings)
        {
            IsMuted = true,
        };
        var padding = vm.WindowPadding;
        var window = new FloatingVideoWindow
        {
            DataContext = vm,
            Width = (width / scaling) + padding.Left + padding.Right,
            Height = (height / scaling) + padding.Top + padding.Bottom,
            Position = new PixelPoint(-20000, -20000),
            ShowActivated = false,
        };
        window.Show();
        // Windows puts a new window on a screen whatever position it was given: moved off every screen once shown, it
        // stays there, out of the way of whoever uses the desktop meanwhile.
        window.Position = new PixelPoint(-20000, -20000);
        PumpFor(TimeSpan.FromSeconds(4));
        Assert.True(window.Position.X < -10000, $"the pin is on screen at {window.Position}");
        string variant = Environment.GetEnvironmentVariable("PIN_PLAYBACK_VARIANT") ?? "playing";
        if (variant.StartsWith("paused", StringComparison.Ordinal))
        {
            vm.TogglePlaybackCommand.Execute().Subscribe();
            PumpFor(TimeSpan.FromSeconds(2));
            Assert.False(vm.IsPlaying);
            if (variant == "paused-no-toolbar")
            {
                vm.ShowToolbar = false;
                PumpFor(TimeSpan.FromSeconds(1));
            }
        }
        else if (variant == "no-toolbar")
        {
            vm.ShowToolbar = false;
            PumpFor(TimeSpan.FromSeconds(1));
        }

        output.WriteLine($"variant {variant}, toolbar {vm.ShowToolbar}, playing {vm.IsPlaying}");

        uint ui = ThreadActivity.CurrentThreadId;
        // What the UI thread does besides the frames: the playhead's changes, and the layout passes they cause.
        int layouts = 0, resizes = 0, moves = 0;
        var events = new List<string>();
        var clock = Stopwatch.StartNew();
        void Note(string what) { if (events.Count < 60) events.Add($"{clock.ElapsedMilliseconds,6} {what}"); }
        window.PositionChanged += (_, e) => { moves++; Note($"moved to {e.Point}"); };
        window.GetObservable(Visual.BoundsProperty).Subscribe(b => Note($"bounds {b.Width:0.#}x{b.Height:0.#}"));
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is "ToolbarMargin" or "IsToolbarFlipped" or "SubToolbarMargin" or "WindowPadding" or "DisplayWidth" or "DisplayHeight")
            {
                Note($"{e.PropertyName} = {vm.GetType().GetProperty(e.PropertyName)?.GetValue(vm)}");
            }
        };
        var changed = new Dictionary<string, int>();
        window.LayoutUpdated += (_, _) => layouts++;
        window.SizeChanged += (_, _) => resizes++;
        vm.PropertyChanged += (_, e) => changed[e.PropertyName ?? "?"] = changed.GetValueOrDefault(e.PropertyName ?? "?") + 1;
        long before = ProcessCycles();
        var watch = Stopwatch.StartNew();
        var rows = ThreadActivity.Measure(() => PumpFor(TimeSpan.FromSeconds(double.Parse(Environment.GetEnvironmentVariable("PIN_PLAYBACK_SECONDS") ?? "10"))));
        double seconds = watch.Elapsed.TotalSeconds;
        long process = ProcessCycles() - before;

        output.WriteLine($"{(software ? "software" : "GPU")} rendering, {Path.GetFileName(clip)} {width}x{height} at scaling {scaling}: " +
            $"process {process / 1e6 / seconds:0} M cycles/s ({process / 1e6 / seconds / 32:0.0}% of a 3.2 GHz core)");
        foreach (var row in rows.OrderByDescending(r => r.MegaCycles).Take(8))
        {
            output.WriteLine($"  {row.MegaCycles,8:0.0} M cycles/s {row.Wakeups,7:0.0} wakeups/s  {(row.Thread == ui ? "UI thread" : ThreadActivity.Describe(row.Thread))}");
        }

        var uiRow = rows.FirstOrDefault(r => r.Thread == ui);
        if (moves > 5)
        {
            foreach (string e in events) output.WriteLine("  event " + e);
            var screen = window.Screens.ScreenFromWindow(window);
            output.WriteLine($"  screen bounds {screen?.Bounds} working {screen?.WorkingArea} scaling {screen?.Scaling}; window {window.Bounds} min {window.MinWidth}x{window.MinHeight}");
        }

        output.WriteLine($"UI thread {uiRow.Wakeups:0}/s {uiRow.MegaCycles:0} Mc/s; layouts {layouts / seconds:0.0}/s, resizes {resizes / seconds:0.0}/s, moves {moves / seconds:0.0}/s at {window.Position}; changes/s: " +
            string.Join(", ", changed.OrderByDescending(c => c.Value).Take(6).Select(c => $"{c.Key} {c.Value / seconds:0.0}")));

        window.Close();
        vm.Dispose();
        PumpFor(TimeSpan.FromMilliseconds(500));
    }

    private static (int Width, int Height) ProbeSize(string clip)
    {
        GimmeCapture.Services.Core.Media.NativeFFmpeg.FFmpegRuntime.EnsureInitialized();
        using var player = new GimmeCapture.Services.Core.Media.NativeFFmpeg.LibavVideoFramePlayer();
        var size = player.ProbeVideoSizeAsync(clip).GetAwaiter().GetResult();
        Assert.NotNull(size);
        return size.Value;
    }

    private static void PumpFor(TimeSpan time)
    {
        using var cts = new CancellationTokenSource();
        using var end = new Timer(_ => Dispatcher.UIThread.Post(cts.Cancel), null, time, Timeout.InfiniteTimeSpan);
        Dispatcher.UIThread.MainLoop(cts.Token);
    }

    private static long ProcessCycles()
    {
        QueryProcessCycleTime(Process.GetCurrentProcess().Handle, out ulong cycles);
        return (long)cycles;
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern bool QueryProcessCycleTime(IntPtr process, out ulong cycles);
}
