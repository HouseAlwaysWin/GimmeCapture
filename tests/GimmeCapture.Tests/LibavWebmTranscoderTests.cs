using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
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

    [Fact]
    public void Progress_RisesWithTheSourceTime_AndEndsAtOne()
    {
        string source = VideoTestClips.WriteConstantRate(Path.Combine(_dir, "progress.mkv"), Width, Height, 90, 30);
        var reported = new List<double>();

        LibavWebmTranscoder.TranscodeToWebm(
            source, Path.Combine(_dir, "progress.webm"), 30, VideoQuality.Low, new InlineProgress(reported.Add));

        Assert.Equal(1.0, reported[^1]);
        Assert.True(reported.Zip(reported.Skip(1)).All(pair => pair.First < pair.Second), "progress went back or repeated itself");
        Assert.Contains(reported, value => value > 0.2 && value < 0.8);
    }

    [Fact]
    public void Cancelling_StopsTheTranscode_AndLeavesNoWebmBehind()
    {
        // It ran to the end whatever the caller's token said, so a cancelled compress came out as done.
        string source = VideoTestClips.WriteConstantRate(Path.Combine(_dir, "cancel.mkv"), Width, Height, 90, 30, detailed: true);
        string output = Path.Combine(_dir, "cancel.webm");
        using var cancellation = new CancellationTokenSource();
        bool startedWriting = false;
        var progress = new InlineProgress(value =>
        {
            if (value >= 0.5 && !cancellation.IsCancellationRequested)
            {
                startedWriting = File.Exists(output);
                cancellation.Cancel();
            }
        });

        Assert.ThrowsAny<OperationCanceledException>(() => LibavWebmTranscoder.TranscodeToWebm(
            source, output, 30, VideoQuality.Low, progress, cancellation.Token));

        Assert.True(startedWriting);
        Assert.False(File.Exists(output));
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

    // Reports on the transcoding thread itself, unlike Progress<T>, so a test can act at an exact point of it.
    private sealed class InlineProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }
}
