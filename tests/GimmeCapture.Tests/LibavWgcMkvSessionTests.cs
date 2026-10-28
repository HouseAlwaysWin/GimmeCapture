#if WINDOWS // records with Windows Graphics Capture and shows WinForms windows to record
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Windows.Forms;
using FFmpeg.AutoGen;
using GimmeCapture.Models;
using GimmeCapture.Services.Core.Media.NativeFFmpeg;
using Xunit.Abstractions;

namespace GimmeCapture.Tests;

/// <summary>
/// Records a window of the test's own through the session the app uses for single-window recording, and checks the
/// frames Windows Graphics Capture delivered, what each cost in managed memory and the file that came out: a window that
/// keeps changing, one that does not, one resized and one closed while recorded. Every frame of the file is decoded to
/// see the window's colours in it. They put windows on screen, so they are opt-in (RECORDING_PROBE=1); each prints what
/// it measured. They run on their own, since they count the whole process's allocations.
/// </summary>
[Collection(nameof(LibavWgcMkvSessionTests))]
public sealed class LibavWgcMkvSessionTests : IDisposable
{
    private const int Fps = 30;
    private const string ProbeSwitch = "RECORDING_PROBE=1";
    private static readonly TimeSpan Settle = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan Warmup = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan Steady = TimeSpan.FromSeconds(4);
    private static readonly Color[] Palette = { Color.Red, Color.Lime, Color.Blue, Color.Yellow, Color.Magenta, Color.Cyan };

