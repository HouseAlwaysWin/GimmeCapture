using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using GimmeCapture.Services.Core.Media;
using GimmeCapture.Services.Core.Media.NativeFFmpeg;
using Xunit;

namespace GimmeCapture.Tests;

/// <summary>
/// A video pin's loop in one decode (<see cref="LibavVideoFramePlayer.PlayFramesAsync"/> with a loop): pass after
/// pass from one open of the file, the time running on across each jump back, the frames shown by the sound's clock
/// when there is one. Each pin pass used to be a call of its own, which opened the file, the decoder and its GPU device
/// again. The clip is a tiny raw-video file written in-process, eight frames at 30 fps, so like
/// <see cref="LibavVideoFramePlayerFrameTests"/> these need only the bundled FFmpeg and run everywhere.
/// </summary>
public sealed class LibavVideoFramePlayerLoopTests : IDisposable
{
    private const int ClipWidth = 32;
    private const int ClipHeight = 16;
    private const int FrameCount = 8;
    private const double FrameSeconds = 1.0 / 30;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "GimmeCapture.Tests", Guid.NewGuid().ToString("N"));

    public LibavVideoFramePlayerLoopTests()
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
    public async Task TheWholeClip_PlaysPassAfterPass_InOneCall_ItsLoopEndingWhereThePictureDoes()
    {
        string clip = WriteClip();
        var frames = new List<int>();
        PlaybackLoop? heard = null;
        using var cts = new CancellationTokenSource(Timeout);

        using var player = new LibavVideoFramePlayer();
        await player.PlayFramesAsync(clip, ClipWidth, ClipHeight, 0, 10.0, false, (_, seconds) =>
            {
                frames.Add(FrameIndexAt(seconds));
                if (frames.Count == FrameCount * 4)
                {
                    cts.Cancel();
                }
            },
            cts.Token,
            loopRange: new PlaybackLoop(0, double.NaN),
            onLoopKnown: loop => heard = loop);

        Assert.Equal(FrameCount * 4, frames.Count);
        Assert.NotNull(heard);
        Assert.Equal(FrameCount * FrameSeconds, heard.Value.EndSeconds, 3);

        // The machine may stall and drop a frame it was late for; the passes still come one after another.
        int passes = 1;
        for (int i = 1; i < frames.Count; i++)
        {
            if (frames[i] < frames[i - 1])
            {
                passes++;
            }
        }

        Assert.True(passes >= 4, $"{passes} passes in {string.Join(",", frames)}");
    }

    [Fact]
    public async Task AStretch_LoopsJustThatStretch_TheFirstPassFromWhereThePlaybackBegan()
    {
        string clip = WriteClip();
        var frames = new List<int>();
        using var cts = new CancellationTokenSource(Timeout);

        using var player = new LibavVideoFramePlayer();
        await player.PlayFramesAsync(clip, ClipWidth, ClipHeight, 4 * FrameSeconds, 10.0, false, (_, seconds) =>
            {
                frames.Add(FrameIndexAt(seconds));
                if (frames.Count == 10)
                {
                    cts.Cancel();
                }
            },
            cts.Token,
            loopRange: new PlaybackLoop(2 * FrameSeconds, 6 * FrameSeconds));

        Assert.Equal(4, frames[0]);
        Assert.All(frames, frame => Assert.InRange(frame, 2, 5));
        Assert.Contains(2, frames);
    }

    [Fact]
    public async Task TheFrames_KeepToTheSoundsClock_RatherThanAStopwatch()
    {
        // A sound running at half speed: a frame shown by a stopwatch would come before the sound reached it.
        string clip = WriteClip();
        var soundClock = Stopwatch.StartNew();
        double SoundSeconds() => soundClock.Elapsed.TotalSeconds * 0.5;
        var shown = new List<(double Sound, double Due)>();
        double loopSeconds = FrameCount * FrameSeconds;
        int pass = 0;
        double previous = -1;
        using var cts = new CancellationTokenSource(Timeout);

        using var player = new LibavVideoFramePlayer();
        await player.PlayFramesAsync(clip, ClipWidth, ClipHeight, 0, 1.0, false, (_, seconds) =>
            {
                if (seconds < previous)
                {
                    pass++;
                }

                previous = seconds;
                shown.Add((SoundSeconds(), (pass * loopSeconds) + seconds));
                if (shown.Count == FrameCount + 3)
                {
                    cts.Cancel();
                }
            },
            cts.Token,
            loopRange: new PlaybackLoop(0, double.NaN),
            soundClock: () => SoundClockReading.At(SoundSeconds()));

        Assert.Equal(FrameCount + 3, shown.Count);
        Assert.Equal(1, pass);
        for (int i = 1; i < shown.Count; i++)
        {
            Assert.True(
                shown[i].Sound >= shown[i].Due - 0.002,
                $"frame {i}, due at {shown[i].Due:0.000} s, was shown with the sound at {shown[i].Sound:0.000} s");
        }
    }

    private static int FrameIndexAt(double seconds) => (int)Math.Round(seconds / FrameSeconds);

    // Eight flat grey frames, brighter with each one.
    private string WriteClip()
    {
        string path = Path.Combine(_dir, "clip.y4m");
        using FileStream file = File.Create(path);
        file.Write(Encoding.ASCII.GetBytes($"YUV4MPEG2 W{ClipWidth} H{ClipHeight} F30:1 Ip A1:1 C420jpeg\n"));

        int lumaSize = ClipWidth * ClipHeight;
        int chromaSize = (ClipWidth / 2) * (ClipHeight / 2);
        var planes = new byte[lumaSize + (2 * chromaSize)];
        byte[] frameHeader = Encoding.ASCII.GetBytes("FRAME\n");
        for (int i = 0; i < FrameCount; i++)
        {
            planes.AsSpan(0, lumaSize).Fill((byte)(40 + (i * 20)));
            planes.AsSpan(lumaSize).Fill(128); // no colour
            file.Write(frameHeader);
            file.Write(planes);
        }

        return path;
    }
}
