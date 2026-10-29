using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using GimmeCapture.Models;
using GimmeCapture.Services.Core.Media.NativeFFmpeg;
using Xunit.Abstractions;

namespace GimmeCapture.Tests;

/// <summary>
/// Times the offline software encoders on a synthetic ten-second 1080p clip and prints wall time, frames per second and
/// how many cores they kept busy: H.264 and H.265 at the compress tab's defaults (preset medium, CRF 22), two H.264
/// encodes at once as the compress queue runs them by default, and the VP9 WebM transcode. libavcodec gives an encoder
/// one thread unless it is asked for more, which held x265 to one frame at a time and VP9 to one core (x264 asks for
/// auto itself). Opt-in (ENCODE_BENCH=1): it takes about a minute, and its numbers are for reading, not for asserting on
/// a shared CI machine.
/// </summary>
public sealed class LibavClipExporterThreadingTests : IDisposable
{
    private const int Width = 1920;
    private const int Height = 1080;
    private const int Fps = 30;
    private const int Frames = Fps * 10;

    private readonly ITestOutputHelper _output;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "GimmeCapture.Tests", Guid.NewGuid().ToString("N"));

    public LibavClipExporterThreadingTests(ITestOutputHelper output)
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

    [OptInFact("ENCODE_BENCH=1")]
    public void Encode1080p_ReportsThroughput()
    {
        string source = VideoTestClips.WriteConstantRate(Path.Combine(_dir, "source.mp4"), Width, Height, Frames, Fps, detailed: true);
        var ranges = new[] { new LibavClipExporter.SourceRange(0, (double)Frames / Fps) };
        var options = new LibavExportOptions { Preset = "medium", CrfOverride = 22, DropAudio = true };

        bool Export(string name, LibavExportOptions? with = null) =>
            LibavClipExporter.TryExport(source, ranges, Path.Combine(_dir, name), VideoQuality.Medium, options: with ?? options);

        Measure("H.264 medium CRF 22, one encode", Frames, () => Assert.True(Export("h264.mp4")));
        Measure("H.265 medium CRF 22, one encode", Frames, () => Assert.True(Export(
            "h265.mp4", new LibavExportOptions { Codec = VideoCodec.H265, Preset = "medium", CrfOverride = 22, DropAudio = true })));
        Measure("H.264 medium CRF 22, two at once", 2 * Frames, () =>
        {
            Task<bool> first = OwnThread.Run(() => Export("h264_a.mp4"));
            Task<bool> second = OwnThread.Run(() => Export("h264_b.mp4"));
            Assert.True(first.GetAwaiter().GetResult() && second.GetAwaiter().GetResult());
        });
        Measure("VP9 WebM, quality Medium", Frames, () =>
            LibavWebmTranscoder.TranscodeToWebm(source, Path.Combine(_dir, "vp9.webm"), Fps, VideoQuality.Medium));
    }

    private void Measure(string label, int frames, Action encode)
    {
        using Process self = Process.GetCurrentProcess();
        TimeSpan cpuBefore = self.TotalProcessorTime;
        var clock = Stopwatch.StartNew();
        encode();
        clock.Stop();
        self.Refresh();
        double cpuSeconds = (self.TotalProcessorTime - cpuBefore).TotalSeconds;
        double wall = clock.Elapsed.TotalSeconds;
        _output.WriteLine(
            $"{label}: {wall:F2} s wall, {frames / wall:F1} fps, {cpuSeconds:F1} s CPU, " +
            $"{cpuSeconds / wall:F1} of {Environment.ProcessorCount} logical cores busy");
    }
}