    private readonly ITestOutputHelper _output;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "GimmeCapture.Tests", Guid.NewGuid().ToString("N"));

    public LibavWgcMkvSessionTests(ITestOutputHelper output)
    {
        _output = output;
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    [OptInFact(ProbeSwitch)]
    public async Task AChangingWindow_IsRecordedFrameByFrame_WithoutAFrameSizedAllocationEach()
    {
        using var window = new ColourWindow(800, 450, animate: true);
        await OwnThread.Delay(Settle);
        Usage windowAlone = await MeasureAsync(session: null);

        string path = Path.Combine(_dir, "changing.mkv");
        using var session = new LibavWgcMkvSession { PreferHardwareEncoder = false };
        var recorded = Stopwatch.StartNew();
        Assert.True(await session.StartAsync(path, window.Handle, Fps, drawMouse: false, VideoCodec.H264), $"did not start: {session.LastErrorMessage}");
        await OwnThread.Delay(Warmup);
        Usage recording = await MeasureAsync(session);
        await session.StopAsync();
        recorded.Stop();
        Assert.True(session.LastErrorMessage is null, $"the recording failed: {session.LastErrorMessage}");

        Video video = await ReadAsync(path);
        long bytesPerFrame = Math.Max(0, recording.Bytes - windowAlone.Bytes) / Math.Max(1, recording.ReadBack);
        Report(video, session, windowAlone, recording, bytesPerFrame);

        // Frames came in all along, and each new one was converted at most once.
        Assert.True(recording.ReadBack >= 20, $"only {recording.ReadBack} frames read back in {Steady.TotalSeconds} s");
        Assert.InRange(session.FramesConverted, 20, session.FramesReadBack);

        // A frame of this window is about 2 MB. Every one used to be a new array of that size and a new SoftwareBitmap,
        // whose GC memory pressure set off a gen2 collection every three frames.
        Assert.True(bytesPerFrame < 64 * 1024, $"{bytesPerFrame:N0} bytes allocated per frame read back");
        Assert.True(recording.Gen2 <= 1, $"{recording.Gen2} gen2 collections in {Steady.TotalSeconds} s");

        AssertLength(video, recorded.Elapsed);
        Assert.True(video.Colours.Distinct().Count() >= 3, $"colours recorded: {string.Join(", ", video.Colours.Distinct())}");
    }

    [OptInFact(ProbeSwitch)]
    public async Task AStillWindow_IsNotConvertedAgainForEveryFrame()
    {
        using var window = new ColourWindow(800, 450, animate: false);
        await OwnThread.Delay(Settle);
        Usage windowAlone = await MeasureAsync(session: null);

        string path = Path.Combine(_dir, "still.mkv");
        using var session = new LibavWgcMkvSession { PreferHardwareEncoder = false };
        var recorded = Stopwatch.StartNew();
        Assert.True(await session.StartAsync(path, window.Handle, Fps, drawMouse: false, VideoCodec.H264), $"did not start: {session.LastErrorMessage}");
        await OwnThread.Delay(Warmup);
        Usage recording = await MeasureAsync(session);
        await session.StopAsync();
        recorded.Stop();
        Assert.True(session.LastErrorMessage is null, $"the recording failed: {session.LastErrorMessage}");

        Video video = await ReadAsync(path);
        long bytesPerFrame = Math.Max(0, recording.Bytes - windowAlone.Bytes) / (long)(Fps * Steady.TotalSeconds);
        Report(video, session, windowAlone, recording, bytesPerFrame);

        // Windows delivers a window that does not change a few times at most, and each was converted once. Every other
        // frame repeats the picture already in the encoder's frame: it used to be copied twice and converted again.
        Assert.InRange(session.FramesConverted, 1, session.FramesReadBack);
        Assert.True(session.FramesConverted * 5 < video.Colours.Count, $"{session.FramesConverted} of {video.Colours.Count} frames converted");
        Assert.True(bytesPerFrame < 64, $"{bytesPerFrame:N0} bytes allocated per frame encoded");

        AssertLength(video, recorded.Elapsed);
        Assert.All(video.Colours, colour => Assert.Equal(Palette[0], colour));
    }

    [OptInFact(ProbeSwitch)]
    public async Task AResizedWindow_KeepsBeingRecorded_AtTheSizeItStartedAt()
    {
        using var window = new ColourWindow(800, 450, animate: false);
        await OwnThread.Delay(Settle);

        string path = Path.Combine(_dir, "resized.mkv");
        using var session = new LibavWgcMkvSession { PreferHardwareEncoder = false };
        var recorded = Stopwatch.StartNew();
        Assert.True(await session.StartAsync(path, window.Handle, Fps, drawMouse: false, VideoCodec.H264), $"did not start: {session.LastErrorMessage}");
        await OwnThread.Delay(TimeSpan.FromSeconds(1));
        window.Change(form => { form.BackColor = Palette[2]; form.Size = new Size(600, 340); });
        await OwnThread.Delay(TimeSpan.FromSeconds(1.2));
        long convertedSmaller = session.FramesConverted;
        window.Change(form => { form.BackColor = Palette[1]; form.Size = new Size(900, 500); });
        await OwnThread.Delay(TimeSpan.FromSeconds(1.2));
        await session.StopAsync();
        recorded.Stop();
        Assert.True(session.LastErrorMessage is null, $"the recording failed: {session.LastErrorMessage}");

        Video video = await ReadAsync(path);
        Report(video, session);

        // Frames of the new sizes were converted into the encoder's fixed size, in the order the window showed them.
        Assert.True(session.FramesConverted > convertedSmaller, "nothing converted after the window grew");
        AssertLength(video, recorded.Elapsed);
        Assert.Equal(Palette[0], video.Colours[0]);
        Assert.Equal(Palette[1], video.Colours[^1]);
        int smaller = video.Colours.IndexOf(Palette[2]);
        Assert.True(smaller > 0 && smaller < video.Colours.IndexOf(Palette[1]), $"colours recorded: {string.Join(", ", video.Colours.Distinct())}");
    }

    [OptInFact(ProbeSwitch)]
    public async Task AWindowThatCloses_KeepsItsLastPicture_UntilTheRecordingStops()
    {
        using var window = new ColourWindow(800, 450, animate: false);
        await OwnThread.Delay(Settle);

        string path = Path.Combine(_dir, "closed.mkv");
        using var session = new LibavWgcMkvSession { PreferHardwareEncoder = false };
        var recorded = Stopwatch.StartNew();
        Assert.True(await session.StartAsync(path, window.Handle, Fps, drawMouse: false, VideoCodec.H264), $"did not start: {session.LastErrorMessage}");
        await OwnThread.Delay(TimeSpan.FromSeconds(1));
        window.Change(form => form.BackColor = Palette[5]);
        await OwnThread.Delay(TimeSpan.FromSeconds(0.6));
        long readBackBeforeClose = session.FramesReadBack;
        window.Dispose(); // closes it
        await OwnThread.Delay(TimeSpan.FromSeconds(1.5));
        await session.StopAsync();
        recorded.Stop();
        Assert.True(session.LastErrorMessage is null, $"the recording failed: {session.LastErrorMessage}");

        Video video = await ReadAsync(path);
        Report(video, session);
        _output.WriteLine($"read back before the window closed: {readBackBeforeClose}");

        // The recording went on to the stop at its frame rate, showing the window as it last was.
        AssertLength(video, recorded.Elapsed);
        Assert.Equal(Palette[5], video.Colours[^1]);
    }

    private void Report(Video video, LibavWgcMkvSession session, Usage? windowAlone = null, Usage? recording = null, long? bytesPerFrame = null)
    {
        _output.WriteLine($"file: {video.Width}x{video.Height} @ {video.Fps} fps, {video.Duration:F2} s, {video.Colours.Count} frames decoded");
        _output.WriteLine($"session: read back {session.FramesReadBack}, converted {session.FramesConverted}");
        if (windowAlone is { } alone && recording is { } rec)
        {
            _output.WriteLine($"window alone, {Steady.TotalSeconds} s: {alone}");
            _output.WriteLine($"recording, {Steady.TotalSeconds} s: {rec}");
            _output.WriteLine($"bytes per frame (net of the window alone): {bytesPerFrame:N0}");
        }
    }

    private static void AssertLength(Video video, TimeSpan recorded)
    {
        Assert.Equal(Fps, video.Fps);
        Assert.True(video.Width > 0 && video.Height > 0 && video.Width % 2 == 0 && video.Height % 2 == 0, $"frame size {video.Width}x{video.Height}");

        // As long as it was recorded, less the start-up before the first frame, and one frame for every 1/fps of it.
        Assert.InRange(video.Duration, recorded.TotalSeconds - 1.5, recorded.TotalSeconds + 0.5);
        Assert.InRange(video.Colours.Count, (int)((video.Duration - 0.2) * Fps), (int)((video.Duration + 0.2) * Fps));
    }

    private static async Task<Usage> MeasureAsync(LibavWgcMkvSession? session)
    {
        Usage start = Usage.Now(session);
        await OwnThread.Delay(Steady);
        return Usage.Now(session) - start;
    }

    private static async Task<Video> ReadAsync(string path)
    {
        Assert.True(File.Exists(path) && new FileInfo(path).Length > 0, "no file was written");
        using var player = new LibavVideoFramePlayer();
        var info = await player.ProbeAsync(path);
        return new Video(info.Width, info.Height, info.Fps, info.Duration, DecodeCentreColours(path));
    }

    /// <summary>Decodes every frame and names the palette colour at its centre, which is inside the window's client area.</summary>
    private static unsafe List<Color> DecodeCentreColours(string path)
    {
        FFmpegRuntime.EnsureInitialized();
        var colours = new List<Color>();
        AVFormatContext* format = null;
        AVCodecContext* decoder = null;
        AVPacket* packet = ffmpeg.av_packet_alloc();
        AVFrame* frame = ffmpeg.av_frame_alloc();
        SwsContext* toBgra = null;
        byte[]? bgra = null;
        try
        {
            Assert.True(ffmpeg.avformat_open_input(&format, path, null, null) >= 0, "open_input");
            Assert.True(ffmpeg.avformat_find_stream_info(format, null) >= 0, "find_stream_info");
            AVCodec* codec = null;
            int video = ffmpeg.av_find_best_stream(format, AVMediaType.AVMEDIA_TYPE_VIDEO, -1, -1, &codec, 0);
            Assert.True(video >= 0 && codec != null, "no video stream");
            decoder = ffmpeg.avcodec_alloc_context3(codec);
            Assert.True(ffmpeg.avcodec_parameters_to_context(decoder, format->streams[video]->codecpar) >= 0, "parameters_to_context");
            Assert.True(ffmpeg.avcodec_open2(decoder, codec, null) >= 0, "open decoder");

            bool draining = false;
            while (!draining)
            {
                if (ffmpeg.av_read_frame(format, packet) < 0)
                {
                    draining = true;
                    ffmpeg.avcodec_send_packet(decoder, null);
                }
                else
                {
                    if (packet->stream_index == video)
                    {
                        Assert.True(ffmpeg.avcodec_send_packet(decoder, packet) >= 0, "send_packet");
                    }

                    ffmpeg.av_packet_unref(packet);
                }

                while (ffmpeg.avcodec_receive_frame(decoder, frame) >= 0)
                {
                    int width = frame->width;
                    int height = frame->height;
                    if (toBgra == null)
                    {
                        toBgra = ffmpeg.sws_getContext(
                            width, height, (AVPixelFormat)frame->format, width, height, AVPixelFormat.AV_PIX_FMT_BGRA,
                            (int)SwsFlags.SWS_POINT, null, null, null);
                        bgra = new byte[width * height * 4];
                    }

                    fixed (byte* pixels = bgra)
                    {
                        ffmpeg.sws_scale(toBgra, frame->data, frame->linesize, 0, height, new byte*[] { pixels, null, null, null }, new[] { width * 4, 0, 0, 0 });
                    }

                    int at = ((height / 2 * width) + (width / 2)) * 4;
                    colours.Add(Nearest(Color.FromArgb(bgra![at + 2], bgra[at + 1], bgra[at])));
                    ffmpeg.av_frame_unref(frame);
                }
            }
        }
        finally
        {
            if (toBgra != null)
            {
                ffmpeg.sws_freeContext(toBgra);
            }

            ffmpeg.av_frame_free(&frame);
            ffmpeg.av_packet_free(&packet);
            ffmpeg.avcodec_free_context(&decoder);
            if (format != null)
            {
                ffmpeg.avformat_close_input(&format);
            }
        }

        return colours;
    }

    // The palette colour within reach of what was decoded, or black for anything else (a blank or garbled frame).
    private static Color Nearest(Color decoded)
    {
        static int Distance(Color a, Color b) =>
            ((a.R - b.R) * (a.R - b.R)) + ((a.G - b.G) * (a.G - b.G)) + ((a.B - b.B) * (a.B - b.B));

        Color nearest = Palette.MinBy(colour => Distance(colour, decoded));
        return Distance(nearest, decoded) < 60 * 60 ? nearest : Color.Black;
    }

    private sealed record Video(int Width, int Height, int Fps, double Duration, List<Color> Colours);

    private readonly record struct Usage(long Bytes, int Gen0, int Gen1, int Gen2, double CpuMs, long ReadBack, long Converted)
    {
        public static Usage Now(LibavWgcMkvSession? session) => new(
            GC.GetTotalAllocatedBytes(precise: true),
            GC.CollectionCount(0),
            GC.CollectionCount(1),
            GC.CollectionCount(2),
            Process.GetCurrentProcess().TotalProcessorTime.TotalMilliseconds,
            session?.FramesReadBack ?? 0,
            session?.FramesConverted ?? 0);

        public static Usage operator -(Usage end, Usage start) => new(
            end.Bytes - start.Bytes,
            end.Gen0 - start.Gen0,
            end.Gen1 - start.Gen1,
            end.Gen2 - start.Gen2,
            end.CpuMs - start.CpuMs,
            end.ReadBack - start.ReadBack,
            end.Converted - start.Converted);

        public override string ToString() =>
            $"allocated {Bytes:N0} B, gen0/1/2 {Gen0}/{Gen1}/{Gen2}, cpu {CpuMs:F0} ms, read back {ReadBack}, converted {Converted}";
    }

    /// <summary>A coloured window on a UI thread of its own, for WGC to record; it can step through the palette.</summary>
    private sealed class ColourWindow : IDisposable
    {
        private readonly Thread _thread;
        private Form? _form;

        public ColourWindow(int width, int height, bool animate)
        {
            // Not disposed: after a timeout the window's thread could still set it.
            var shown = new ManualResetEventSlim();
            _thread = new Thread(() =>
            {
                _form = new QuietForm
                {
                    StartPosition = FormStartPosition.Manual,
                    Location = new Point(100, 100),
                    Size = new Size(width, height),
                    BackColor = Palette[0],
                    Text = "GimmeCapture WGC recording test",
                    ShowInTaskbar = false,
                    TopMost = true,
                };
                if (animate)
                {
                    int step = 0;
                    var timer = new System.Windows.Forms.Timer { Interval = 15 };
                    timer.Tick += (_, _) => _form.BackColor = Palette[++step % Palette.Length];
                    _form.Shown += (_, _) => timer.Start();
                    _form.FormClosed += (_, _) => timer.Dispose();
                }

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

        public void Change(Action<Form> change)
        {
            Form form = _form!;
            form.Invoke(() => change(form));
        }

        public void Dispose()
        {
            if (_thread.IsAlive)
            {
                _form?.Invoke(() => _form.Close());
                _thread.Join(TimeSpan.FromSeconds(5));
            }
        }

        // Appears without taking the keyboard from whatever the person running the tests is typing into.
        private sealed class QuietForm : Form
        {
            protected override bool ShowWithoutActivation => true;
        }
    }
}

[CollectionDefinition(nameof(LibavWgcMkvSessionTests), DisableParallelization = true)]
public sealed class LibavWgcMkvSessionTestsCollection
{
}
#endif
