using System.Drawing;
using System.Threading;
using System.Windows.Forms;
using FFmpeg.AutoGen;
using GimmeCapture.Models;
using GimmeCapture.Services.Core.Media.NativeFFmpeg;
using GimmeCapture.Services.Interop;
using GimmeCapture.Services.Platforms.Windows;

namespace GimmeCapture.Tests;

/// <summary>
/// Records about a second and a half with each native recorder session and checks the Matroska segment it leaves: the
/// encoder it picked, the frame size, a duration close to the time recorded, the frame rate, and a frame that decodes.
/// They pin what a recording produces, so the encode-and-mux code the three sessions share can be moved under them.
/// The region cases capture a corner of the desktop without showing anything and run with the other Windows tests,
/// through gdigrab and through Desktop Duplication; the WGC ones and the ones that look at what was recorded put small
/// windows on screen, so they are opt-in (RECORDING_PROBE=1).
/// </summary>
public sealed class LibavRecordingSessionTests : IDisposable
{
    private const int Fps = 15; // not 30, which is what the frame-rate probe reports when it fails
    private const string ProbeSwitch = "RECORDING_PROBE=1";
    private static readonly TimeSpan Length = TimeSpan.FromSeconds(1.5);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "GimmeCapture.Tests", Guid.NewGuid().ToString("N"));

    public LibavRecordingSessionTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    [Theory]
    [InlineData(false, false, false)] // frame-counter timestamps on one thread: the default loop
    [InlineData(true, false, false)]  // wall-clock timestamps, which RecordingService always asks for
    [InlineData(false, true, false)]  // capture and encode on separate threads
    [InlineData(true, false, true)]   // cursor and keystroke overlays, drawn through a BGRA round-trip
    [InlineData(true, true, true)]    // separate threads, with the capture side stamping and drawing
    public async Task Gdigrab_RecordsTheRegion(bool wallClockPts, bool pipelined, bool overlays)
    {
        string path = Path.Combine(_dir, "gdigrab.mkv");
        using var session = new LibavGdigrabMkvSession
        {
            PreferHardwareEncoder = false,
            UseWallClockPts = wallClockPts,
            PipelinedEncoding = pipelined,
            HighlightCursor = overlays,
            ShowKeystrokes = overlays,
            UseDesktopDuplication = false,
        };

        await RecordAsync(
            session.StartAsync(path, 0, 0, 320, 240, Fps, drawMouse: true, VideoCodec.H264),
            session.StopAsync,
            () => session.LastErrorMessage);

        Assert.Equal("gdigrab", session.CaptureSourceName);
        await AssertSegmentAsync(path, session.SelectedEncoderName, expectedSize: (320, 240));
    }

    // A region on one display goes through Desktop Duplication wherever this machine can duplicate it (the corner of
    // the main display), and through gdigrab where it cannot, recording the same either way.
    [Theory]
    [InlineData(false, false)] // the loop RecordingService uses
    [InlineData(true, true)]   // separate threads, cursor and keystroke overlays drawn on the duplicated BGRA
    public async Task DesktopDuplication_RecordsTheRegion_WhereItCanDuplicateTheDisplay(bool pipelined, bool overlays)
    {
        string path = Path.Combine(_dir, "ddagrab.mkv");
        using var session = new LibavGdigrabMkvSession
        {
            PreferHardwareEncoder = false,
            UseWallClockPts = true,
            PipelinedEncoding = pipelined,
            HighlightCursor = overlays,
            ShowKeystrokes = overlays,
        };

        await RecordAsync(
            session.StartAsync(path, 0, 0, 320, 240, Fps, drawMouse: true, VideoCodec.H264),
            session.StopAsync,
            () => session.LastErrorMessage);

        Assert.Equal(CanDuplicate(0, 0, 320, 240) ? "ddagrab" : "gdigrab", session.CaptureSourceName);
        await AssertSegmentAsync(path, session.SelectedEncoderName, expectedSize: (320, 240));
    }

    // Desktop Duplication captures one display, so a region wider than any display is left to gdigrab, which takes the
    // whole virtual screen.
    [Fact]
    public void DesktopDuplication_TakesNoRegionNoSingleDisplayHolds()
    {
        Assert.Null(DxgiOutputs.TryCreateDuplicationDevice(0, 0, 100_000, 240));
    }

    // The file says the BT.601 its pictures were converted with: unsaid, players took BT.709 for HD and shifted them.
    [Fact]
    public async Task Gdigrab_SaysWhichColoursItsFileHolds()
    {
        string path = Path.Combine(_dir, "tagged.mkv");
        using var session = new LibavGdigrabMkvSession { PreferHardwareEncoder = false, UseWallClockPts = true, UseDesktopDuplication = false };

        await RecordAsync(
            session.StartAsync(path, 0, 0, 320, 240, Fps, drawMouse: false, VideoCodec.H264),
            session.StopAsync,
            () => session.LastErrorMessage);

        Assert.Equal(AVColorSpace.AVCOL_SPC_BT470BG, ReadColourSpace(path));
    }

    // A start that fails at once must answer false. It used to rethrow the worker's error instead whenever the
    // startup gate saw the worker's faulted first-frame signal finish before the worker itself, about half the time
    // on CI. Each failure case repeats so that race would show.

    // It leaves no file: the encoder now opens before the capture, and a file holding only the header was taken for a
    // segment by the finalize.
    [Fact]
    public async Task Gdigrab_ARegionOffTheScreen_FailsToStart()
    {
        for (int attempt = 0; attempt < 20; attempt++)
        {
            using var session = new LibavGdigrabMkvSession { PreferHardwareEncoder = false };
            string path = Path.Combine(_dir, "offscreen.mkv");

            Assert.False(await session.StartAsync(path, 1_000_000, 1_000_000, 320, 240, Fps, drawMouse: false, VideoCodec.H264));
            Assert.False(string.IsNullOrEmpty(session.LastErrorMessage));
            Assert.False(File.Exists(path), "a failed start left a segment file");
        }
    }

    [Fact]
    public async Task Wgc_AWindowThatDoesNotExist_FailsToStart_WithoutTurningWgcOff()
    {
        for (int attempt = 0; attempt < 5; attempt++)
        {
            using var session = new LibavWgcMkvSession { PreferHardwareEncoder = false };

            Assert.False(await session.StartAsync(
                Path.Combine(_dir, "nowindow.mkv"), IntPtr.Zero, Fps, drawMouse: false, VideoCodec.H264));
            Assert.False(string.IsNullOrEmpty(session.LastErrorMessage));
            // A quick failure is not the "WGC never delivers a frame" wedge that turns WGC off for the session.
            Assert.False(session.TimedOutWaitingForFrame);
        }
    }

    [Fact]
    public async Task Wgc_AWindowThatDoesNotExist_IsNotTakenForAWedge_EvenWhenBringUpRunsLate()
    {
        // A bring-up that runs late is how a wedged WGC is recognized, and WGC is then turned off for the session. A
        // cold or busy machine can make any bring-up late (CI did, once), so a window that is gone has to fail before
        // it. With no time allowed for the bring-up at all, the start used to set the flag every time.
        using var session = new LibavWgcMkvSession { PreferHardwareEncoder = false, BringupTimeoutMs = 0 };

        Assert.False(await session.StartAsync(
            Path.Combine(_dir, "gone.mkv"), IntPtr.Zero, Fps, drawMouse: false, VideoCodec.H264));
        Assert.False(session.TimedOutWaitingForFrame);
    }

    [Fact]
    public async Task WgcComposite_NoWindowThatCanBeCaptured_FailsToStart()
    {
        for (int attempt = 0; attempt < 20; attempt++)
        {
            using var session = new LibavWgcCompositeMkvSession { PreferHardwareEncoder = false };

            Assert.False(await session.StartAsync(
                Path.Combine(_dir, "nowindows.mkv"), new[] { IntPtr.Zero }, Fps, drawMouse: false, VideoCodec.H264));
            Assert.False(string.IsNullOrEmpty(session.LastErrorMessage));
        }
    }

    [OptInFact(ProbeSwitch)]
    public async Task Wgc_RecordsTheWindow()
    {
        using var window = new TargetWindow(left: 100, width: 400, height: 300, Color.SteelBlue);
        string path = Path.Combine(_dir, "wgc.mkv");
        using var session = new LibavWgcMkvSession { PreferHardwareEncoder = false };

        await RecordAsync(
            session.StartAsync(path, window.Handle, Fps, drawMouse: false, VideoCodec.H264),
            session.StopAsync,
            () => session.LastErrorMessage);

        // WGC records the window as Windows draws it (its frame and display scaling included), made even.
        await AssertSegmentAsync(path, session.SelectedEncoderName, expectedSize: null);
    }

    [OptInFact(ProbeSwitch)]
    public async Task WgcComposite_RecordsBothWindowsOnOneCanvas()
    {
        using var left = new TargetWindow(left: 100, width: 400, height: 300, Color.SteelBlue);
        using var right = new TargetWindow(left: 600, width: 300, height: 200, Color.Goldenrod);
        string path = Path.Combine(_dir, "composite.mkv");
        using var session = new LibavWgcCompositeMkvSession { PreferHardwareEncoder = false };

        await RecordAsync(
            session.StartAsync(path, new[] { left.Handle, right.Handle }, Fps, drawMouse: false, VideoCodec.H264),
            session.StopAsync,
            () => session.LastErrorMessage);

        Assert.True(string.IsNullOrEmpty(session.LastWarningMessage), session.LastWarningMessage); // none skipped
        await AssertSegmentAsync(path, session.SelectedEncoderName, expectedSize: null);
    }

    /// <summary>
    /// A region recording on NVENC hands it the capture's BGRA: the colours come out as the CPU's conversion to 4:2:0
    /// gave them (BT.601), and the file says so, grabbed by gdigrab or by Desktop Duplication. Opt-in: it needs an
    /// NVIDIA GPU with NVENC. On a laptop whose display hangs off the other chip, the duplication also needs DXGI asked
    /// before NVENC starts CUDA (DisplayAdapterOrder): run alone, in a process of its own.
    /// </summary>
    [OptInFact("NVENC_PROBE=1")]
    public Task Gdigrab_OnNvenc_KeepsTheColours_AndSaysWhichTheyAre() => RecordsSteelBlueOnNvenc(duplicate: false, "gdigrab");

    [OptInFact("NVENC_PROBE=1")]
    public Task DesktopDuplication_OnNvenc_KeepsTheColours_AndSaysWhichTheyAre() => RecordsSteelBlueOnNvenc(duplicate: true, "ddagrab");

    /// <summary>
    /// Desktop Duplication leaves out a window that asks to be left out of captures (WDA_EXCLUDEFROMCAPTURE), as gdigrab
    /// does: that is how the capture overlay's frame and toolbar stay out of the region a recording takes. Opt-in
    /// (RECORDING_PROBE=1): it puts two windows on screen.
    /// </summary>
    [OptInFact(ProbeSwitch)]
    public async Task DesktopDuplication_LeavesOutAWindowExcludedFromCapture()
    {
        using var below = new TargetWindow(left: 200, width: 600, height: 400, Color.SteelBlue);
        using var above = new TargetWindow(left: 260, width: 480, height: 280, Color.Crimson, excludedFromCapture: true);
        string path = Path.Combine(_dir, "excluded.mkv");
        using var session = new LibavGdigrabMkvSession { PreferHardwareEncoder = false, UseWallClockPts = true };

        // Inside both windows: the crimson one is on top, on screen.
        await RecordAsync(
            session.StartAsync(path, 300, 250, 320, 160, Fps, drawMouse: false, VideoCodec.H264),
            session.StopAsync,
            () => session.LastErrorMessage);

        Assert.Equal("ddagrab", session.CaptureSourceName);
        AssertCentreIs(Color.SteelBlue, await LibavVideoFramePlayer.DecodeFrameAtAsync(path, 0.5, 320, 160), tolerance: 6);
    }

    private async Task RecordsSteelBlueOnNvenc(bool duplicate, string expectedSource)
    {
        using var window = new TargetWindow(left: 200, width: 600, height: 400, Color.SteelBlue);
        string path = Path.Combine(_dir, "nvenc.mkv");
        using var session = new LibavGdigrabMkvSession { PreferHardwareEncoder = true, UseWallClockPts = true, UseDesktopDuplication = duplicate };

        // Well inside the window, below its title bar.
        await RecordAsync(
            session.StartAsync(path, 300, 250, 320, 160, Fps, drawMouse: false, VideoCodec.H265),
            session.StopAsync,
            () => session.LastErrorMessage);

        Assert.Equal("hevc_nvenc", session.SelectedEncoderName);
        Assert.Equal(expectedSource, session.CaptureSourceName);
        Assert.Equal(AVColorSpace.AVCOL_SPC_BT470BG, ReadColourSpace(path));
        AssertCentreIs(Color.SteelBlue, await LibavVideoFramePlayer.DecodeFrameAtAsync(path, 0.5, 320, 160), tolerance: 3);
    }

    private static void AssertCentreIs(Color expected, byte[]? bgra, int tolerance)
    {
        Assert.NotNull(bgra);
        int centre = ((80 * 320) + 160) * 4;
        Assert.InRange(bgra[centre + 2], expected.R - tolerance, expected.R + tolerance);
        Assert.InRange(bgra[centre + 1], expected.G - tolerance, expected.G + tolerance);
        Assert.InRange(bgra[centre], expected.B - tolerance, expected.B + tolerance);
    }

    // Whether Desktop Duplication opens on the region here and now: not on a machine without a display adapter that
    // duplicates, such as a virtual one.
    private static bool CanDuplicate(int x, int y, int width, int height)
    {
        using var device = DxgiOutputs.TryCreateDuplicationDevice(x, y, width, height);
        using var source = device != null ? LibavDesktopDuplicationSource.TryOpen(device, x, y, width, height, Fps, drawMouse: false) : null;
        return source != null;
    }

    private static unsafe AVColorSpace ReadColourSpace(string path)
    {
        AVFormatContext* format = null;
        Assert.True(ffmpeg.avformat_open_input(&format, path, null, null) >= 0);
        try
        {
            Assert.True(ffmpeg.avformat_find_stream_info(format, null) >= 0);
            int video = ffmpeg.av_find_best_stream(format, AVMediaType.AVMEDIA_TYPE_VIDEO, -1, -1, null, 0);
            Assert.True(video >= 0);
            return format->streams[video]->codecpar->color_space;
        }
        finally
        {
            ffmpeg.avformat_close_input(&format);
        }
    }

    private static async Task RecordAsync(Task<bool> start, Func<Task> stop, Func<string?> lastError)
    {
        Assert.True(await start, $"did not start: {lastError()}");
        await Task.Delay(Length);
        await stop();
        Assert.True(lastError() is null, $"the recording failed: {lastError()}");
    }

    private static async Task AssertSegmentAsync(string path, string? encoder, (int Width, int Height)? expectedSize)
    {
        Assert.True(File.Exists(path) && new FileInfo(path).Length > 0, "no segment was written");
        Assert.Equal("libx264", encoder); // PreferHardwareEncoder = false

        using var player = new LibavVideoFramePlayer();
        var size = await player.ProbeVideoSizeAsync(path);
        Assert.NotNull(size);
        var (width, height) = size.Value;
        if (expectedSize is { } expected)
        {
            Assert.Equal(expected, (width, height));
        }

        Assert.True(width > 0 && height > 0 && width % 2 == 0 && height % 2 == 0, $"frame size {width}x{height}");

        // About as long as it was recorded, give or take the start-up and the last frames.
        Assert.InRange(await player.ProbeDurationSecondsAsync(path) ?? 0, 0.5, 4.0);
        Assert.Equal(Fps, LibavClipExporter.ProbeFps(path));
        Assert.NotNull(await LibavVideoFramePlayer.DecodeFrameAtAsync(path, 0.2, width, height));
    }

    /// <summary>A plain coloured window on a UI thread of its own, for WGC to record.</summary>
    private sealed class TargetWindow : IDisposable
    {
        private readonly Thread _thread;
        private Form? _form;

        public TargetWindow(int left, int width, int height, Color color, bool excludedFromCapture = false)
        {
            // Not disposed: after a timeout the window's thread could still set it.
            var shown = new ManualResetEventSlim();
            _thread = new Thread(() =>
            {
                _form = new QuietForm
                {
                    StartPosition = FormStartPosition.Manual,
                    Location = new Point(left, 100),
                    Size = new Size(width, height),
                    BackColor = color,
                    Text = "GimmeCapture recording test",
                    ShowInTaskbar = false,
                    TopMost = true,
                };
                _form.Shown += (_, _) =>
                {
                    Handle = _form.Handle;
                    if (excludedFromCapture)
                    {
                        Win32Helpers.SetWindowCaptureVisibility(Handle, visible: false);
                    }

                    shown.Set();
                };
                Application.Run(_form);
            })
            { IsBackground = true };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
            if (!shown.Wait(TimeSpan.FromSeconds(10)))
            {
                throw new TimeoutException("The test window did not appear.");
            }
        }

        public IntPtr Handle { get; private set; }

        public void Dispose()
        {
            _form?.Invoke(() => _form.Close());
            _thread.Join(TimeSpan.FromSeconds(5));
        }

        // Appears without taking the keyboard from whatever the person running the tests is typing into.
        private sealed class QuietForm : Form
        {
            protected override bool ShowWithoutActivation => true;
        }
    }
}
