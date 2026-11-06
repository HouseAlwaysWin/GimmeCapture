using System.Diagnostics;
using GimmeCapture.Models;
using GimmeCapture.Services.Core.Media;
using GimmeCapture.Services.Core.Media.NativeFFmpeg;
using Xunit.Abstractions;

namespace GimmeCapture.Tests;

/// <summary>
/// A pinned recording with a cut, saved or copied as a GIF, the way the pin makes it: the cut goes to an MP4 first, which
/// the GIF is then made from at Medium quality. As it was (the MP4 with its sound, each GIF pass decoding in turn) against
/// as it is (no sound in the MP4, each pass decoding ahead on a thread of its own), alternately, and whether the two GIFs
/// are the same. Opt-in (PIN_GIF_CUT_CLIP names a recording); it writes only to a folder of its own.
/// </summary>
public sealed class PinGifCutProbeTests
{
    private readonly ITestOutputHelper _output;

    public PinGifCutProbeTests(ITestOutputHelper output) => _output = output;

    [OptInFact("PIN_GIF_CUT_CLIP")]
    public async Task ACutGif_AsItWas_AgainstAsItIs()
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
            var cut = new Dictionary<bool, List<double>> { [false] = new(), [true] = new() };
            var gif = new Dictionary<bool, List<double>> { [false] = new(), [true] = new() };
            var made = new Dictionary<bool, string>();
            for (int round = 0; round < 4; round++)
            {
                foreach (bool now in round % 2 == 0 ? new[] { false, true } : new[] { true, false })
                {
                    string mid = Path.Combine(folder, $"mid{round}{now}.mp4");
                    string result = Path.Combine(folder, $"out{round}{now}.gif");
                    var options = now ? new LibavExportOptions { DropAudio = true } : null;
                    var stopwatch = Stopwatch.StartNew();
                    Assert.True(LibavClipExporter.TryExport(clip, ranges, mid, VideoQuality.Medium, options: options));
                    cut[now].Add(stopwatch.Elapsed.TotalMilliseconds);
                    stopwatch.Restart();
                    LibavGifTranscoder.TranscodeToGif(mid, result, gifFps, maxWidth, dither, decodeAhead: now);
                    gif[now].Add(stopwatch.Elapsed.TotalMilliseconds);
                    made[now] = result;
                }
            }

            foreach (bool now in new[] { false, true })
            {
                _output.WriteLine(
                    $"{(now ? "as it is" : "as it was"),-10} cut {Describe(cut[now])}, GIF {Describe(gif[now])}, " +
                    $"total {Median(cut[now]) + Median(gif[now]):0} ms; {new FileInfo(made[now]).Length / 1024} KB");
            }

            Assert.Equal(File.ReadAllBytes(made[false]), File.ReadAllBytes(made[true]));
        }
        finally
        {
            try { Directory.Delete(folder, recursive: true); } catch { }
        }
    }

    private static double Median(List<double> values) => values.OrderBy(x => x).ElementAt(values.Count / 2);

    private static string Describe(List<double> values) => $"{Median(values):0} ms ({values.Min():0}-{values.Max():0})";
}
