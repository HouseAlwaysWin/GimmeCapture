using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GimmeCapture.Models;
using GimmeCapture.Services.Core.Media;
using GimmeCapture.Services.Core.Media.NativeFFmpeg;
using Xunit.Abstractions;

namespace GimmeCapture.Tests;

/// <summary>
/// A compress to GIF or WebM transcodes an unedited clip from the source, and an edited one from an H.264 intermediate
/// with the edits applied. The intermediate used to be made for every clip; it reported the whole progress bar and the
/// transcode nothing, and the transcode never saw the cancel token or the pause, so a cancel during it ran it to the end
/// and the file came out as done. The tests tell the steps apart by the files they write in the pipeline's temporary
/// folder. Real libav on tiny synthetic clips (<see cref="VideoTestClips"/>), so like
/// <see cref="LibavWebmTranscoderTests"/> they need only the bundled FFmpeg.
/// </summary>
public sealed class CompressPipelineGifWebmTests : IDisposable
{
    // A split that joins up again, which only an intermediate can render: the same frames as the whole clip.
    private static readonly (double Start, double End, double Speed)[] SplitInTwo = [(0, 1.5, 1.0), (1.5, 3.0, 1.0)];

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "GimmeCapture.Tests", Guid.NewGuid().ToString("N"));
    private readonly ITestOutputHelper _output;

    public CompressPipelineGifWebmTests(ITestOutputHelper output)
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

    [Theory]
    [InlineData(".gif", false)]
    [InlineData(".gif", true)]
    [InlineData(".webm", false)]
    [InlineData(".webm", true)]
    public async Task Progress_MovesOnThroughTheTranscode_AndEndsAtOne(string extension, bool throughAnIntermediate)
    {
        string clip = VideoTestClips.WriteConstantRate(Path.Combine(_dir, "clip.mkv"), 64, 48, 90, 30, detailed: true);
        var transcode = new TranscodeWatch(extension);
        var reported = new List<double>();
        int duringTranscode = 0;

        bool ok = await CompressPipeline.EncodeOneFileAsync(
            clip, Path.Combine(_dir, "out" + extension), Settings(), 0, 3.0,
            new InlineProgress(value =>
            {
                reported.Add(value);
                if (value < 1 && transcode.HasStarted())
                {
                    duringTranscode++;
                }
            }),
            CancellationToken.None, pauseGate: null, keptRuns: throughAnIntermediate ? SplitInTwo : null);

        Assert.True(ok);
        Assert.Equal(1.0, reported[^1]);
        Assert.True(reported.Zip(reported.Skip(1)).All(pair => pair.First <= pair.Second), "progress went back");
        Assert.True(duringTranscode > 0, "nothing was reported while the transcode ran");
    }

    [Theory]
    [InlineData(".gif", false)]
    [InlineData(".gif", true)]
    [InlineData(".webm", false)]
    [InlineData(".webm", true)]
    public async Task CancellingDuringTheTranscode_StopsIt_AndWritesNoFile(string extension, bool throughAnIntermediate)
    {
        string clip = VideoTestClips.WriteConstantRate(Path.Combine(_dir, "clip.mkv"), 64, 48, 90, 30, detailed: true);
        string output = Path.Combine(_dir, "cancelled" + extension);
        var transcode = new TranscodeWatch(extension);
        using var cancellation = new CancellationTokenSource();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CompressPipeline.EncodeOneFileAsync(
            clip, output, Settings(), 0, 3.0,
            new InlineProgress(_ =>
            {
                if (!cancellation.IsCancellationRequested && transcode.HasStarted())
                {
                    cancellation.Cancel();
                }
            }),
            cancellation.Token, pauseGate: null, keptRuns: throughAnIntermediate ? SplitInTwo : null));

        Assert.True(cancellation.IsCancellationRequested);
        Assert.False(File.Exists(output));
    }

    [Theory]
    [InlineData(".gif")]
    [InlineData(".webm")]
    public async Task AnUneditedClip_IsTranscodedFromTheSource_WithNoIntermediate(string extension)
    {
        string clip = VideoTestClips.WriteWithTone(Path.Combine(_dir, "clip.mkv"), 64, 48, 90, 30, detailed: true);

        (bool ok, bool intermediate) = await EncodeWatchingTheIntermediateAsync(clip, "out" + extension, Settings());

        Assert.True(ok);
        Assert.False(intermediate);
    }

    [Theory]
    [InlineData(".gif", "trimmed")]
    [InlineData(".webm", "sped up")]
    [InlineData(".gif", "rotated")]
    [InlineData(".webm", "a filter")]
    [InlineData(".gif", "scaled down")]
    [InlineData(".webm", "fewer frames")]
    [InlineData(".webm", "mono")]
    public async Task AnEditedClip_StillGoesThroughTheIntermediate(string extension, string edit)
    {
        string clip = VideoTestClips.WriteWithTone(Path.Combine(_dir, "clip.mkv"), 64, 48, 90, 30, detailed: true);
        (CompressSettingsSnapshot settings, (double, double, double)[]? keptRuns, int rotation) = edit switch
        {
            "trimmed" => (Settings(), [(0.5, 2.5, 1.0)], 0),
            "sped up" => (Settings(), [(0, 3.0, 2.0)], 0),
            "rotated" => (Settings(), null, 90),
            "a filter" => (Settings() with { Grayscale = true }, null, 0),
            "scaled down" => (Settings() with { MaxHeight = 24 }, null, 0), // the clip is 48 tall
            "fewer frames" => (Settings() with { MaxFps = 15 }, null, 0),  // the clip has 30 a second
            "mono" => (Settings() with { AudioChannels = 1 }, ((double, double, double)[]?)null, 0),
            _ => throw new ArgumentOutOfRangeException(nameof(edit)),
        };

        (bool ok, bool intermediate) = await EncodeWatchingTheIntermediateAsync(
            clip, "out" + extension, settings, keptRuns, rotation);

        Assert.True(ok);
        Assert.True(intermediate);
    }

    // Each with a frame-rate cap of 30, the clip's own rate.
    [Theory]
    [InlineData(".gif", 0)]   // a GIF's rate is a setting of its own
    [InlineData(".gif", 48)]  // and a size cap of the clip's own height
    [InlineData(".webm", 0)]  // the clip has 30 frames a second
    public async Task ACapTheClipIsWithin_LeavesItToTheSource(string extension, int maxHeight)
    {
        string clip = VideoTestClips.WriteWithTone(Path.Combine(_dir, "clip.mkv"), 64, 48, 90, 30, detailed: true);

        (bool ok, bool intermediate) = await EncodeWatchingTheIntermediateAsync(
            clip, "out" + extension, Settings() with { MaxHeight = maxHeight, MaxFps = 30 });

        Assert.True(ok);
        Assert.False(intermediate);
    }

    [Theory]
    [InlineData(".gif")]
    [InlineData(".webm")]
    public async Task FromTheSource_TheSameFramesComeOut_AsThroughAnIntermediate(string extension)
    {
        string clip = VideoTestClips.WriteWithTone(Path.Combine(_dir, "clip.mkv"), 64, 48, 90, 30, detailed: true);
        string straight = Path.Combine(_dir, "straight" + extension);
        string through = Path.Combine(_dir, "through" + extension);

        Assert.True(await CompressPipeline.EncodeOneFileAsync(
            clip, straight, Settings(), 0, 3.0, new InlineProgress(_ => { }), CancellationToken.None, pauseGate: null));
        Assert.True(await CompressPipeline.EncodeOneFileAsync(
            clip, through, Settings(), 0, 3.0, new InlineProgress(_ => { }), CancellationToken.None, pauseGate: null,
            keptRuns: SplitInTwo));

        VideoTestClips.Timeline fromSource = VideoTestClips.Read(straight);
        VideoTestClips.Timeline fromIntermediate = VideoTestClips.Read(through);
        Assert.Equal(fromIntermediate.Frames, fromSource.Frames);
        Assert.Equal(fromIntermediate.DurationSeconds, fromSource.DurationSeconds, 2);
        using var player = new LibavVideoFramePlayer();
        Assert.Equal(await player.ProbeVideoSizeAsync(through), await player.ProbeVideoSizeAsync(straight));
        Assert.Equal(await player.ProbeHasAudioAsync(through), await player.ProbeHasAudioAsync(straight));
    }

    [Theory]
    [InlineData(".gif", false)]
    [InlineData(".gif", true)]
    [InlineData(".webm", false)]
    [InlineData(".webm", true)]
    public async Task PausedDuringTheTranscode_ItStops_UntilResumed(string extension, bool throughAnIntermediate)
    {
        string clip = VideoTestClips.WriteConstantRate(Path.Combine(_dir, "clip.mkv"), 64, 48, 90, 30, detailed: true);
        string output = Path.Combine(_dir, "paused" + extension);
        var transcode = new TranscodeWatch(extension);
        using var pause = new ManualResetEventSlim(true);
        var paused = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int reports = 0;

        Task<bool> encoding = CompressPipeline.EncodeOneFileAsync(
            clip, output, Settings(), 0, 3.0,
            new InlineProgress(_ =>
            {
                Interlocked.Increment(ref reports);
                if (pause.IsSet && !paused.Task.IsCompleted && transcode.HasStarted())
                {
                    pause.Reset();
                    paused.TrySetResult();
                }
            }),
            CancellationToken.None, pause, keptRuns: throughAnIntermediate ? SplitInTwo : null);
        await paused.Task.WaitAsync(TimeSpan.FromSeconds(30));

        // A frame already on its way may still report; after that nothing moves while paused.
        int settled = await SettledAsync(() => Volatile.Read(ref reports));
        await OwnThread.Delay(TimeSpan.FromMilliseconds(500));

        Assert.False(encoding.IsCompleted, "the transcode ran on while paused");
        Assert.Equal(settled, Volatile.Read(ref reports));

        pause.Set();
        Assert.True(await encoding);
        Assert.True(File.Exists(output));
    }

    [Fact]
    public void NeedsNoIntermediate_OnlyForTheWholeClip_AsItIs()
    {
        LibavClipExporter.SourceRange[] whole = [new(0, 3.0)];
        Assert.True(CompressPipeline.NeedsNoIntermediate(whole, 3.0, 0, null, false, Settings(), isGif: false));
        LibavClipExporter.SourceRange[] lengthNotKnown = [new(0, CompressPipeline.UnknownLengthEndSeconds)];
        Assert.True(CompressPipeline.NeedsNoIntermediate(lengthNotKnown, 0, 360, null, false, Settings(), false));
        Assert.False(CompressPipeline.NeedsNoIntermediate([new(0, 5.0)], 0, 0, null, false, Settings(), false));

        Assert.False(CompressPipeline.NeedsNoIntermediate([new(0.5, 3.0)], 3.0, 0, null, false, Settings(), false));
        Assert.False(CompressPipeline.NeedsNoIntermediate([new(0, 2.5)], 3.0, 0, null, false, Settings(), false));
        Assert.False(CompressPipeline.NeedsNoIntermediate([new(0, 3.0, 1.5)], 3.0, 0, null, false, Settings(), false));
        Assert.False(CompressPipeline.NeedsNoIntermediate([new(0, 1.5), new(1.5, 3.0)], 3.0, 0, null, false, Settings(), false));
        Assert.False(CompressPipeline.NeedsNoIntermediate(whole, 3.0, 270, null, false, Settings(), false));
        Assert.False(CompressPipeline.NeedsNoIntermediate(whole, 3.0, 0, new VideoEditCrop(0, 0, 32, 24), false, Settings(), false));
        Assert.False(CompressPipeline.NeedsNoIntermediate(whole, 3.0, 0, null, burnsIn: true, Settings(), false));
        Assert.False(CompressPipeline.NeedsNoIntermediate(whole, 3.0, 0, null, false, Settings() with { Deblock = true }, false));
        Assert.False(CompressPipeline.NeedsNoIntermediate(
            whole, 3.0, 0, null, false, Settings() with { Denoise = DenoiseMode.Light }, false));
        Assert.False(CompressPipeline.NeedsNoIntermediate(
            whole, 3.0, 0, null, false, Settings() with { Sharpen = SharpenMode.Light }, false));

        // Mono matters only to a WebM's sound, and not once the sound is dropped.
        CompressSettingsSnapshot mono = Settings() with { AudioChannels = 1 };
        Assert.False(CompressPipeline.NeedsNoIntermediate(whole, 3.0, 0, null, false, mono, isGif: false));
        Assert.True(CompressPipeline.NeedsNoIntermediate(whole, 3.0, 0, null, false, mono with { DropAudio = true }, false));
        Assert.True(CompressPipeline.NeedsNoIntermediate(whole, 3.0, 0, null, false, mono, isGif: true));
    }

    [Theory]
    [InlineData(0, 0, false, 1080, 60, true)]
    [InlineData(1080, 0, false, 900, 30, true)]   // within the size cap
    [InlineData(720, 0, false, 900, 30, false)]   // scaled down
    [InlineData(720, 0, false, 0, 30, false)]     // a size not known
    [InlineData(0, 30, false, 900, 30, true)]     // the rate cap is the source's rate
    [InlineData(0, 30, false, 900, 60, false)]    // fewer frames
    [InlineData(0, 30, true, 900, 60, true)]      // a GIF's rate is its own setting
    public void CapsLeaveTheSource_WhenItIsWithinThem(
        int maxHeight, int maxFps, bool isGif, int sourceHeight, int sourceFps, bool expected)
    {
        CompressSettingsSnapshot settings = Settings() with { MaxHeight = maxHeight, MaxFps = maxFps };

        Assert.Equal(expected, CompressPipeline.CapsLeaveTheSource(settings, isGif, sourceHeight, sourceFps));
    }

    /// <summary>
    /// What going straight from the source saves, against the intermediate a split that joins up again makes, on a
    /// real recording with the Compress tab's defaults (CRF 22, the medium preset). Each way runs twice, in the order
    /// through, from, from, through, so a processor that warms up over the runs slows both alike. Opt-in:
    /// COMPRESS_GIFWEBM_PROBE names the clip; COMPRESS_GIFWEBM_PROBE_FORMATS=.gif or .webm runs only that one.
    /// </summary>
    [OptInFact("COMPRESS_GIFWEBM_PROBE")]
    public async Task FromTheSource_AgainstThroughAnIntermediate_Timed()
    {
        string clip = Environment.GetEnvironmentVariable("COMPRESS_GIFWEBM_PROBE")!;
        string[] formats = Environment.GetEnvironmentVariable("COMPRESS_GIFWEBM_PROBE_FORMATS") is { Length: > 0 } only
            ? only.Split(',')
            : [".gif", ".webm"];
        double duration = await CompressPipeline.ProbeInputDurationAsync(clip);
        (double, double, double)[] split = [(0, duration / 2, 1.0), (duration / 2, duration, 1.0)];
        foreach (string extension in formats)
        {
            foreach (bool throughAnIntermediate in new[] { true, false, false, true })
            {
                string output = Path.Combine(_dir, $"probe{(throughAnIntermediate ? "-through" : "")}{extension}");
                var watch = Stopwatch.StartNew();
                bool ok = await CompressPipeline.EncodeOneFileAsync(
                    clip, output, Settings(crf: 22, preset: "medium"), 0, duration, new InlineProgress(_ => { }),
                    CancellationToken.None, pauseGate: null, keptRuns: throughAnIntermediate ? split : null);
                Assert.True(ok);
                VideoTestClips.Timeline timeline = VideoTestClips.Read(output);
                _output.WriteLine(
                    $"{extension} {(throughAnIntermediate ? "through an intermediate" : "from the source"),-23}: " +
                    $"{watch.Elapsed.TotalSeconds:F2} s for a {duration:F1} s clip, {new FileInfo(output).Length / 1024} KB, " +
                    $"{timeline.Frames} frames over {timeline.DurationSeconds:F2} s");
            }
        }
    }

    // The count once it has stayed the same for 300 ms, or where it is after 10 s.
    private static async Task<int> SettledAsync(Func<int> count)
    {
        var waited = Stopwatch.StartNew();
        int last = count();
        while (waited.Elapsed < TimeSpan.FromSeconds(10))
        {
            await OwnThread.Delay(TimeSpan.FromMilliseconds(300));
            int now = count();
            if (now == last)
            {
                return now;
            }

            last = now;
        }

        return last;
    }

    // Encodes the clip, telling whether an intermediate appeared in the pipeline's temporary folder on the way.
    private async Task<(bool Ok, bool Intermediate)> EncodeWatchingTheIntermediateAsync(
        string clip, string outputName, CompressSettingsSnapshot settings,
        (double, double, double)[]? keptRuns = null, int rotation = 0)
    {
        var watch = new TempFileWatch("intermediate.mp4");
        bool seen = false;
        bool ok = await CompressPipeline.EncodeOneFileAsync(
            clip, Path.Combine(_dir, outputName), settings, 0, 3.0, new InlineProgress(_ => seen |= watch.HasAppeared()),
            CancellationToken.None, pauseGate: null, rotationDegrees: rotation, keptRuns: keptRuns);
        return (ok, seen);
    }

    private static CompressSettingsSnapshot Settings(int crf = 22, string preset = "ultrafast") => new(
        VideoCodec.H264, crf, MaxHeight: 0, MaxFps: 0, preset,
        DropAudio: false, AudioBitrateKbps: 128, AudioChannels: 2,
        UseTargetSize: false, TargetSizeMB: 0, UseTwoPass: false,
        DenoiseMode.Off, SharpenMode.Off, Deblock: false, Grayscale: false);

    // The transcode's first file in a temporary folder the pipeline made since this watch was: the GIF's own (its
    // encode pass, after the palette pass) or the WebM's video.
    private sealed class TranscodeWatch(string extension)
    {
        private readonly TempFileWatch _file = new(extension == ".gif" ? "output.gif" : "video.webm");

        public bool HasStarted() => _file.HasAppeared();
    }

    // A file by this name in a temporary folder the pipeline made since this watch was; folders left by earlier runs do
    // not count.
    private sealed class TempFileWatch(string name)
    {
        private readonly HashSet<string> _before = Folders().ToHashSet(StringComparer.OrdinalIgnoreCase);

        public bool HasAppeared() =>
            Folders().Any(folder => !_before.Contains(folder) && File.Exists(Path.Combine(folder, name)));

        private static IEnumerable<string> Folders() =>
            Directory.EnumerateDirectories(Path.GetTempPath(), "GimmeCapture_Compress_*");
    }

    // Reports on the encoding thread itself, unlike Progress<T>, so a test can act at an exact point of the encode.
    private sealed class InlineProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }
}
