using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GimmeCapture.Models;
using GimmeCapture.ViewModels.Main;
using GimmeCapture.Views.Main;

namespace GimmeCapture.Tests;

/// <summary>
/// The Compress editor's picture while its seek bar or its strip of pieces is dragged, through the real window and a
/// clip whose every frame is its own grey: the frame under the pointer shows during the drag, from a keyframe near it,
/// and the exact frame once the drag ends. Until 2026-11 nothing but the time moved until the release. Each test is
/// opt-in by a switch of its own (EDITOR_SCRUB_PROBE=1, EDITOR_CUT_PLAY_PROBE=1, EDITOR_KEYS_PROBE=1): it sets up a headless
/// Avalonia application, which a process can do only once.
/// </summary>
public sealed class VideoEditWindowScrubTests : IDisposable
{
    private const int Width = 64;
    private const int Height = 48;
    private const int Fps = 30;
    private const int FrameCount = 30; // a grey of its own for each, in the 219 levels of luma

    // What one step of the clips' grey (7 levels of luma) comes to in BGRA.
    private const double GreyStep = 7 * 255.0 / 219;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "GimmeCapture.Tests", Guid.NewGuid().ToString("N"));

    public VideoEditWindowScrubTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [OptInFact("EDITOR_CUT_PLAY_PROBE=1")]
    public void PlayingAVideoWithAPieceCutOut_ShowsWhatIsKept_AndNoFrameOfThatPiece()
    {
        SetUpAvalonia();
        string clip = VideoTestClips.WriteConstantRate(Path.Combine(_dir, "clip.mp4"), Width, Height, FrameCount, Fps, gopSize: 10);
        double duration = (double)FrameCount / Fps;
        var vm = new VideoEditViewModel(
            clip, duration, Fps, Width, Height,
            VideoEditResult.Empty with { KeptRuns = [new VideoEditSegment(0, 0.3), new VideoEditSegment(0.7, duration)] }, _ => { });
        var window = new VideoEditWindow { DataContext = vm, Width = 960, Height = 760 };
        window.Show();
        Pump(() => !vm.IsPreparing && ShownFrame(vm) == 0, "the first frame");

        var shown = new HashSet<int>();
        vm.PlayPauseCommand.Execute().Subscribe();
        Assert.True(vm.IsPlaying);
        Pump(
            () =>
            {
                if (ShownFrame(vm) is { } frame)
                {
                    shown.Add(frame);
                }

                return !vm.IsPlaying;
            },
            "the end of the playback");

        Assert.DoesNotContain(shown, frame => frame is >= 10 and <= 20); // 9 and 21 are within a rounding of the cuts
        Assert.Contains(shown, frame => frame is > 0 and < 9);
        Assert.Contains(shown, frame => frame >= 21);

        window.Close();
        Dispatcher.UIThread.RunJobs();
    }

    [OptInFact("EDITOR_KEYS_PROBE=1")]
    public void SpaceAndTheArrows_WorkThePlayback_ExceptWhereTheKeysAreForTyping()
    {
        SetUpAvalonia();
        string clip = VideoTestClips.WriteConstantRate(Path.Combine(_dir, "clip.mp4"), Width, Height, FrameCount, Fps, gopSize: 10);
        double duration = (double)FrameCount / Fps;
        var vm = new VideoEditViewModel(
            clip, duration, Fps, Width, Height,
            VideoEditResult.Empty with { KeptRuns = [new VideoEditSegment(0, duration)] }, _ => { });
        vm.OutputFileName = "name";
        var window = new VideoEditWindow { DataContext = vm, Width = 960, Height = 760 };
        window.Show();
        Pump(() => !vm.IsPreparing && ShownFrame(vm) == 0, "the first frame");

        // With the seek bar focused, which would take the arrows for moves of its own.
        window.FindControl<Slider>("PositionSlider")!.Focus();
        Tap(window, Key.Right, PhysicalKey.ArrowRight);
        Pump(() => ShownFrame(vm) == 1, "a frame on");
        Assert.Equal(1.0 / Fps, vm.PositionSeconds, 3);
        Tap(window, Key.Left, PhysicalKey.ArrowLeft);
        Pump(() => ShownFrame(vm) == 0, "a frame back");

        // Held: the key repeats, and the picture follows to where it stopped.
        for (int repeat = 0; repeat < 6; repeat++)
        {
            window.KeyPress(Key.Right, RawInputModifiers.None, PhysicalKey.ArrowRight, null);
        }

        window.KeyRelease(Key.Right, RawInputModifiers.None, PhysicalKey.ArrowRight, null);
        Pump(() => ShownFrame(vm) == 6, "six frames on");

        // Space with the Play button focused: once, not once for the shortcut and once for the button's click.
        Button play = window.GetVisualDescendants().OfType<Button>().First(button => ReferenceEquals(button.Command, vm.PlayPauseCommand));
        play.Focus();
        Tap(window, Key.Space, PhysicalKey.Space);
        Pump(() => vm.IsPlaying, "the playback");
        Dispatcher.UIThread.RunJobs();
        Assert.True(vm.IsPlaying);
        Tap(window, Key.Space, PhysicalKey.Space);
        Pump(() => !vm.IsPlaying, "the pause");

        // Held, Space toggles once.
        window.KeyPress(Key.Space, RawInputModifiers.None, PhysicalKey.Space, null);
        window.KeyPress(Key.Space, RawInputModifiers.None, PhysicalKey.Space, null);
        window.KeyRelease(Key.Space, RawInputModifiers.None, PhysicalKey.Space, null);
        Pump(() => vm.IsPlaying, "the playback from a held key");
        Assert.True(vm.IsPlaying);
        Tap(window, Key.Space, PhysicalKey.Space);
        Pump(() => !vm.IsPlaying, "the second pause");

        // In the file name box the keys are for typing.
        TextBox name = window.GetVisualDescendants().OfType<TextBox>().First(box => box.Text == "name");
        name.Focus();
        double before = vm.PositionSeconds;
        Tap(window, Key.Space, PhysicalKey.Space);
        Tap(window, Key.Left, PhysicalKey.ArrowLeft);
        Dispatcher.UIThread.RunJobs();
        Assert.False(vm.IsPlaying);
        Assert.Equal(before, vm.PositionSeconds);

        window.Close();
        Dispatcher.UIThread.RunJobs();
    }

    private static void Tap(Window window, Key key, PhysicalKey physical)
    {
        window.KeyPress(key, RawInputModifiers.None, physical, null);
        window.KeyRelease(key, RawInputModifiers.None, physical, null);
    }

    [OptInFact("EDITOR_SCRUB_PROBE=1")]
    public void DraggingTheSeekBarOrTheStrip_ShowsThePictureUnderThePointer_AndTheDropTheExactFrame()
    {
        SetUpAvalonia();

        string clip = VideoTestClips.WriteConstantRate(Path.Combine(_dir, "clip.mp4"), Width, Height, FrameCount, Fps, gopSize: 10);
        double duration = (double)FrameCount / Fps;
        var vm = new VideoEditViewModel(
            clip, duration, Fps, Width, Height,
            VideoEditResult.Empty with { KeptRuns = [new VideoEditSegment(0, duration)] }, _ => { });
        var window = new VideoEditWindow { DataContext = vm, Width = 960, Height = 760 };
        window.Show();
        Pump(() => !vm.IsPreparing && ShownFrame(vm) == 0, "the first frame");

        // The seek bar, pressed near its start and dragged along it.
        var slider = window.FindControl<Slider>("PositionSlider")!;
        window.MouseDown(OnSlider(slider, window, 0.05), MouseButton.Left);
        Pump(() => vm.PositionSeconds > 0, "the press on the bar");
        window.MouseMove(OnSlider(slider, window, 0.55), RawInputModifiers.LeftMouseButton);
        double dragged = vm.PositionSeconds;
        int expected = (int)Math.Ceiling(dragged * Fps);
        Assert.InRange(dragged, duration * 0.4, duration * 0.7);

        // Before the release, the keyframe the position's seek lands on: within one group of ten frames of it.
        Pump(() => ShownFrame(vm) is { } f && f >= expected - 10 && f <= expected, $"a frame near {expected} during the drag");
        int duringDrag = ShownFrame(vm)!.Value;

        window.MouseUp(OnSlider(slider, window, 0.55), MouseButton.Left);
        Pump(() => ShownFrame(vm) == expected, $"the exact frame {expected} (the drag showed {duringDrag})");

        // The strip of pieces, the same way.
        var strip = window.FindControl<Grid>("SegmentStripGrid")!;
        window.MouseDown(OnStrip(strip, window, 0.2), MouseButton.Left);
        window.MouseMove(OnStrip(strip, window, 0.8), RawInputModifiers.LeftMouseButton);
        double stripDragged = vm.PositionSeconds;
        Assert.InRange(stripDragged, duration * 0.7, duration * 0.9);
        int stripExpected = (int)Math.Ceiling(stripDragged * Fps);
        Pump(() => ShownFrame(vm) is { } f && f >= stripExpected - 10 && f <= stripExpected, $"a frame near {stripExpected} on the strip");
        window.MouseUp(OnStrip(strip, window, 0.8), MouseButton.Left);
        Pump(() => ShownFrame(vm) == stripExpected, $"the exact frame {stripExpected} after the strip");

        window.Close();
        Dispatcher.UIThread.RunJobs();
    }

    private static void SetUpAvalonia() =>
        AppBuilder.Configure<GimmeCapture.App>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .SetupWithoutStarting();

    private static Point OnSlider(Slider slider, Window window, double fraction)
    {
        // The thumb's half width at each end of the track, about.
        const double inset = 10;
        double x = inset + (fraction * (slider.Bounds.Width - (2 * inset)));
        return slider.TranslatePoint(new Point(x, slider.Bounds.Height / 2), window)!.Value;
    }

    private static Point OnStrip(Grid strip, Window window, double fraction) =>
        strip.TranslatePoint(new Point(fraction * strip.Bounds.Width, strip.Bounds.Height / 2), window)!.Value;

    // The frame the preview shows: its centre pixel's grey says which.
    private static unsafe int? ShownFrame(VideoEditViewModel vm)
    {
        if (vm.Frame is not WriteableBitmap bitmap)
        {
            return null;
        }

        using var locked = bitmap.Lock();
        byte* row = (byte*)locked.Address + ((Height / 2) * locked.RowBytes);
        return (int)Math.Round(row[(Width / 2) * 4] / GreyStep);
    }

    private static void Pump(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, $"Timed out waiting for {what}.");
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(5);
        }
    }
}
