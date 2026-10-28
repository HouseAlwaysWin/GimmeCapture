using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using GimmeCapture.Services.Core.Media.NativeFFmpeg;
using Xunit;

namespace GimmeCapture.Tests;

/// <summary>
/// What <see cref="LibavVideoFramePlayer"/> hands its callers, decoded from a tiny raw-video clip written in-process
/// (YUV4MPEG2, so no encoder is involved). <see cref="LibavVideoFramePlayer.PlayFramesAsync"/>, which a playing pin
/// uses, gives each frame as exactly width × height × 4 bytes of BGRA straight from the scaler, without the copy into
/// a pooled array it used to make first; <see cref="LibavVideoFramePlayer.PlayAsync"/> still gives the same rented
/// array every time, for the compress editor and thumbnails. Needs only the bundled FFmpeg, which every CI job
/// provisions, so like <see cref="LibavAudioTranscoderTests"/> it runs everywhere.
/// </summary>
public sealed class LibavVideoFramePlayerFrameTests : IDisposable
{
    private const int ClipWidth = 64;
    private const int ClipHeight = 48;
    private const int FrameCount = 8;

    // Ten times real time: the eight frames of the 30 fps clip play in about 30 ms.
    private const double Speed = 10.0;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "GimmeCapture.Tests", Guid.NewGuid().ToString("N"));

    public LibavVideoFramePlayerFrameTests()
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
    [InlineData(ClipWidth, ClipHeight)]
    [InlineData(63, 47)] // odd: rows of 252 bytes, which a 32-byte row alignment would have padded
    public async Task PlayFramesAsync_GivesEachFrameAsExactlyItsPixels(int width, int height)
    {
        string clip = WriteClip();
        var frames = new List<(byte[] Pixels, double Seconds)>();

        using var player = new LibavVideoFramePlayer();
        await player.PlayFramesAsync(clip, width, height, 0, Speed, false,
            (bgra, seconds) => frames.Add((bgra.ToArray(), seconds)), CancellationToken.None);

        // A frame can be skipped if the machine stalls (the player drops frames it is late for), never the first.
        Assert.InRange(frames.Count, 1, FrameCount);
        Assert.Equal(0, frames[0].Seconds, 3);
        int previousGrey = -1;
        foreach ((byte[] pixels, double seconds) in frames)
        {
            Assert.Equal(width * height * 4, pixels.Length);
            int grey = AssertFlatGrey(pixels);
            Assert.True(grey > previousGrey, $"the frame at {seconds:0.000} s is not brighter than the one before it");
            previousGrey = grey;
        }
    }

    [Fact]
    public async Task PlayAsync_StillHandsOutOneRentedArray_HoldingTheSamePixels()
    {
        string clip = WriteClip();
        const int frameSize = ClipWidth * ClipHeight * 4;
        byte[]? array = null;
        bool alwaysTheSameArray = true;
        byte[]? firstFrame = null;

        using var player = new LibavVideoFramePlayer();
        await player.PlayAsync(clip, ClipWidth, ClipHeight, 0, Speed, false, (data, _) =>
        {
            alwaysTheSameArray &= array == null || ReferenceEquals(array, data);
            array ??= data;
            firstFrame ??= data.AsSpan(0, frameSize).ToArray();
        }, CancellationToken.None);

        byte[]? firstSpanFrame = null;
        await player.PlayFramesAsync(clip, ClipWidth, ClipHeight, 0, Speed, false,
            (bgra, _) => firstSpanFrame ??= bgra.ToArray(), CancellationToken.None);

        Assert.NotNull(array);
        Assert.True(alwaysTheSameArray);
        Assert.True(array!.Length >= frameSize);
        Assert.NotNull(firstSpanFrame);
        Assert.Equal(firstSpanFrame, firstFrame);
    }

    [Fact]
    public async Task DecodeFrameAtAsync_ReturnsJustTheFrame()
    {
        string clip = WriteClip();

        byte[]? frame = await LibavVideoFramePlayer.DecodeFrameAtAsync(clip, 0, ClipWidth, ClipHeight);

        Assert.NotNull(frame);
        Assert.Equal(ClipWidth * ClipHeight * 4, frame!.Length);
        AssertFlatGrey(frame);
    }

    // Frame i is one flat grey, brighter with each frame, so a frame's pixels say which frame it is.
    private static byte LumaOf(int frameIndex) => (byte)(40 + (frameIndex * 20));

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
            planes.AsSpan(0, lumaSize).Fill(LumaOf(i));
            planes.AsSpan(lumaSize).Fill(128); // no colour
            file.Write(frameHeader);
            file.Write(planes);
        }

        return path;
    }

    // Every pixel the same opaque grey (within rounding); returns that grey.
    private static int AssertFlatGrey(byte[] bgra)
    {
        int grey = bgra[0];
        for (int i = 0; i < bgra.Length; i += 4)
        {
            Assert.InRange(bgra[i], grey - 2, grey + 2);
            Assert.InRange(bgra[i + 1], grey - 2, grey + 2);
            Assert.InRange(bgra[i + 2], grey - 2, grey + 2);
            Assert.Equal(255, bgra[i + 3]);
        }

        return grey;
    }
}
