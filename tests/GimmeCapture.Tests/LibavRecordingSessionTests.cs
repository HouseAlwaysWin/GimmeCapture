using System.Drawing;
using System.Threading;
using System.Windows.Forms;
using GimmeCapture.Models;
using GimmeCapture.Services.Core.Media.NativeFFmpeg;

namespace GimmeCapture.Tests;

/// <summary>
/// Records about a second and a half with each native recorder session and checks the Matroska segment it leaves: the
/// encoder it picked, the frame size, a duration close to the time recorded, the frame rate, and a frame that decodes.
/// They pin what a recording produces, so the encode-and-mux code the three sessions share can be moved under them.
/// The gdigrab cases capture a corner of the desktop without showing anything and run with the other Windows tests;
/// the WGC ones put small windows on screen to record, so they are opt-in (RECORDING_PROBE=1).
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
        };

        await RecordAsync(
            session.StartAsync(path, 0, 0, 320, 240, Fps, drawMouse: true, VideoCodec.H264),
            session.StopAsync,
            () => session.LastErrorMessage);

        await AssertSegmentAsync(path, session.SelectedEncoderName, expectedSize: (320, 240));
    }

    // A start that fails at once must answer false. It used to rethrow the worker's error instead whenever the
    // startup gate saw the worker's faulted first-frame signal finish before the worker itself, about half the time
    // on CI. Each failure case repeats so that race would show.

    [Fact]
    public async Task Gdigrab_ARegionOffTheScreen_FailsToStart()
    {
        for (int attempt = 0; attempt < 20; attempt++)
        {
            using var session = new LibavGdigrabMkvSession { PreferHardwareEncoder = false };

            Assert.False(await session.StartAsync(
                Path.Combine(_dir, "offscreen.mkv"), 1_000_000, 1_000_000, 320, 240, Fps, drawMouse: false, VideoCodec.H264));
            Assert.False(string.IsNullOrEmpty(session.LastErrorMessage));
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

        public TargetWindow(int left, int width, int height, Color color)
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
