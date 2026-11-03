using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using GimmeCapture.Models;
using GimmeCapture.Services.Core.Media;
using GimmeCapture.Services.Core.Media.NativeFFmpeg;
using Xunit.Abstractions;

namespace GimmeCapture.Tests;

/// <summary>
/// A compress at a quality (CRF) of a whole clip into a plain container, the Compress tab's default: the clip is encoded
/// in chunks that a restart can resume from, then joined and given its sound. Real libav on tiny synthetic clips (<see
/// cref="VideoTestClips"/>); the chunks go to the test run's own data folder (<see cref="TestStorageRoot"/>).
/// </summary>
public sealed class CompressPipelineSegmentedTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "GimmeCapture.Tests", Guid.NewGuid().ToString("N"));
    private readonly ITestOutputHelper _output;

    public CompressPipelineSegmentedTests(ITestOutputHelper output)
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

    // With no sound to add, the joined chunks were copied to the output as they were, a Matroska file, whatever the
    // output's name said.
    [Theory]
    [InlineData(".mp4", false, false)] // a recording with no sound
    [InlineData(".mp4", true, true)]   // its sound dropped
    [InlineData(".mp4", true, false)]
    [InlineData(".mkv", false, false)]
    [InlineData(".mkv", true, false)]
    public async Task TheOutput_IsTheContainerItsNameSays(string extension, bool withSound, bool dropAudio)
    {
        string clip = withSound
            ? VideoTestClips.WriteWithTone(Path.Combine(_dir, "clip.mkv"), 64, 48, 60, 30)
            : VideoTestClips.WriteConstantRate(Path.Combine(_dir, "clip.mkv"), 64, 48, 60, 30);
        string output = Path.Combine(_dir, "out" + extension);

        bool ok = await CompressPipeline.EncodeOneFileAsync(
            clip, output, Settings() with { DropAudio = dropAudio }, 0, 2.0, new Progress<double>(),
            CancellationToken.None, pauseGate: null);

        Assert.True(ok);
        Assert.Equal(extension == ".mp4" ? "mp4" : "matroska", ContainerOf(output));
        using var player = new LibavVideoFramePlayer();
        Assert.Equal(withSound && !dropAudio, await player.ProbeHasAudioAsync(output));
    }

    // The silent output is the joined chunks remuxed, which must keep every frame. (A clip with the tone is Matroska
    // with millisecond times, which the encode reads as 30.3 frames a second, so it has a frame more.)
    [Theory]
    [InlineData(".mp4")]
    [InlineData(".mkv")]
    public async Task WithNoSound_EveryFrameComesThrough(string extension)
    {
        string clip = VideoTestClips.WriteConstantRate(Path.Combine(_dir, "clip.mkv"), 64, 48, 60, 30);
        string output = Path.Combine(_dir, "out" + extension);

        Assert.True(await CompressPipeline.EncodeOneFileAsync(
            clip, output, Settings(), 0, 2.0, new Progress<double>(), CancellationToken.None, pauseGate: null));

        VideoTestClips.Timeline timeline = VideoTestClips.Read(output);
        Assert.Equal(60, timeline.Frames);
        Assert.Equal(2.0, timeline.DurationSeconds, 2);
    }

    // A compress of two minutes is four chunks with B-frames, joined at three places. The old join started each chunk
    // two frames after the one before it ended, so the picture ran 0.2 s long of the sound by the end, and further for
    // every 30 s more.
    [Fact]
    public async Task ALongerCompress_KeepsThePictureAsLongAsItsSound()
    {
        string clip = VideoTestClips.WriteWithTone(Path.Combine(_dir, "clip.mkv"), 64, 48, 3600, 30);
        string output = Path.Combine(_dir, "out.mp4");

        Assert.True(await CompressPipeline.EncodeOneFileAsync(
            clip, output, Settings() with { Preset = "medium" }, 0, 120, new Progress<double>(), CancellationToken.None,
            pauseGate: null));

        double picture = VideoTestClips.Read(output).DurationSeconds;
        double sound = LibavPinAudioPcmDecoder.Decode(output, 0).PcmBytes.Length / (48_000.0 * 4);
        Assert.Equal(sound, picture, 0.05);
        Assert.Equal(120, picture, 0.05);
    }

    /// <summary>
    /// How long a compress goes on once its chunks are done and the bar shows 100%: the chunks joined, the sound built
    /// (alongside the chunks now, after them before), the two muxed; then how long the sound takes to build on its own,
    /// which the finalize used to add. Opt-in: SEGMENTED_FINALIZE_PROBE=1, with SEGMENTED_FINALIZE_PROBE_FILE for a real
    /// video, otherwise SEGMENTED_FINALIZE_PROBE_MINUTES (10 by default) of a clip with a tone, 64x48 unless
    /// SEGMENTED_FINALIZE_PROBE_SIZE says (960x540, say, for chunks that take longer than the sound).
    /// </summary>
    [OptInFact("SEGMENTED_FINALIZE_PROBE=1")]
    public async Task TheFinalizeAfterTheBarIsFull_Timed()
    {
        string clip;
        double duration;
        if (Environment.GetEnvironmentVariable("SEGMENTED_FINALIZE_PROBE_FILE") is { Length: > 0 } file)
        {
            clip = file;
            duration = await CompressPipeline.ProbeInputDurationAsync(clip);
        }
        else
        {
            double minutes = double.TryParse(Environment.GetEnvironmentVariable("SEGMENTED_FINALIZE_PROBE_MINUTES"), out double m)
                ? m
                : 10;
            string[] size = (Environment.GetEnvironmentVariable("SEGMENTED_FINALIZE_PROBE_SIZE") ?? "64x48").Split('x');
            int width = int.Parse(size[0]);
            int height = int.Parse(size[1]);
            clip = VideoTestClips.WriteWithTone(
                Path.Combine(_dir, "long.mkv"), width, height, (int)(minutes * 60 * 30), 30, detailed: width > 64);
            duration = minutes * 60;
        }

        for (int run = 0; run < 2; run++)
        {
            // The chunks report in whole percents, so the last one ends just short of 1; the 1 itself comes at the end.
            var watch = Stopwatch.StartNew();
            double barFullAt = -1;
            var bar = new InlineProgress(value =>
            {
                if (value < 1)
                {
                    barFullAt = watch.Elapsed.TotalSeconds;
                }
            });
            string output = Path.Combine(_dir, $"out{run}.mp4");
            Assert.True(await CompressPipeline.EncodeOneFileAsync(
                clip, output, Settings() with { Preset = "medium", Crf = 23 }, 0, duration, bar, CancellationToken.None,
                pauseGate: null));
            double total = watch.Elapsed.TotalSeconds;
            _output.WriteLine(
                $"{Path.GetFileName(clip)} ({duration:F0} s): {total:F2} s in all, the bar full at {barFullAt:F2} s, " +
                $"then {total - barFullAt:F2} s more");
            File.Delete(output);
        }

        string soundDir = Directory.CreateDirectory(Path.Combine(_dir, "sound")).FullName;
        var soundWatch = Stopwatch.StartNew();
        LibavClipExporter.TryBuildAudio(
            clip, [new LibavClipExporter.SourceRange(0, duration)], soundDir, VideoQuality.Medium, new LibavExportOptions(),
            pauseGate: null, CancellationToken.None, out _);
        _output.WriteLine($"the sound alone: {soundWatch.Elapsed.TotalSeconds:F2} s");
    }

    // Reports on the encoding thread itself, unlike Progress<T>.
    private sealed class InlineProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }

    // What the file's first bytes say it is: an MP4 begins with an ftyp box, a Matroska file with the EBML magic.
    private static string ContainerOf(string path)
    {
        var head = new byte[12];
        using (FileStream file = File.OpenRead(path))
        {
            file.ReadExactly(head);
        }

        if (head[4] == 'f' && head[5] == 't' && head[6] == 'y' && head[7] == 'p')
        {
            return "mp4";
        }

        return head[0] == 0x1A && head[1] == 0x45 && head[2] == 0xDF && head[3] == 0xA3 ? "matroska" : "unknown";
    }

    private static CompressSettingsSnapshot Settings() => new(
        VideoCodec.H264, 28, MaxHeight: 0, MaxFps: 0, "ultrafast",
        DropAudio: false, AudioBitrateKbps: 128, AudioChannels: 0,
        UseTargetSize: false, TargetSizeMB: 0, UseTwoPass: false,
        DenoiseMode.Off, SharpenMode.Off, Deblock: false, Grayscale: false);
}
