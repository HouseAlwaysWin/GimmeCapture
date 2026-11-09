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
/// The editor's preview (<see cref="LibavStillFrameDecoder.PlayPiecesAsync"/>): the pieces of a cut video, in the one decode
/// that also gives the paused frames. Each piece used to open the file, the decoder and its GPU device again, which held
/// the picture for 350-530 ms at every cut of a 4K phone video, and pressing Play took 210-470 ms for the same reason. The
/// clips are written in-process, so these need only the bundled FFmpeg and run everywhere. A raw video has no index, so a
/// piece more than a second on is reached by a seek and a nearer one by decoding on.
/// </summary>
public sealed class LibavStillFrameDecoderPiecesTests : IDisposable
{
    private const int RawWidth = 32;
    private const int RawHeight = 16;
    private const int RawFrames = 60;
    private const int Width = 64;
    private const int Height = 48;
    private const int EncodedFrames = 30; // a grey of its own for each
    private const int Fps = 30;
    private const double FrameSeconds = 1.0 / Fps;

    // What one step of the clips' grey (7 levels of luma) comes to in BGRA.
    private const double GreyStep = 7 * 255.0 / 219;

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "GimmeCapture.Tests", Guid.NewGuid().ToString("N"));

    public LibavStillFrameDecoderPiecesTests()
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
        PlaybackPiece first = Piece(2, 6);
        PlaybackPiece second = Piece(secondPieceFrame, secondPieceFrame + 4);
        var frames = new List<int>();
        var started = new List<PlaybackPiece>();
        using var cts = new CancellationTokenSource(Timeout);

        using var decoder = new LibavStillFrameDecoder(WriteRawClip(), RawWidth, RawHeight);
        await decoder.PlayPiecesAsync(
            new PlaybackPieces([first, second], 0, first.StartSeconds, () => false),
            (_, seconds) => frames.Add(FrameIndexAt(seconds)), cts.Token, Noting(started));

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
        var frames = new List<int>();
        var started = new List<PlaybackPiece>();
        using var cts = new CancellationTokenSource(Timeout);

        using var decoder = new LibavStillFrameDecoder(WriteRawClip(), RawWidth, RawHeight);
        await decoder.PlayPiecesAsync(
            new PlaybackPieces([Piece(2, 6), Piece(10, 16)], 1, 12 * FrameSeconds, () => false),
            (_, seconds) => frames.Add(FrameIndexAt(seconds)), cts.Token, Noting(started));

        Assert.Equal(12, frames[0]);
        Assert.All(frames, frame => Assert.InRange(frame, 12, 15));
        Assert.Equal([Piece(12, 16)], started);
    }

    [Fact]
    public async Task ARound_BeginsAgainAtTheFirstPiecesStart()
    {
        int asked = 0;
        var frames = new List<int>();
        using var cts = new CancellationTokenSource(Timeout);

        using var decoder = new LibavStillFrameDecoder(WriteRawClip(), RawWidth, RawHeight);
        await decoder.PlayPiecesAsync(
            new PlaybackPieces([Piece(2, 6), Piece(40, 44)], 1, 41 * FrameSeconds, () => ++asked == 1),
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
        int asked = 0;
        var frames = new List<int>();
        using var cts = new CancellationTokenSource(Timeout);

        using var decoder = new LibavStillFrameDecoder(WriteEncodedClip(), Width, Height);
        await decoder.PlayPiecesAsync(
            new PlaybackPieces([Piece(EncodedFrames + 40, EncodedFrames + 50)], 0, (EncodedFrames + 40) * FrameSeconds, () =>
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
        PlaybackPiece[] pieces = Enumerable.Range(0, 5).Select(i => Piece(2 + (i * 6), 5 + (i * 6), speed: 1.0)).ToArray();
        var shown = new List<(int Frame, double At)>();
        var clock = Stopwatch.StartNew();
        using var cts = new CancellationTokenSource(Timeout);

        using var decoder = new LibavStillFrameDecoder(WriteRawClip(), RawWidth, RawHeight);
        await decoder.PlayPiecesAsync(
            new PlaybackPieces(pieces, 0, pieces[0].StartSeconds, () => false),
            (_, seconds) => shown.Add((FrameIndexAt(seconds), clock.Elapsed.TotalSeconds)), cts.Token);

        List<double> atCuts = shown.Zip(shown.Skip(1)).Where(p => p.Second.Frame - p.First.Frame > 1).Select(p => p.Second.At - p.First.At).ToList();
        Assert.NotEmpty(atCuts);
        Assert.True(atCuts.Max() >= 0.025, $"seconds at the cuts: {string.Join(", ", atCuts)}");
    }

    [Fact]
    public async Task EachPiece_PlaysAtItsOwnSpeed()
    {
        PlaybackPiece fast = Piece(2, 6, speed: 10.0);
        PlaybackPiece slow = Piece(10, 14, speed: 0.5);
        var shown = new List<(int Frame, double At)>();
        var clock = Stopwatch.StartNew();
        using var cts = new CancellationTokenSource(Timeout);

        using var decoder = new LibavStillFrameDecoder(WriteRawClip(), RawWidth, RawHeight);
        await decoder.PlayPiecesAsync(
            new PlaybackPieces([fast, slow], 0, fast.StartSeconds, () => false),
            (_, seconds) => shown.Add((FrameIndexAt(seconds), clock.Elapsed.TotalSeconds)), cts.Token);

        // Three frames of the slow piece, at twice each frame's time. A late machine only adds to that.
        double slowBegan = shown.First(s => s.Frame == 10).At;
        double slowEnded = shown.Last(s => s.Frame >= 10).At;
        int framesPlayed = shown.Last(s => s.Frame >= 10).Frame - 10;
        Assert.True(framesPlayed >= 1, string.Join(",", shown.Select(s => s.Frame)));
        Assert.True(slowEnded - slowBegan >= (framesPlayed * FrameSeconds / 0.5) - 0.01, $"{slowEnded - slowBegan:F3} s for {framesPlayed} frames");
    }

    [Fact]
    public async Task APlayback_GoesOnFromTheFrameOnTheScreen_WithoutOpeningOrSeeking()
    {
        using var decoder = new LibavStillFrameDecoder(WriteEncodedClip(), Width, Height);
        await decoder.DecodeFrameAtAsync(13 * FrameSeconds);
        int seeks = decoder.Seeks;
        var frames = new List<int>();
        using var cts = new CancellationTokenSource(Timeout);

        await decoder.PlayPiecesAsync(
            new PlaybackPieces([Piece(0, EncodedFrames)], 0, 13 * FrameSeconds, () => false),
            (_, seconds) => frames.Add(FrameIndexAt(seconds)), cts.Token);

        Assert.Equal(1, decoder.Opens);
        Assert.Equal(seeks, decoder.Seeks);
        Assert.Equal(14, frames[0]);
        Assert.Equal(frames.Order(), frames);
    }

    [Fact]
    public async Task APlaybackFromElsewhere_SeeksThere_AndBeginsAtThatFrame()
    {
        using var decoder = new LibavStillFrameDecoder(WriteEncodedClip(), Width, Height);
        await decoder.DecodeFrameAtAsync(13 * FrameSeconds);
        int seeks = decoder.Seeks;
        var frames = new List<int>();
        using var cts = new CancellationTokenSource(Timeout);

        await decoder.PlayPiecesAsync(
            new PlaybackPieces([Piece(0, EncodedFrames)], 0, 4 * FrameSeconds, () => false),
            (_, seconds) => frames.Add(FrameIndexAt(seconds)), cts.Token);

        Assert.Equal(1, decoder.Opens);
        Assert.Equal(seeks + 1, decoder.Seeks);
        Assert.Equal(4, frames[0]);
    }

    [Fact]
    public async Task APausedPlayback_GoesOnFromWhereItStopped_WithoutSeeking()
    {
        using var decoder = new LibavStillFrameDecoder(WriteEncodedClip(), Width, Height);
        using var pause = new CancellationTokenSource(Timeout);
        await decoder.PlayPiecesAsync(
            new PlaybackPieces([Piece(0, EncodedFrames)], 0, 0, () => false),
            (_, seconds) =>
            {
                if (FrameIndexAt(seconds) >= 8)
                {
                    pause.Cancel();
                }
            },
            pause.Token);
        int seeks = decoder.Seeks;
        var frames = new List<int>();
        using var cts = new CancellationTokenSource(Timeout);

        await decoder.PlayPiecesAsync(
            new PlaybackPieces([Piece(0, EncodedFrames)], 0, 8 * FrameSeconds, () => false),
            (_, seconds) => frames.Add(FrameIndexAt(seconds)), cts.Token);

        Assert.Equal(1, decoder.Opens);
        Assert.Equal(seeks, decoder.Seeks);
        Assert.InRange(frames[0], 9, 11);
    }

    [Fact]
    public async Task AFrameAskedForAfterAPlayback_IsTheExactOne_EitherWay()
    {
        using var decoder = new LibavStillFrameDecoder(WriteEncodedClip(), Width, Height);
        using var pause = new CancellationTokenSource(Timeout);
        await decoder.PlayPiecesAsync(
            new PlaybackPieces([Piece(0, EncodedFrames)], 0, 0, () => false),
            (_, seconds) =>
            {
                if (FrameIndexAt(seconds) >= 12)
                {
                    pause.Cancel();
                }
            },
            pause.Token);

        Assert.Equal(20, FrameIndexOf(await decoder.DecodeFrameAtAsync(20 * FrameSeconds)));
        Assert.Equal(21, FrameIndexOf(await decoder.DecodeFrameAtAsync(21 * FrameSeconds)));
        Assert.Equal(5, FrameIndexOf(await decoder.DecodeFrameAtAsync(5 * FrameSeconds)));
        Assert.Equal(1, decoder.Opens);
    }

    [Fact]
    public async Task AFrameAfterAPlaybackToTheEnd_IsTheExactOne_AndThePlaybackAfterThatToo()
    {
        using var decoder = new LibavStillFrameDecoder(WriteEncodedClip(), Width, Height);
        using var cts = new CancellationTokenSource(Timeout);
        await decoder.PlayPiecesAsync(
            new PlaybackPieces([Piece(20, EncodedFrames + 10)], 0, 20 * FrameSeconds, () => false),
            (_, _) => { }, cts.Token);
        Assert.False(cts.IsCancellationRequested);

        Assert.Equal(3, FrameIndexOf(await decoder.DecodeFrameAtAsync(3 * FrameSeconds)));

        // Asked for a frame past the last, the decoder has seen the end of the file; a playback starts it over.
        Assert.Null(await decoder.DecodeFrameAtAsync((EncodedFrames + 5) * FrameSeconds));
        var frames = new List<int>();
        await decoder.PlayPiecesAsync(
            new PlaybackPieces([Piece(25, EncodedFrames)], 0, 25 * FrameSeconds, () => false),
            (_, seconds) => frames.Add(FrameIndexAt(seconds)), cts.Token);
        Assert.Equal(25, frames[0]);
    }

    [Fact]
    public async Task AFrameAskedForDuringAPlayback_WaitsForItsEnd()
    {
        using var decoder = new LibavStillFrameDecoder(WriteEncodedClip(), Width, Height);
        var firstShown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool piecesOver = false;
        using var cts = new CancellationTokenSource(Timeout);
        Task playback = decoder.PlayPiecesAsync(
            new PlaybackPieces([Piece(2, 12, speed: 1.0)], 0, 2 * FrameSeconds, () =>
            {
                Volatile.Write(ref piecesOver, true);
                return false;
            }),
            (_, _) => firstShown.TrySetResult(), cts.Token);
        await firstShown.Task.WaitAsync(Timeout);

        byte[]? frame = await decoder.DecodeFrameAtAsync(20 * FrameSeconds).WaitAsync(Timeout);

        Assert.True(Volatile.Read(ref piecesOver), "the frame came during the playback");
        Assert.Equal(20, FrameIndexOf(frame));
        await playback.WaitAsync(Timeout);
    }

    [Fact]
    public async Task AfterItsFirstFrame_APiece_WaitsForItsSoundToStart()
    {
        // A sound that takes 150 ms to open, then plays. The first frame needs no sound to show.
        var sound = new Stopwatch();
        const double Opening = 0.15;
        SoundClockReading Reading() => sound.Elapsed.TotalSeconds < Opening
            ? SoundClockReading.Pending
            : SoundClockReading.At(sound.Elapsed.TotalSeconds - Opening + 0.001);
        var shown = new List<double>();
        var clock = Stopwatch.StartNew();
        using var cts = new CancellationTokenSource(Timeout);

        using var decoder = new LibavStillFrameDecoder(WriteRawClip(), RawWidth, RawHeight);
        await decoder.PlayPiecesAsync(
            new PlaybackPieces([Piece(4, 9, speed: 1.0)], 0, 4 * FrameSeconds, () => false),
            (_, _) => shown.Add(clock.Elapsed.TotalSeconds),
            cts.Token,
            _ =>
            {
                sound.Restart();
                return Reading;
            });

        Assert.True(shown.Count >= 2, $"{shown.Count} frames");
        Assert.True(shown[1] - shown[0] >= Opening - 0.05, $"the second frame came {shown[1] - shown[0]:F3} s after the first");
    }

    [Fact]
    public async Task ADecoderDisposedDuringAPlayback_LetsGoOfTheFileWhenItEnds()
    {
        var decoder = new LibavStillFrameDecoder(WriteEncodedClip(), Width, Height);
        var firstShown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cts = new CancellationTokenSource();
        Task playback = decoder.PlayPiecesAsync(
            new PlaybackPieces([Piece(0, EncodedFrames, speed: 0.5)], 0, 0, () => false),
            (_, _) => firstShown.TrySetResult(), cts.Token);
        await firstShown.Task.WaitAsync(Timeout);

        decoder.Dispose();
        Assert.True(decoder.IsOpen); // the playback still has it
        cts.Cancel();
        await playback.WaitAsync(Timeout);

        Assert.False(decoder.IsOpen);
        Assert.Null(await decoder.DecodeFrameAtAsync(0.1));
    }

    // A piece of the clip by its frames, played fast so the tests that don't time it take no time.
    private static PlaybackPiece Piece(int firstFrame, int endFrame, double speed = 10.0) =>
        new(firstFrame * FrameSeconds, endFrame * FrameSeconds, speed);

    private static Func<PlaybackPiece, Func<SoundClockReading>?> Noting(List<PlaybackPiece> started) => piece =>
    {
        started.Add(piece);
        return null;
    };

    private static int FrameIndexAt(double seconds) => (int)Math.Round(seconds * Fps);

    // Which frame of an encoded clip a picture is: its grey says.
    private static int? FrameIndexOf(byte[]? bgra)
    {
        if (bgra == null)
        {
            return null;
        }

        int centre = ((Height / 2 * Width) + (Width / 2)) * 4;
        return (int)Math.Round(bgra[centre] / GreyStep);
    }

    private string WriteRawClip() => VideoTestClips.WriteY4m(Path.Combine(_dir, "clip.y4m"), RawWidth, RawHeight, RawFrames, Fps, 1);

    private string WriteEncodedClip() => VideoTestClips.WriteConstantRate(Path.Combine(_dir, "clip.mp4"), Width, Height, EncodedFrames, Fps, gopSize: 10);
}
