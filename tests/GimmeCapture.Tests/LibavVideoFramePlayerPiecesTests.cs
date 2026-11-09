using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GimmeCapture.Services.Core.Media;
using GimmeCapture.Services.Core.Media.NativeFFmpeg;
using Xunit;

namespace GimmeCapture.Tests;

/// <summary>
/// The editor's preview of a cut video in one decode (<see cref="LibavVideoFramePlayer.PlayPiecesAsync"/>): each piece
/// used to be a call of its own, which opened the file, the decoder and its GPU device again, and on a 4K phone video
/// held the picture for 350-530 ms at every cut. The clip is a raw-video file written in-process, so these need only
/// the bundled FFmpeg and run everywhere. A raw video has no index, so a piece more than a second on is reached by a
/// seek and a nearer one by decoding on.
/// </summary>
public sealed class LibavVideoFramePlayerPiecesTests : IDisposable
{
    private const int ClipWidth = 32;
    private const int ClipHeight = 16;
    private const int FrameCount = 60;
    private const int Fps = 30;
    private const double FrameSeconds = 1.0 / Fps;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "GimmeCapture.Tests", Guid.NewGuid().ToString("N"));

    public LibavVideoFramePlayerPiecesTests()
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

    [Theory]
    [InlineData(10)] // a short way on, decoded on to
    [InlineData(50)] // too far for that, a seek
    public async Task ThePieces_PlayOneAfterTheOther_InOneCall_WhatLiesBetweenLeftOut(int secondPieceFrame)
    {
        string clip = WriteClip();
        PlaybackPiece first = Piece(2, 6);
        PlaybackPiece second = Piece(secondPieceFrame, secondPieceFrame + 4);
        var frames = new List<int>();
        var started = new List<PlaybackPiece>();
        using var cts = new CancellationTokenSource(Timeout);

        using var player = new LibavVideoFramePlayer();
        await player.PlayPiecesAsync(
            clip, ClipWidth, ClipHeight, new PlaybackPieces([first, second], 0, first.StartSeconds, () => false),
            (_, seconds) => frames.Add(FrameIndexAt(seconds)), cts.Token, started.Add);

        Assert.False(cts.IsCancellationRequested, "the call did not end after the last piece");
        Assert.Equal([first, second], started);

        // A machine that stalls drops a frame it was late for, but never the first of a piece, and shows nothing else.
        Assert.Equal(2, frames[0]);
        Assert.Contains(secondPieceFrame, frames);
        Assert.Equal(frames.Order(), frames);
        Assert.All(frames, frame => Assert.True(
            (frame >= 2 && frame < 6) || (frame >= secondPieceFrame && frame < secondPieceFrame + 4), $"frame {frame} of {string.Join(",", frames)}"));
    }

    [Fact]
    public async Task ThePlayback_BeginsPartwayThroughALaterPiece()
    {
        string clip = WriteClip();
        var frames = new List<int>();
        var started = new List<PlaybackPiece>();
        using var cts = new CancellationTokenSource(Timeout);

        using var player = new LibavVideoFramePlayer();
        await player.PlayPiecesAsync(
            clip, ClipWidth, ClipHeight, new PlaybackPieces([Piece(2, 6), Piece(10, 16)], 1, 12 * FrameSeconds, () => false),
            (_, seconds) => frames.Add(FrameIndexAt(seconds)), cts.Token, started.Add);

        Assert.Equal(12, frames[0]);
        Assert.All(frames, frame => Assert.InRange(frame, 12, 15));
        Assert.Equal([Piece(12, 16)], started);
    }

    [Fact]
    public async Task ARound_BeginsAgainAtTheFirstPiecesStart()
    {
        string clip = WriteClip();
        int asked = 0;
        var frames = new List<int>();
        using var cts = new CancellationTokenSource(Timeout);

        using var player = new LibavVideoFramePlayer();
        await player.PlayPiecesAsync(
            clip, ClipWidth, ClipHeight, new PlaybackPieces([Piece(2, 6), Piece(40, 44)], 1, 41 * FrameSeconds, () => ++asked == 1),
            (_, seconds) => frames.Add(FrameIndexAt(seconds)), cts.Token);

        Assert.False(cts.IsCancellationRequested, "the call did not end after the second round");
        Assert.Equal(2, asked);
        Assert.Equal(41, frames[0]);
        int roundBegins = frames.IndexOf(2);
        Assert.True(roundBegins > 0, $"no second round in {string.Join(",", frames)}");
        Assert.Contains(40, frames.Skip(roundBegins));
    }

    [Fact]
    public async Task PiecesPastTheEndOfThePicture_EndTheRounds_ThoughMoreAreWanted()
    {
        // An encoded file: a seek past the end of a raw one fails, and a seek in this one lands on its last keyframe.
        string clip = VideoTestClips.WriteConstantRate(Path.Combine(_dir, "clip.mp4"), 64, 48, FrameCount, Fps, gopSize: 10);
        int asked = 0;
        var frames = new List<int>();
        using var cts = new CancellationTokenSource(Timeout);

        using var player = new LibavVideoFramePlayer();
        await player.PlayPiecesAsync(
            clip, 64, 48, new PlaybackPieces([Piece(FrameCount + 10, FrameCount + 20)], 0, (FrameCount + 10) * FrameSeconds, () =>
            {
                asked++;
                return true;
            }),
            (_, seconds) => frames.Add(FrameIndexAt(seconds)), cts.Token);

        Assert.False(cts.IsCancellationRequested, "the rounds went on until the timeout");
        Assert.Empty(frames);
        Assert.Equal(2, asked);
    }

    [Fact]
    public async Task ThePieceBefore_HasItsLastFramesTime_BeforeTheNextShows()
    {
        // The next piece's first frame is decoded within a millisecond here. A frame lasts 33 ms, and without the wait
        // the last frame of each piece would be gone at once. Lateness can shorten what one cut shows, never all of them.
        string clip = WriteClip();
        PlaybackPiece[] pieces = Enumerable.Range(0, 5).Select(i => Piece(2 + (i * 6), 5 + (i * 6), speed: 1.0)).ToArray();
        var shown = new List<(int Frame, double At)>();
        var clock = Stopwatch.StartNew();
        using var cts = new CancellationTokenSource(Timeout);

        using var player = new LibavVideoFramePlayer();
        await player.PlayPiecesAsync(
            clip, ClipWidth, ClipHeight, new PlaybackPieces(pieces, 0, pieces[0].StartSeconds, () => false),
            (_, seconds) => shown.Add((FrameIndexAt(seconds), clock.Elapsed.TotalSeconds)), cts.Token);

        List<double> atCuts = shown.Zip(shown.Skip(1)).Where(p => p.Second.Frame - p.First.Frame > 1).Select(p => p.Second.At - p.First.At).ToList();
        Assert.NotEmpty(atCuts);
        Assert.True(atCuts.Max() >= 0.025, $"seconds at the cuts: {string.Join(", ", atCuts)}");
    }

    [Fact]
    public async Task EachPiece_PlaysAtItsOwnSpeed()
    {
        string clip = WriteClip();
        PlaybackPiece fast = Piece(2, 6, speed: 10.0);
        PlaybackPiece slow = Piece(10, 14, speed: 0.5);
        var shown = new List<(int Frame, double At)>();
        var clock = Stopwatch.StartNew();
        using var cts = new CancellationTokenSource(Timeout);

        using var player = new LibavVideoFramePlayer();
        await player.PlayPiecesAsync(
            clip, ClipWidth, ClipHeight, new PlaybackPieces([fast, slow], 0, fast.StartSeconds, () => false),
            (_, seconds) => shown.Add((FrameIndexAt(seconds), clock.Elapsed.TotalSeconds)), cts.Token);

        // Three frames of the slow piece, at twice each frame's time. A late machine only adds to that.
        double slowBegan = shown.First(s => s.Frame == 10).At;
        double slowEnded = shown.Last(s => s.Frame >= 10).At;
        int framesPlayed = shown.Last(s => s.Frame >= 10).Frame - 10;
        Assert.True(framesPlayed >= 1, string.Join(",", shown.Select(s => s.Frame)));
        Assert.True(slowEnded - slowBegan >= (framesPlayed * FrameSeconds / 0.5) - 0.01, $"{slowEnded - slowBegan:F3} s for {framesPlayed} frames");
    }

    // A piece of the clip by its frames, played fast so the tests that don't time it take no time.
    private static PlaybackPiece Piece(int firstFrame, int endFrame, double speed = 10.0) =>
        new(firstFrame * FrameSeconds, endFrame * FrameSeconds, speed);

    private static int FrameIndexAt(double seconds) => (int)Math.Round(seconds * Fps);

    private string WriteClip() => VideoTestClips.WriteY4m(Path.Combine(_dir, "clip.y4m"), ClipWidth, ClipHeight, FrameCount, Fps, 1);
}
