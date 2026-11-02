using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using GimmeCapture.Models;
using GimmeCapture.Services.Core.Media;
using GimmeCapture.Services.Core.Media.NativeFFmpeg;
using Xunit.Abstractions;

namespace GimmeCapture.Tests;

/// <summary>
/// A two-pass export runs its first pass for x264's statistics alone. That pass also wrote its whole video, an MP4 about
/// the size of the output, to the export's temporary folder, where nothing read it. Encodes a tiny synthetic clip
/// (<see cref="VideoTestClips"/>), so it needs only the bundled FFmpeg and runs everywhere.
/// </summary>
public sealed class LibavClipExporterTwoPassTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "GimmeCapture.Tests", Guid.NewGuid().ToString("N"));
    private readonly ITestOutputHelper _output;

    public LibavClipExporterTwoPassTests(ITestOutputHelper output)
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

    [Fact]
    public void TheFirstPass_WritesNoVideo_AndTheSecondStillUsesItsStatistics()
    {
        string source = VideoTestClips.WriteConstantRate(Path.Combine(_dir, "source.mkv"), 64, 48, 90, 30, detailed: true);
        string output = Path.Combine(_dir, "output.mp4");
        HashSet<string> before = ExportFolders().ToHashSet(StringComparer.OrdinalIgnoreCase);
        bool secondPassSeen = false;
        bool firstPassVideoSeen = false;

        bool ok = LibavClipExporter.TryExport(
            source,
            [new LibavClipExporter.SourceRange(0, 3.0)],
            output,
            VideoQuality.Low,
            options: new LibavExportOptions { TargetVideoBitrateKbps = 200, TwoPass = true, DropAudio = true },
            progress: new InlineProgress(value =>
            {
                if (value <= 0.5)
                {
                    return;
                }

                // The second pass is under way: the first one's files are all there is to see.
                secondPassSeen = true;
                firstPassVideoSeen |= ExportFolders()
                    .Where(folder => !before.Contains(folder))
                    .Any(folder => File.Exists(Path.Combine(folder, "pass1.mp4")));
            }));

        Assert.True(ok);
        Assert.True(secondPassSeen);
        Assert.False(firstPassVideoSeen, "the first pass wrote its video");
        Assert.Equal(90, VideoTestClips.Read(output).Frames);
    }

    /// <summary>
    /// A real recording to a target size, in one pass and in two, with the sizes and times. Opt-in: TWOPASS_PROBE names
    /// the clip, TWOPASS_PROBE_MB the target (3 MB unless given).
    /// </summary>
    [OptInFact("TWOPASS_PROBE")]
    public void ARealClip_ComesInAtItsTargetSize()
    {
        string source = Environment.GetEnvironmentVariable("TWOPASS_PROBE")!;
        double targetMb = double.TryParse(Environment.GetEnvironmentVariable("TWOPASS_PROBE_MB"), out double mb) ? mb : 3;
        double duration = new LibavVideoFramePlayer().ProbeDurationSecondsAsync(source).GetAwaiter().GetResult() ?? 0;
        int kbps = CompressEncodeMath.ComputeTargetVideoBitrateKbps(targetMb, duration);
        foreach (bool twoPass in new[] { false, true })
        {
            string output = Path.Combine(_dir, twoPass ? "two.mp4" : "one.mp4");
            var watch = System.Diagnostics.Stopwatch.StartNew();
            Assert.True(LibavClipExporter.TryExport(
                source,
                [new LibavClipExporter.SourceRange(0, duration)],
                output,
                VideoQuality.Medium,
                options: new LibavExportOptions { TargetVideoBitrateKbps = kbps, TwoPass = twoPass }));
            double megabytes = new FileInfo(output).Length / 1048576.0;
            _output.WriteLine(
                $"{(twoPass ? "two passes" : "one pass")}: {megabytes:F2} MB for a {targetMb} MB target " +
                $"({duration:F1} s at {kbps} kbps) in {watch.Elapsed.TotalSeconds:F1} s");
            if (twoPass)
            {
                Assert.InRange(megabytes, 0, targetMb * 1.1);
            }
        }
    }

    private static IEnumerable<string> ExportFolders() =>
        Directory.EnumerateDirectories(Path.GetTempPath(), "GimmeCapture_ClipExport_*");

    // Reports on the encoding thread itself, unlike Progress<T>, so the test looks while the second pass runs.
    private sealed class InlineProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }
}
