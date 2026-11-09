using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GimmeCapture.Services.Core.Media.NativeFFmpeg;
using Xunit.Abstractions;

namespace GimmeCapture.Tests;

/// <summary>
/// The frames the compress editor and its compare show while paused, from one decoder that keeps the file open, against
/// <see cref="LibavVideoFramePlayer.DecodeFrameAtAsync"/>, which opens it for each frame. The clips are flat greys, one
/// shade per frame, so a frame's pixels say which frame it is. Needs only the bundled FFmpeg.
/// </summary>
public sealed class LibavStillFrameDecoderTests : IDisposable
{
    private const int Width = 64;
    private const int Height = 48;
    private const int Fps = 30;

    // What one step of the clips' grey (7 levels of luma) comes to in BGRA.
    private const double GreyStep = 7 * 255.0 / 219;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "GimmeCapture.Tests", Guid.NewGuid().ToString("N"));
    private readonly ITestOutputHelper _output;

    public LibavStillFrameDecoderTests(ITestOutputHelper output)
    {
        _output = output;
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // A test that failed may have left a decoder holding a clip.
        }
    }

    // Steps both ways, a frame asked for twice, jumps across keyframes, a time between frames, past the end and back.
    private static readonly double[] Times =
    [
        0, 1.0 / Fps, 2.0 / Fps, 3.0 / Fps, 2.0 / Fps, 2.0 / Fps, 0.5, 16.0 / Fps, 14.0 / Fps, 0.41, 29.0 / Fps,
        1.5, 6.0 / Fps, 25.0 / Fps, 0,
    ];

    [Theory]
    [InlineData("clip.mp4")] // a keyframe every ten frames, which the file's index lists
    [InlineData("clip.y4m")] // raw frames: every frame a keyframe
    public async Task EachFrame_IsTheOne_OpeningTheFileForItGives(string name)
    {
        string clip = name.EndsWith(".y4m", StringComparison.Ordinal)
            ? VideoTestClips.WriteY4m(Path.Combine(_dir, name), Width, Height, 30, Fps, 1)
            : VideoTestClips.WriteConstantRate(Path.Combine(_dir, name), Width, Height, 30, Fps, gopSize: 10);
        using var decoder = new LibavStillFrameDecoder(clip, Width, Height);

        foreach (double seconds in Times)
        {
            byte[]? kept = await decoder.DecodeFrameAtAsync(seconds);
            byte[]? opened = await LibavVideoFramePlayer.DecodeFrameAtAsync(clip, seconds, Width, Height);

            Assert.Equal(FrameIndexOf(opened), FrameIndexOf(kept));
            Assert.Equal(ExpectedFrame(seconds, frameCount: 30), FrameIndexOf(kept));
        }

        Assert.Equal(1, decoder.Opens);
    }

    [Fact]
    public async Task AStepForward_DecodesOn_AndAJumpPastAKeyframe_Seeks()
    {
        string clip = VideoTestClips.WriteConstantRate(Path.Combine(_dir, "clip.mp4"), Width, Height, 30, Fps, gopSize: 10);
        using var decoder = new LibavStillFrameDecoder(clip, Width, Height);

        Assert.Equal(3, FrameIndexOf(await decoder.DecodeFrameAtAsync(3.0 / Fps)));
        int seeksAtFirstFrame = decoder.Seeks;
        for (int frame = 4; frame <= 9; frame++)
        {
            Assert.Equal(frame, FrameIndexOf(await decoder.DecodeFrameAtAsync((double)frame / Fps)));
        }

        Assert.Equal(seeksAtFirstFrame, decoder.Seeks);

        // Frame 12 is after the keyframe at 10, where a seek starts decoding: nearer than decoding on from 9.
        Assert.Equal(12, FrameIndexOf(await decoder.DecodeFrameAtAsync(12.0 / Fps)));
        Assert.Equal(seeksAtFirstFrame + 1, decoder.Seeks);

        // A step back can only seek.
        Assert.Equal(11, FrameIndexOf(await decoder.DecodeFrameAtAsync(11.0 / Fps)));
        Assert.Equal(seeksAtFirstFrame + 2, decoder.Seeks);
    }

    [Fact]
    public async Task TheFrameLastGiven_AskedForAgain_ComesBackWithoutADecode()
    {
        string clip = VideoTestClips.WriteConstantRate(Path.Combine(_dir, "clip.mp4"), Width, Height, 30, Fps, gopSize: 10);
        using var decoder = new LibavStillFrameDecoder(clip, Width, Height);

        // 0.41 s is between frames 12 (0.400) and 13 (0.433): frame 13 is the first at or after it.
        byte[]? first = await decoder.DecodeFrameAtAsync(0.41);
        int seeks = decoder.Seeks;
        byte[]? again = await decoder.DecodeFrameAtAsync(0.41);
        byte[]? later = await decoder.DecodeFrameAtAsync(0.43);

        Assert.Equal(13, FrameIndexOf(first));
        Assert.Same(first, again);
        Assert.Same(first, later);
        Assert.Equal(seeks, decoder.Seeks);

        // Before 0.41 there may be a frame that came earlier (here frame 12), so that one decodes.
        Assert.Equal(12, FrameIndexOf(await decoder.DecodeFrameAtAsync(0.40)));
    }

    [Fact]
    public async Task KeyframeWillDo_ASeekShowsTheKeyframeItLandsOn_AndTheExactFrameStillComes()
    {
        string clip = VideoTestClips.WriteConstantRate(Path.Combine(_dir, "clip.mp4"), Width, Height, 30, Fps, gopSize: 10);
        using var decoder = new LibavStillFrameDecoder(clip, Width, Height);
        Assert.Equal(5, FrameIndexOf(await decoder.DecodeFrameAtAsync(5.0 / Fps)));
        int seeks = decoder.Seeks;

        // Frame 25 needs a seek, which lands on the keyframe at 20: what a drag shows, with no decoding on to 25.
        Assert.Equal(20, FrameIndexOf(await decoder.DecodeFrameAtAsync(25.0 / Fps, default, keyframeWillDo: true)));
        Assert.Equal(seeks + 1, decoder.Seeks);

        // The exact frame when the drag ends: decoded on from that keyframe, and not the keyframe given back as frame 25.
        Assert.Equal(25, FrameIndexOf(await decoder.DecodeFrameAtAsync(25.0 / Fps)));
        Assert.Equal(seeks + 1, decoder.Seeks);

        // A frame reached by decoding on is the exact one, drag or not.
        Assert.Equal(27, FrameIndexOf(await decoder.DecodeFrameAtAsync(27.0 / Fps, default, keyframeWillDo: true)));
        Assert.Equal(seeks + 1, decoder.Seeks);

        // Back before the decoder: a seek again, to the keyframe at 10 for frame 13.
        Assert.Equal(10, FrameIndexOf(await decoder.DecodeFrameAtAsync(13.0 / Fps, default, keyframeWillDo: true)));
        Assert.Equal(seeks + 2, decoder.Seeks);
    }

    [Fact]
    public async Task PastTheLastFrame_ThereIsNone_AndAnEarlierFrameStillComes()
    {
        string clip = VideoTestClips.WriteConstantRate(Path.Combine(_dir, "clip.mp4"), Width, Height, 30, Fps, gopSize: 10);
        using var decoder = new LibavStillFrameDecoder(clip, Width, Height);

        Assert.Equal(29, FrameIndexOf(await decoder.DecodeFrameAtAsync(29.0 / Fps)));
        Assert.Null(await decoder.DecodeFrameAtAsync(30.0 / Fps));
        Assert.Null(await decoder.DecodeFrameAtAsync(5));
        Assert.Equal(20, FrameIndexOf(await decoder.DecodeFrameAtAsync(20.0 / Fps)));
        Assert.Equal(1, decoder.Opens);
    }

    [Fact]
    public async Task ACancelledRequest_GivesNothing_AndTheNextOneDecodes()
    {
        string clip = VideoTestClips.WriteConstantRate(Path.Combine(_dir, "clip.mp4"), Width, Height, 30, Fps, gopSize: 10);
        using var decoder = new LibavStillFrameDecoder(clip, Width, Height);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        Assert.Null(await decoder.DecodeFrameAtAsync(0.5, cancelled.Token));
        Assert.Equal(15, FrameIndexOf(await decoder.DecodeFrameAtAsync(0.5)));
    }

    [Fact]
    public async Task Idle_TheFileCloses_KeepingTheFrameLastGiven_AndTheNextFrameOpensItAgain()
    {
        string clip = VideoTestClips.WriteConstantRate(Path.Combine(_dir, "clip.mp4"), Width, Height, 30, Fps, gopSize: 10);
        using var decoder = new LibavStillFrameDecoder(clip, Width, Height, idleCloseDelay: TimeSpan.FromMilliseconds(50));
        byte[]? shown = await decoder.DecodeFrameAtAsync(0.5);
        Assert.True(decoder.IsOpen);

        // The close runs on the pool, which the rest of the suite keeps busy: give it time.
        var waited = Stopwatch.StartNew();
        while (decoder.IsOpen && waited.Elapsed < TimeSpan.FromSeconds(10))
        {
            await OwnThread.Delay(TimeSpan.FromMilliseconds(20));
        }

        Assert.False(decoder.IsOpen);
        Assert.Same(shown, await decoder.DecodeFrameAtAsync(0.5));
        Assert.False(decoder.IsOpen);
        Assert.Equal(16, FrameIndexOf(await decoder.DecodeFrameAtAsync(16.0 / Fps)));
        Assert.Equal(2, decoder.Opens);
    }

    [Fact]
    public async Task AfterDispose_ThereAreNoFrames()
    {
        string clip = VideoTestClips.WriteConstantRate(Path.Combine(_dir, "clip.mp4"), Width, Height, 30, Fps, gopSize: 10);
        var decoder = new LibavStillFrameDecoder(clip, Width, Height);
        Assert.NotNull(await decoder.DecodeFrameAtAsync(0));

        decoder.Dispose();

        Assert.Null(await decoder.DecodeFrameAtAsync(0.5));
    }

    // Windows refuses to delete a file that is open, as libav opens it; elsewhere a delete always works.
    [WindowsFact]
    public async Task Dispose_LetsTheFileGo()
    {
        string clip = VideoTestClips.WriteConstantRate(Path.Combine(_dir, "clip.mp4"), Width, Height, 30, Fps, gopSize: 10);
        var decoder = new LibavStillFrameDecoder(clip, Width, Height);
        Assert.NotNull(await decoder.DecodeFrameAtAsync(0.5));
        Assert.Throws<IOException>(() => File.Delete(clip));

        decoder.Dispose();

        File.Delete(clip);
    }

    [WindowsFact]
    public async Task Dispose_WhileAFrameDecodes_LetsTheFileGoWhenItIsDone()
    {
        // Long enough that a frame near its end takes the decoder a while to reach.
        string clip = VideoTestClips.WriteConstantRate(
            Path.Combine(_dir, "long.mp4"), 640, 360, 600, Fps, detailed: true, gopSize: 600);
        var decoder = new LibavStillFrameDecoder(clip, 640, 360);
        Task<byte[]?> decoding = decoder.DecodeFrameAtAsync(598.0 / Fps);

        decoder.Dispose();
        await decoding;

        File.Delete(clip);
    }

    /// <summary>
    /// What a paused editor's clicks cost, one decoder kept against a file opened for each frame: the first frame, steps
    /// forward and back, the frame again (a rotation), 5 s skips and slider drops. Opt-in: STILL_FRAME_PROBE=1, with
    /// STILL_FRAME_PROBE_FILE for a real video (otherwise a generated 1920x1080 60 fps clip, a keyframe every 2 s as
    /// the recorder writes); STILL_FRAME_PROBE_SOFTWARE=1 decodes as with hardware decoding turned off in the settings.
    /// </summary>
    [OptInFact("STILL_FRAME_PROBE=1")]
    public async Task APausedEditorsClicks_Timed()
    {
        bool hardware = LibavVideoFramePlayer.HardwareDecodeEnabled;
        LibavVideoFramePlayer.HardwareDecodeEnabled = Environment.GetEnvironmentVariable("STILL_FRAME_PROBE_SOFTWARE") != "1";
        try
        {
            await TimeClicksAsync();
        }
        finally
        {
            LibavVideoFramePlayer.HardwareDecodeEnabled = hardware;
        }
    }

    private async Task TimeClicksAsync()
    {
        string clip = Environment.GetEnvironmentVariable("STILL_FRAME_PROBE_FILE") is { Length: > 0 } file
            ? file
            : VideoTestClips.WriteConstantRate(Path.Combine(_dir, "probe.mp4"), 1920, 1080, 600, 60, detailed: true, gopSize: 120);
        using var player = new LibavVideoFramePlayer();
        LibavVideoFramePlayer.MediaInfo info = await player.ProbeAsync(clip);
        double step = 1.0 / Math.Max(1, info.Fps);
        double scale = Math.Min(1.0, 720.0 / Math.Max(1, info.Width));
        int width = Math.Max(2, (int)(info.Width * scale)) & ~1;
        int height = Math.Max(2, (int)(info.Height * scale)) & ~1;
        _output.WriteLine($"{Path.GetFileName(clip)}: {info.Width}x{info.Height} {info.Fps} fps, {info.Duration:F1} s, decoded at {width}x{height}");

        // Each click as the editor makes it: the action and the time it asks for.
        var clicks = new List<(string Action, double Seconds)> { ("first frame", info.Duration * 0.1) };
        double position = info.Duration * 0.1;
        for (int i = 0; i < 10; i++)
        {
            clicks.Add(("step forward", position += step));
        }

        for (int i = 0; i < 10; i++)
        {
            clicks.Add(("step back", position -= step));
        }

        clicks.Add(("same frame", position));
        for (int i = 0; i < 3; i++)
        {
            clicks.Add(("skip 5 s", position = Math.Min(info.Duration - 0.5, position + 5)));
        }

        foreach (double fraction in new[] { 0.25, 0.5, 0.75 })
        {
            clicks.Add(("slider drop", info.Duration * fraction));
        }

        // What the open decoder holds: the process's private bytes with it open, against before and after.
        long before = PrivateMegabytes();
        using (var held = new LibavStillFrameDecoder(clip, width, height))
        {
            await held.DecodeFrameAtAsync(clicks[0].Seconds);
            long open = PrivateMegabytes();
            held.Dispose();
            _output.WriteLine($"private bytes: {before} MB, {open} MB with the decoder open, {PrivateMegabytes()} MB after");
        }

        // Each click both ways in turn, so neither has a cooler processor or a warmer disk cache to itself.
        var opened = new Dictionary<string, List<double>>();
        var kept = new Dictionary<string, List<double>>();
        using (var decoder = new LibavStillFrameDecoder(clip, width, height))
        {
            foreach ((string action, double seconds) in clicks)
            {
                var watch = Stopwatch.StartNew();
                await LibavVideoFramePlayer.DecodeFrameAtAsync(clip, seconds, width, height);
                Add(opened, action, watch.Elapsed.TotalMilliseconds);

                watch.Restart();
                await decoder.DecodeFrameAtAsync(seconds);
                Add(kept, action, watch.Elapsed.TotalMilliseconds);
            }

            _output.WriteLine($"kept decoder: {decoder.Opens} open, {decoder.Seeks} seeks for {clicks.Count} clicks");
        }

        foreach (string action in opened.Keys)
        {
            _output.WriteLine($"{action,-12} opened each time {Summary(opened[action])} | kept {Summary(kept[action])}");
        }

        // A drag along the seek bar as the editor sees it, each position asked for as the last one's frame arrives: across
        // much of the clip (every frame needs a seek) and a pixel at a time on a bar about 800 wide.
        double perPixel = info.Duration / 800;
        foreach ((string name, double stride) in new[] { ("a fast drag", info.Duration * 0.6 / 40), ("a slow drag", perPixel) })
        {
            foreach (bool keyframeWillDo in new[] { false, true, false, true })
            {
                using var decoder = new LibavStillFrameDecoder(clip, width, height);
                await decoder.DecodeFrameAtAsync(info.Duration * 0.2);
                var times = new List<double>();
                for (int i = 1; i <= 40; i++)
                {
                    var watch = Stopwatch.StartNew();
                    await decoder.DecodeFrameAtAsync((info.Duration * 0.2) + (i * stride), default, keyframeWillDo);
                    times.Add(watch.Elapsed.TotalMilliseconds);
                }

                _output.WriteLine($"{name}, {(keyframeWillDo ? "keyframes" : "exact frames")}: a frame in {Summary(times)}, {decoder.Seeks} seeks");
            }
        }

        static long PrivateMegabytes()
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            using var process = Process.GetCurrentProcess();
            return process.PrivateMemorySize64 / (1024 * 1024);
        }

        static void Add(Dictionary<string, List<double>> times, string action, double ms)
        {
            if (!times.TryGetValue(action, out List<double>? list))
            {
                times[action] = list = [];
            }

            list.Add(ms);
        }

        static string Summary(List<double> ms)
        {
            double[] sorted = ms.Order().ToArray();
            return $"median {sorted[sorted.Length / 2],6:F1} ms ({sorted[0]:F1}-{sorted[^1]:F1})";
        }
    }

    // The frame of the clips a time shows: the first at or after it, as the player picks it, or none past the end.
    private static int? ExpectedFrame(double seconds, int frameCount)
    {
        int frame = Math.Max(0, (int)Math.Ceiling((seconds - 0.0005) * Fps));
        return frame < frameCount ? frame : null;
    }

    private static int? FrameIndexOf(byte[]? bgra)
    {
        if (bgra == null)
        {
            return null;
        }

        Assert.Equal(Width * Height * 4, bgra.Length);
        int centre = ((Height / 2 * Width) + (Width / 2)) * 4;
        return (int)Math.Round(bgra[centre] / GreyStep);
    }
}
