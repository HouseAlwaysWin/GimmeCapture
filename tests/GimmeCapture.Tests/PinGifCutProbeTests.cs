using System.Diagnostics;
using GimmeCapture.Models;
using GimmeCapture.Services.Core.Media;
using GimmeCapture.Services.Core.Media.NativeFFmpeg;
using Xunit.Abstractions;

namespace GimmeCapture.Tests;

/// <summary>
/// A pinned recording with a cut, saved or copied as a GIF, at Medium quality. As it was (the cut encoded into an MP4 first,
/// which the GIF is then made from) against as it is (the GIF made straight from the recording's kept stretches),
/// alternately. Opt-in (PIN_GIF_CUT_CLIP names a recording); it writes only to a folder of its own.
/// </summary>
public sealed class PinGifCutProbeTests
{
    private readonly ITestOutputHelper _output;

    public PinGifCutProbeTests(ITestOutputHelper output) => _output = output;

    [OptInFact("PIN_GIF_CUT_CLIP")]
    public async Task ACutGif_ThroughAnMp4_AgainstStraightFromTheRecording()
    {
        string clip = Environment.GetEnvironmentVariable("PIN_GIF_CUT_CLIP")!;
        double duration = await CompressPipeline.ProbeInputDurationAsync(clip);
        Assert.True(duration > 1, $"{clip}: {duration} s");
        // The middle fifth cut out.
        var ranges = new[]
        {
            new LibavClipExporter.SourceRange(0, duration * 0.4, 1.0),
            new LibavClipExporter.SourceRange(duration * 0.6, duration, 1.0),
        };
        (int gifFps, int maxWidth) = LibavGifTranscoder.QualityLadder(VideoQuality.Medium);
        string dither = LibavGifTranscoder.QualityDither(VideoQuality.Medium);

        string folder = Path.Combine(Path.GetTempPath(), "pin-gif-cut-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var total = new Dictionary<bool, List<double>> { [false] = new(), [true] = new() };
            var made = new Dictionary<bool, string>();
            for (int round = 0; round < 4; round++)
            {
                foreach (bool straight in round % 2 == 0 ? new[] { false, true } : new[] { true, false })
                {
                    string result = Path.Combine(folder, $"out{round}{straight}.gif");
                    var stopwatch = Stopwatch.StartNew();
                    if (straight)
                    {
                        LibavGifTranscoder.TranscodeToGif(clip, result, gifFps, maxWidth, dither, ranges: ranges);
                    }
                    else
                    {
                        string mid = Path.Combine(folder, $"mid{round}.mp4");
                        Assert.True(LibavClipExporter.TryExport(
                            clip, ranges, mid, VideoQuality.Medium, options: new LibavExportOptions { DropAudio = true }));
                        LibavGifTranscoder.TranscodeToGif(mid, result, gifFps, maxWidth, dither);
                    }

                    total[straight].Add(stopwatch.Elapsed.TotalMilliseconds);
                    made[straight] = result;
                }
            }

            foreach (bool straight in new[] { false, true })
            {
                VideoTestClips.Timeline timeline = VideoTestClips.Read(made[straight]);
                _output.WriteLine(
                    $"{(straight ? "straight" : "through an MP4"),-15} {Describe(total[straight])}; " +
                    $"{new FileInfo(made[straight]).Length / 1024} KB, {timeline.Frames} frames over {timeline.DurationSeconds:F2} s");
            }
        }
        finally
        {
            try { Directory.Delete(folder, recursive: true); } catch { }
        }
    }

    private static double Median(List<double> values) => values.OrderBy(x => x).ElementAt(values.Count / 2);

    private static string Describe(List<double> values) => $"{Median(values):0} ms ({values.Min():0}-{values.Max():0})";
}
