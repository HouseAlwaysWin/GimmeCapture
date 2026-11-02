using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GimmeCapture.Models;
using GimmeCapture.Services.Core.Media;
using Xunit.Abstractions;

namespace GimmeCapture.Tests;

/// <summary>
/// A compress to GIF or WebM renders an edited H.264 intermediate, then transcodes it. The intermediate reported the
/// whole progress bar and the transcode nothing, and the transcode never saw the cancel token, so a cancel during it
/// ran it to the end and the file came out as done. The tests tell the transcode is under way by the file it writes in
/// the pipeline's temporary folder. Real libav on tiny synthetic clips (<see cref="VideoTestClips"/>), so like
/// <see cref="LibavWebmTranscoderTests"/> they need only the bundled FFmpeg.
/// </summary>
public sealed class CompressPipelineGifWebmTests : IDisposable
{
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
    [InlineData(".gif")]
    [InlineData(".webm")]
    public async Task Progress_MovesOnThroughTheTranscode_AndEndsAtOne(string extension)
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
            CancellationToken.None, pauseGate: null);

        Assert.True(ok);
        Assert.Equal(1.0, reported[^1]);
        Assert.True(reported.Zip(reported.Skip(1)).All(pair => pair.First <= pair.Second), "progress went back");
        Assert.True(duringTranscode > 0, "nothing was reported while the transcode ran");
    }

    [Theory]
    [InlineData(".gif")]
    [InlineData(".webm")]
    public async Task CancellingDuringTheTranscode_StopsIt_AndWritesNoFile(string extension)
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
            cancellation.Token, pauseGate: null));

        Assert.True(cancellation.IsCancellationRequested);
        Assert.False(File.Exists(output));
    }

    /// <summary>
    /// How the bar is shared between the intermediate and the transcode, measured on a real recording with the Compress
    /// tab's defaults (CRF 22, the medium preset). Opt-in: COMPRESS_GIFWEBM_PROBE names the clip.
    /// </summary>
    [OptInFact("COMPRESS_GIFWEBM_PROBE")]
    public async Task TheIntermediatesShareOfTheTime()
    {
        string clip = Environment.GetEnvironmentVariable("COMPRESS_GIFWEBM_PROBE")!;
        double duration = await CompressPipeline.ProbeInputDurationAsync(clip);
        foreach (string extension in new[] { ".gif", ".webm" })
        {
            var transcode = new TranscodeWatch(extension);
            var watch = Stopwatch.StartNew();
            double intermediateSeconds = -1;
            bool ok = await CompressPipeline.EncodeOneFileAsync(
                clip, Path.Combine(_dir, "probe" + extension), Settings(crf: 22, preset: "medium"), 0, duration,
                new InlineProgress(_ =>
                {
                    if (intermediateSeconds < 0 && transcode.HasStarted())
                    {
                        intermediateSeconds = watch.Elapsed.TotalSeconds;
                    }
                }),
                CancellationToken.None, pauseGate: null);
            double total = watch.Elapsed.TotalSeconds;
            Assert.True(ok);
            _output.WriteLine(
                $"{extension}: {duration:F1} s clip, intermediate {intermediateSeconds:F2} s of {total:F2} s " +
                $"({intermediateSeconds / total:P0}), {new FileInfo(Path.Combine(_dir, "probe" + extension)).Length / 1024} KB");
        }
    }

    private static CompressSettingsSnapshot Settings(int crf = 22, string preset = "ultrafast") => new(
        VideoCodec.H264, crf, MaxHeight: 0, MaxFps: 0, preset,
        DropAudio: false, AudioBitrateKbps: 128, AudioChannels: 2,
        UseTargetSize: false, TargetSizeMB: 0, UseTwoPass: false,
        DenoiseMode.Off, SharpenMode.Off, Deblock: false, Grayscale: false);

    // The transcode's first file in a temporary folder the pipeline made since this watch was: the GIF's own (its
    // encode pass, after the palette pass) or the WebM's video. Folders left by earlier runs do not count.
    private sealed class TranscodeWatch(string extension)
    {
        private readonly HashSet<string> _before = Folders().ToHashSet(StringComparer.OrdinalIgnoreCase);
        private readonly string _file = extension == ".gif" ? "output.gif" : "video.webm";

        public bool HasStarted() =>
            Folders().Any(folder => !_before.Contains(folder) && File.Exists(Path.Combine(folder, _file)));

        private static IEnumerable<string> Folders() =>
            Directory.EnumerateDirectories(Path.GetTempPath(), "GimmeCapture_Compress_*");
    }

    // Reports on the encoding thread itself, unlike Progress<T>, so a test can act at an exact point of the encode.
    private sealed class InlineProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }
}
