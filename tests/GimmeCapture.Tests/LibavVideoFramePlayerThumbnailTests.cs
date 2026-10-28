using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using GimmeCapture.Services.Core.Media.NativeFFmpeg;
using Xunit;

namespace GimmeCapture.Tests;

/// <summary>
/// <see cref="LibavVideoFramePlayer.ProbeWithThumbnailAsync"/>, which gives the compress queue a file's probe and its row
/// thumbnail from one open, on tiny raw-video clips written in-process (YUV4MPEG2, so no encoder is involved). Needs
/// only the bundled FFmpeg, which every CI job provisions, so like <see cref="LibavVideoFramePlayerFrameTests"/> it runs
/// everywhere.
/// </summary>
public sealed class LibavVideoFramePlayerThumbnailTests : IDisposable
{
    private const int FrameCount = 8;

    // The first frame's top-left quarter is dark and the rest of it bright; every later frame is mid grey. A thumbnail
    // with a dark top-left corner and bright other corners is the first frame, the right way up and round.
    private const byte Dark = 16;
    private const byte Bright = 235;
    private const byte Grey = 128;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "GimmeCapture.Tests", Guid.NewGuid().ToString("N"));

    public LibavVideoFramePlayerThumbnailTests()
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
    [InlineData(1920, 1080, 96, 96, 54)]
    [InlineData(1080, 1920, 96, 54, 96)] // portrait: the height is the side that fits
    [InlineData(64, 48, 96, 64, 48)]     // never enlarged
    [InlineData(63, 47, 96, 62, 46)]     // odd sides come down to even ones
    [InlineData(64, 48, 42, 42, 30)]     // and so does a side that scales to an odd size (31)
    [InlineData(5000, 10, 96, 96, 2)]    // never below 2
    public void ThumbnailSize_FitsTheLongerSide_KeepingTheShapeWithEvenSides(
        int width, int height, int maxEdge, int expectedWidth, int expectedHeight)
    {
        Assert.Equal((expectedWidth, expectedHeight), LibavVideoFramePlayer.ThumbnailSize(width, height, maxEdge));
    }

    [Theory]
    [InlineData(64, 48, 96, 64, 48)]
    [InlineData(64, 48, 32, 32, 24)]
    [InlineData(63, 47, 96, 62, 46)] // odd source: chroma planes of 32 × 24, scaled to even sides
    [InlineData(48, 64, 42, 30, 42)] // portrait, scaled to an odd width
    public async Task ProbeWithThumbnail_GivesTheProbe_AndTheFirstFrameAtThumbnailSize(
        int width, int height, int maxEdge, int expectedWidth, int expectedHeight)
    {
        string clip = WriteClip(width, height, FrameCount);
        using var player = new LibavVideoFramePlayer();

        (LibavVideoFramePlayer.MediaInfo info, LibavVideoFramePlayer.BgraThumbnail? thumbnail) =
            await player.ProbeWithThumbnailAsync(clip, maxEdge);

        Assert.Equal(await player.ProbeAsync(clip), info);
        Assert.Equal(width, info.Width);
        Assert.Equal(height, info.Height);
        Assert.Equal(30, info.Fps);
        Assert.False(info.HasAudio);

        Assert.NotNull(thumbnail);
        Assert.Equal(expectedWidth, thumbnail!.Width);
        Assert.Equal(expectedHeight, thumbnail.Height);
        Assert.Equal(expectedWidth * expectedHeight * 4, thumbnail.Bgra.Length);
        AssertFirstFrameCorners(thumbnail);

        // The pixels the player shows for the first frame at that size: the same scaling.
        byte[]? played = await LibavVideoFramePlayer.DecodeFrameAtAsync(clip, 0, expectedWidth, expectedHeight);
        Assert.Equal(played, thumbnail.Bgra);
    }

    [Fact]
    public async Task ProbeWithThumbnail_OfAFileWithNoVideo_GivesTheProbeWithoutAThumbnail()
    {
        string wav = WavTestAudio.WriteSineWav(Path.Combine(_dir, "tone.wav"), seconds: 0.5);
        using var player = new LibavVideoFramePlayer();

        (LibavVideoFramePlayer.MediaInfo info, LibavVideoFramePlayer.BgraThumbnail? thumbnail) =
            await player.ProbeWithThumbnailAsync(wav, 96);

        Assert.Null(thumbnail);
        Assert.Equal(await player.ProbeAsync(wav), info);
        Assert.Equal(0, info.Width);
        Assert.Equal(0, info.Height);
        Assert.True(info.HasAudio);
        Assert.Equal(2, info.AudioChannels);
        Assert.Equal(0.5, info.Duration, 2);
    }

    [Fact]
    public async Task ProbeWithThumbnail_OfAVideoWithNoFrames_StillGivesItsSize()
    {
        string empty = WriteClip(64, 48, frameCount: 0);
        using var player = new LibavVideoFramePlayer();

        (LibavVideoFramePlayer.MediaInfo info, LibavVideoFramePlayer.BgraThumbnail? thumbnail) =
            await player.ProbeWithThumbnailAsync(empty, 96);

        Assert.Null(thumbnail);
        Assert.Equal(64, info.Width);
        Assert.Equal(48, info.Height);
    }

    [Fact]
    public async Task ProbeWithThumbnail_Cancelled_Throws()
    {
        string clip = WriteClip(64, 48, FrameCount);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        using var player = new LibavVideoFramePlayer();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => player.ProbeWithThumbnailAsync(clip, 96, cancellation.Token));
    }

    [Fact]
    public async Task ProbeWithThumbnail_OfAFileLibavCannotOpen_Throws()
    {
        // The queue logs this as a failed probe, as it did when ProbeAsync threw.
        string notAVideo = Path.Combine(_dir, "a.mp4");
        File.WriteAllBytes(notAVideo, new byte[] { 0 });
        using var player = new LibavVideoFramePlayer();

        await Assert.ThrowsAsync<FFmpegNativeException>(() => player.ProbeWithThumbnailAsync(notAVideo, 96));
    }

    // A width × height 4:2:0 clip of frameCount frames: the first one patterned, the rest flat grey, all colourless.
    private string WriteClip(int width, int height, int frameCount)
    {
        string path = Path.Combine(_dir, $"clip-{width}x{height}-{frameCount}.y4m");
        using FileStream file = File.Create(path);
        file.Write(Encoding.ASCII.GetBytes($"YUV4MPEG2 W{width} H{height} F30:1 Ip A1:1 C420jpeg\n"));

        int lumaSize = width * height;
        int chromaSize = ((width + 1) / 2) * ((height + 1) / 2); // odd sides round their chroma up
        var planes = new byte[lumaSize + (2 * chromaSize)];
        byte[] frameHeader = Encoding.ASCII.GetBytes("FRAME\n");
        for (int i = 0; i < frameCount; i++)
        {
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    bool topLeft = x < width / 2 && y < height / 2;
                    planes[(y * width) + x] = i > 0 ? Grey : topLeft ? Dark : Bright;
                }
            }

            planes.AsSpan(lumaSize).Fill(128); // no colour
            file.Write(frameHeader);
            file.Write(planes);
        }

        return path;
    }

    private static void AssertFirstFrameCorners(LibavVideoFramePlayer.BgraThumbnail thumbnail)
    {
        int right = thumbnail.Width - 1;
        int bottom = thumbnail.Height - 1;
        Assert.InRange(GreyAt(thumbnail, 0, 0), 0, 63);
        Assert.InRange(GreyAt(thumbnail, right, 0), 192, 255);
        Assert.InRange(GreyAt(thumbnail, 0, bottom), 192, 255);
        Assert.InRange(GreyAt(thumbnail, right, bottom), 192, 255);
    }

    // The pixel's grey level; asserts it is opaque and colourless.
    private static int GreyAt(LibavVideoFramePlayer.BgraThumbnail thumbnail, int x, int y)
    {
        int i = ((y * thumbnail.Width) + x) * 4;
        byte b = thumbnail.Bgra[i];
        Assert.InRange(thumbnail.Bgra[i + 1], b - 2, b + 2);
        Assert.InRange(thumbnail.Bgra[i + 2], b - 2, b + 2);
        Assert.Equal(255, thumbnail.Bgra[i + 3]);
        return b;
    }
}
