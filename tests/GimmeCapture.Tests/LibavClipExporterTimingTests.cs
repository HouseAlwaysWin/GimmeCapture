using System;
using System.IO;
using System.Linq;
using FFmpeg.AutoGen;
using GimmeCapture.Models;
using GimmeCapture.Services.Core.Media.NativeFFmpeg;

namespace GimmeCapture.Tests;

/// <summary>
/// An export keeps real time. The encoder used to run at the source's frame rate rounded to a whole number and number
/// its frames one after another, so a 29.97 fps source came out 0.1% fast (3.6 s an hour) and a variable-rate one off
/// by however much its rate wandered. The output now runs at the source's exact rate, and each frame is placed by its
/// own timestamp. Encodes tiny synthetic clips (<see cref="VideoTestClips"/>), so it needs only the bundled FFmpeg and
/// runs everywhere.
/// </summary>
public sealed class LibavClipExporterTimingTests : IDisposable
{
    private const int Width = 64;
    private const int Height = 48;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "GimmeCapture.Tests", Guid.NewGuid().ToString("N"));

    public LibavClipExporterTimingTests()
    {
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
    public void ANtscSource_KeepsItsExactRateAndLength()
    {
        // A minute is long enough for the old rounding to 30 fps to lose more than a frame (60 ms of 60.06 s).
        const int frames = 1800;
        string source = VideoTestClips.WriteY4m(Path.Combine(_dir, "ntsc.y4m"), Width, Height, frames, 30000, 1001);
        double length = frames * 1001 / 30000.0;

        VideoTestClips.Timeline output = Export(source, new LibavClipExporter.SourceRange(0, length));

        Assert.Equal(30000, output.FrameRate.num);
        Assert.Equal(1001, output.FrameRate.den);
        Assert.Equal(frames, output.Frames);
        AssertWithinAFrame(length, output, 30000 / 1001.0);
    }

    [Fact]
    public void AVariableRateSource_KeepsItsLength()
    {
        // Two seconds at 30 fps, then four at 10 fps, stamped in milliseconds the way Matroska stores a recording. The
        // file records its six seconds, though no packet says how long its last frame shows.
        long[] pts = Enumerable.Range(0, 60).Select(i => (long)Math.Round(i * 1000 / 30.0))
            .Concat(Enumerable.Range(0, 40).Select(i => 2000L + (i * 100)))
            .ToArray();
        string source = VideoTestClips.WriteTimestamped(
            Path.Combine(_dir, "vfr.mkv"), Width, Height, new AVRational { num = 1, den = 1000 }, pts, lastFrameDuration: 100);
        const double length = 6.0;

        VideoTestClips.Timeline output = Export(source, new LibavClipExporter.SourceRange(0, length));

        double rate = (double)output.FrameRate.num / output.FrameRate.den;
        AssertWithinAFrame(length, output, rate);
        // Constant-rate output: the 10 fps stretch repeats frames to fill its time instead of playing them fast.
        Assert.Equal((int)Math.Round(length * rate), output.Frames);
    }

    [Fact]
    public void AFileThatStartsLate_ExportsFromItsFirstFrame()
    {
        // Three seconds at 30 fps whose timestamps begin at 1.4 s, as MPEG-TS files often do. A range from 0 must not
        // hold the first frame over the empty 1.4 s before it: the audio starts at its first sample, so that would put
        // the picture 1.4 s behind the sound.
        long[] pts = Enumerable.Range(0, 90).Select(i => 1400L + (long)Math.Round(i * 1000 / 30.0)).ToArray();
        string source = VideoTestClips.WriteTimestamped(
            Path.Combine(_dir, "late.mkv"), Width, Height, new AVRational { num = 1, den = 1000 }, pts, lastFrameDuration: 33);

        VideoTestClips.Timeline output = Export(source, new LibavClipExporter.SourceRange(0, 4.4));

        Assert.Equal(90, output.Frames);
        Assert.Equal(0, output.StartSeconds, 3);
        AssertWithinAFrame(3.0, output, 30);
    }

    [Fact]
    public void ASpeedRange_PlaysForItsLengthDividedByItsSpeed()
    {
        string source = VideoTestClips.WriteY4m(Path.Combine(_dir, "speed.y4m"), Width, Height, 90, 30, 1);

        VideoTestClips.Timeline output = Export(
            source, new LibavClipExporter.SourceRange(0, 2, Speed: 2.0), new LibavClipExporter.SourceRange(2, 3));

        // Two seconds at 2× take one; the last second plays as it is.
        Assert.Equal(60, output.Frames);
        AssertWithinAFrame(2.0, output, 30);
    }

    [Fact]
    public void SlowMotion_RepeatsFramesToFillItsLength()
    {
        string source = VideoTestClips.WriteY4m(Path.Combine(_dir, "slow.y4m"), Width, Height, 30, 30, 1);

        VideoTestClips.Timeline output = Export(source, new LibavClipExporter.SourceRange(0, 1, Speed: 0.5));

        Assert.Equal(60, output.Frames);
        AssertWithinAFrame(2.0, output, 30);
    }

    [Fact]
    public void TwoKeptRanges_AreJoinedEnd_ToEnd()
    {
        string source = VideoTestClips.WriteY4m(Path.Combine(_dir, "trim.y4m"), Width, Height, 90, 30, 1);

        VideoTestClips.Timeline output = Export(
            source, new LibavClipExporter.SourceRange(0, 1), new LibavClipExporter.SourceRange(2, 3));

        Assert.Equal(60, output.Frames);
        AssertWithinAFrame(2.0, output, 30);
    }

    [Fact]
    public void AnFpsCap_DropsFramesToTheCap_AndKeepsTheLength()
    {
        string source = VideoTestClips.WriteY4m(Path.Combine(_dir, "cap.y4m"), Width, Height, 90, 30, 1);

        VideoTestClips.Timeline output = Export(
            source, new LibavExportOptions { MaxFps = 15, DropAudio = true }, new LibavClipExporter.SourceRange(0, 3));

        Assert.Equal(15, output.FrameRate.num);
        Assert.Equal(1, output.FrameRate.den);
        Assert.Equal(45, output.Frames);
        AssertWithinAFrame(3.0, output, 15);
    }

    [Fact]
    public void TwoPass_EncodesTheSameFramesBothPasses()
    {
        // x264's second pass fails if it is fed a different frame sequence from the first.
        string source = VideoTestClips.WriteY4m(Path.Combine(_dir, "twopass.y4m"), Width, Height, 90, 30000, 1001);
        double length = 90 * 1001 / 30000.0;

        VideoTestClips.Timeline output = Export(
            source,
            new LibavExportOptions { TargetVideoBitrateKbps = 200, TwoPass = true, DropAudio = true },
            new LibavClipExporter.SourceRange(0, length));

        Assert.Equal(90, output.Frames);
        AssertWithinAFrame(length, output, 30000 / 1001.0);
    }

    [Theory]
    [InlineData(30000, 1001, 30, 1, 30000, 1001)] // 29.97 fps stays exact
    [InlineData(0, 0, 25, 1, 25, 1)]              // no average: the base rate
    [InlineData(1000, 1, 1000, 1, 120, 1)]        // a millisecond time base taken for a rate: the old clamped whole number
    public void ChooseFrameRate_PrefersTheExactAverage(int avgNum, int avgDen, int baseNum, int baseDen, int num, int den)
    {
        AVRational rate = LibavClipExporter.ChooseFrameRate(
            new AVRational { num = avgNum, den = avgDen }, new AVRational { num = baseNum, den = baseDen }, roundedFallback: 120);

        Assert.Equal((num, den), (rate.num, rate.den));
    }

    [Theory]
    [InlineData(0, 30000, 1001)]  // no cap
    [InlineData(30, 30000, 1001)] // a cap at the rounded source rate is no cap
    [InlineData(24, 24, 1)]
    public void OutputFrameRate_CapsOnlyBelowTheSourceRate(int maxFps, int num, int den)
    {
        AVRational rate = LibavClipExporter.OutputFrameRate(new AVRational { num = 30000, den = 1001 }, maxFps);

        Assert.Equal((num, den), (rate.num, rate.den));
    }

    [Fact]
    public void AGapInTheSource_HoldsTheFrameBeforeIt()
    {
        var clock = new LibavClipExporter.OutputFrameClock(30);
        for (int i = 0; i < 10; i++)
        {
            clock.Arrive(i / 30.0, out _);
        }

        // The next frame comes a second later: the tenth frame shows until then, as a player would show it.
        Assert.Equal(21, clock.Arrive(1.0, out long firstPts));
        Assert.Equal(9, firstPts);
    }

    [Fact]
    public void MillisecondTimestamps_NeitherDropNorRepeatAFrame()
    {
        var clock = new LibavClipExporter.OutputFrameClock(30);
        clock.Arrive(0, out _);
        for (int i = 1; i < 300; i++)
        {
            Assert.Equal(1, clock.Arrive(Math.Round(i * 1000 / 30.0) / 1000, out _));
        }
    }

    private VideoTestClips.Timeline Export(string source, params LibavClipExporter.SourceRange[] ranges) =>
        Export(source, new LibavExportOptions { DropAudio = true }, ranges);

    private VideoTestClips.Timeline Export(
        string source, LibavExportOptions options, params LibavClipExporter.SourceRange[] ranges)
    {
        string output = Path.Combine(_dir, Path.GetFileNameWithoutExtension(source) + "_out.mp4");
        Assert.True(LibavClipExporter.TryExport(source, ranges, output, VideoQuality.Medium, options: options), "export failed");
        return VideoTestClips.Read(output);
    }

    private static void AssertWithinAFrame(double expectedSeconds, VideoTestClips.Timeline output, double framesPerSecond)
    {
        double frame = 1 / framesPerSecond;
        Assert.True(
            Math.Abs(output.DurationSeconds - expectedSeconds) <= frame + 1e-3,
            $"video lasts {output.DurationSeconds:F4} s, expected {expectedSeconds:F4} s within one frame ({frame:F4} s)");
    }
}
