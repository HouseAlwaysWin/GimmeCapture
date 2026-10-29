using System;
using System.IO;
using System.Linq;
using FFmpeg.AutoGen;
using GimmeCapture.Models;
using GimmeCapture.Services.Core.Media.NativeFFmpeg;

namespace GimmeCapture.Tests;

/// <summary>
/// <see cref="LibavWebmTranscoder"/> keeps a recording's timing. A recording's frames carry the time they were
/// captured, with gaps where a frame was missed; numbering them one after another closed the gaps up, so the WebM's
/// video ran shorter than its audio. Transcodes tiny synthetic clips (<see cref="VideoTestClips"/>) with libvpx-vp9, so
/// it needs only the bundled FFmpeg and runs everywhere.
/// </summary>
public sealed class LibavWebmTranscoderTests : IDisposable
{
    private const int Width = 64;
    private const int Height = 48;
    private static readonly AVRational Ms = new() { num = 1, den = 1000 };
    private static readonly AVRational Fps30 = new() { num = 1, den = 30 };

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "GimmeCapture.Tests", Guid.NewGuid().ToString("N"));

    public LibavWebmTranscoderTests()
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
    public void ARecordingWithAGap_KeepsTheGapAndItsLength()
    {
        // A second of frames, a second the capture missed, then another second: three seconds, sixty frames.
        long[] pts = Enumerable.Range(0, 30).Concat(Enumerable.Range(60, 30)).Select(i => (long)i).ToArray();
        string source = VideoTestClips.WriteTimestamped(
            Path.Combine(_dir, "gap.mkv"), Width, Height, Fps30, pts, lastFrameDuration: 1, frameRate: new AVRational { num = 30, den = 1 });
        string output = Path.Combine(_dir, "gap.webm");

        LibavWebmTranscoder.TranscodeToWebm(source, output, 30, VideoQuality.Low);

        VideoTestClips.Timeline webm = VideoTestClips.Read(output);
        Assert.Equal(60, webm.Frames);
        Assert.Equal(89 / 30.0, webm.FrameSeconds[^1], 2); // the last frame where it was captured, not at 59/30
        Assert.Contains(webm.FrameSeconds.Zip(webm.FrameSeconds.Skip(1), (a, b) => b - a), step => step > 0.9);
    }

    [Fact]
    public void ASourceDenserThanTheOutputRate_DropsFrames_InsteadOfStretching()
    {
        // Two seconds at 60 fps into a 30 fps WebM: every other frame lands on a tick already taken and is dropped.
        string source = VideoTestClips.WriteConstantRate(Path.Combine(_dir, "dense.mkv"), Width, Height, 120, 60);
        string output = Path.Combine(_dir, "dense.webm");

        LibavWebmTranscoder.TranscodeToWebm(source, output, 30, VideoQuality.Low);

        VideoTestClips.Timeline webm = VideoTestClips.Read(output);
        Assert.InRange(webm.Frames, 60, 61);
        Assert.InRange(webm.FrameSeconds[^1], (119 / 60.0) - (1 / 30.0), (119 / 60.0) + (1 / 30.0));
    }

    [Theory]
    [InlineData(33L, 0L, 1L)]         // 30 fps stamped in milliseconds: one tick each, none dropped
    [InlineData(1033L, 30L, 31L)]
    [InlineData(2000L, 29L, 60L)]     // after a missed second the frame keeps its place
    [InlineData(17L, 1L, 1L)]         // a tick already used: not above the last, so dropped
    public void OutputPts_FollowsTheFramesOwnTime(long timestampMs, long lastPts, long expected)
    {
        Assert.Equal(expected, LibavWebmTranscoder.OutputPts(timestampMs, Ms, Fps30, startTimestamp: 0, lastPts));
    }

    [Fact]
    public void OutputPts_MeasuresFromTheInputsStart_AndFollowsAFrameWithoutATimestamp()
    {
        Assert.Equal(3, LibavWebmTranscoder.OutputPts(1500, Ms, Fps30, startTimestamp: 1400, lastPts: -1));
        Assert.Equal(0, LibavWebmTranscoder.OutputPts(1399, Ms, Fps30, startTimestamp: 1400, lastPts: -1));
        Assert.Equal(8, LibavWebmTranscoder.OutputPts(ffmpeg.AV_NOPTS_VALUE, Ms, Fps30, startTimestamp: 0, lastPts: 7));
    }

    [Theory]
    [InlineData(320, 0)]
    [InlineData(640, 1)]
    [InlineData(1000, 2)]
    [InlineData(1280, 2)]
    [InlineData(1920, 2)]
    [InlineData(3840, 3)]
    [InlineData(7680, 4)]
    public void Vp9TileColumns_KeepEveryTileAtLeast256PixelsWide(int width, int expectedLog2)
    {
        Assert.Equal(expectedLog2, LibavWebmTranscoder.Vp9TileColumnsLog2(width));
    }
}
