using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using GimmeCapture.Services.Core.Media.NativeFFmpeg;
using SkiaSharp;
using Xunit;

namespace GimmeCapture.Tests;

/// <summary>
/// <see cref="LibavGifTranscoder.TranscodeToGif"/> on clips written in-process (YUV4MPEG2, so no encoder is involved).
/// The palette path reads the clip twice, palettegen first and paletteuse second, instead of holding every frame in
/// memory until palettegen's palette came out at the end. Needs only the bundled FFmpeg, which every CI job provisions,
/// so like <see cref="LibavVideoFramePlayerFrameTests"/> it runs everywhere.
/// </summary>
public sealed class LibavGifTranscoderTests : IDisposable
{
    // Four seconds at 30 fps, made into a 15 fps GIF: the timeline keeps every other frame.
    private const int ClipFrames = 120;
    private const int GifFps = 15;
    private const int GifFrames = 60;

    // The kept (even) frames of the flat clip cycle through these greys, too far apart for dithering to mix them. The
    // dropped (odd) frames are black, so a black GIF frame would be one the timeline should have dropped.
    private static readonly byte[] KeptLumas = [40, 75, 110, 145, 180, 215];
    private const byte DroppedLuma = 16;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "GimmeCapture.Tests", Guid.NewGuid().ToString("N"));

    public LibavGifTranscoderTests()
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
    public void AMultiSecondClip_GivesOneFramePerKeptFrame_InThePaletteMadeFromIt()
    {
        string clip = WriteFlatClip();
        string gif = Path.Combine(_dir, "flat.gif");

        LibavGifTranscoder.TranscodeToGif(clip, gif, GifFps, maxWidth: 0);

        using SKCodec codec = OpenGif(gif);
        Assert.Equal(GifFrames, codec.FrameCount);
        var colors = new HashSet<uint>();
        for (int k = 0; k < GifFrames; k++)
        {
            byte[] bgra = DecodeFrame(codec, k);
            int expected = RgbOfLuma(KeptLumas[k % KeptLumas.Length]);

            // Each frame comes back as its own grey, within rounding: the palette holds the clip's own colours. The
            // fixed RGB8 fallback palette has only four levels of blue, so it would dither these greys instead.
            Assert.True(MaxDeviation(bgra, expected) <= 3, $"frame {k} is not the grey {expected} of the frame it was made from");
            for (int p = 0; p < bgra.Length; p += 4)
            {
                colors.Add(BitConverter.ToUInt32(bgra, p));
            }
        }

        Assert.InRange(colors.Count, 1, 256);
    }

    [Fact]
    public void Progress_RisesThroughBothPasses_AndEndsAtOne()
    {
        string clip = WriteFlatClip();
        var reported = new List<double>();

        LibavGifTranscoder.TranscodeToGif(
            clip, Path.Combine(_dir, "flat.gif"), GifFps, maxWidth: 0, progress: new InlineProgress(reported.Add));

        Assert.Equal(1.0, reported[^1]);
        Assert.True(reported.Zip(reported.Skip(1)).All(pair => pair.First < pair.Second), "progress went back or repeated itself");
        Assert.Contains(reported, value => value > 0 && value < LibavGifTranscoder.PalettePassShare);
        Assert.Contains(reported, value => value > LibavGifTranscoder.PalettePassShare && value < 1);
    }

    [Theory]
    [InlineData(false)] // halfway through the palette pass, which writes nothing
    [InlineData(true)]  // halfway through the encode pass, with the GIF half written
    public void Cancelling_StopsTheExport_AndLeavesNoGifBehind(bool duringEncodePass)
    {
        string clip = WriteNoiseClip();
        string gif = Path.Combine(_dir, "cancelled.gif");
        double cancelAt = duringEncodePass
            ? LibavGifTranscoder.PalettePassShare + ((1 - LibavGifTranscoder.PalettePassShare) / 2)
            : LibavGifTranscoder.PalettePassShare / 2;
        using var cancellation = new CancellationTokenSource();
        long gifBytesWhenCancelled = -1;
        var progress = new InlineProgress(value =>
        {
            if (value >= cancelAt && !cancellation.IsCancellationRequested)
            {
                gifBytesWhenCancelled = File.Exists(gif) ? new FileInfo(gif).Length : -1;
                cancellation.Cancel();
            }
        });

        Assert.ThrowsAny<OperationCanceledException>(() => LibavGifTranscoder.TranscodeToGif(
            clip, gif, GifFps, maxWidth: 0, progress: progress, cancellationToken: cancellation.Token));

        Assert.True(cancellation.IsCancellationRequested);
        if (duringEncodePass)
        {
            // Frames reach the file as they are mapped, rather than all of them once the clip has been read.
            Assert.True(gifBytesWhenCancelled > 0, $"halfway through the encode the GIF had {gifBytesWhenCancelled} bytes");
        }
        else
        {
            Assert.Equal(-1, gifBytesWhenCancelled);
        }

        Assert.False(File.Exists(gif));
    }

    [Fact]
    public void WhenThePaletteFiltersCannotBeSetUp_TheFixedPaletteStillMakesTheGif()
    {
        string clip = WriteFlatClip();
        string gif = Path.Combine(_dir, "fallback.gif");

        LibavGifTranscoder.TranscodeToGif(clip, gif, GifFps, maxWidth: 0, paletteuseArgs: "dither=no_such_dither");

        using SKCodec codec = OpenGif(gif);
        Assert.Equal(GifFrames, codec.FrameCount);
        int expected = RgbOfLuma(KeptLumas[0]);
        Assert.True(MaxDeviation(DecodeFrame(codec, 0), expected) > 3, "the first frame kept its exact grey, so no fallback ran");
    }

    // The flat clip: every frame one grey (see KeptLumas), 96 × 64.
    private string WriteFlatClip() => WriteClip(
        "flat.y4m", 96, 64, (i, _, _) => i % 2 == 0 ? KeptLumas[(i / 2) % KeptLumas.Length] : DroppedLuma);

    // Random grey noise in 16 levels, 320 × 240, the same every run. It barely compresses, so the GIF is megabytes and
    // gets past the output's write buffer onto the disk long before the export ends.
    private string WriteNoiseClip()
    {
        var random = new Random(20261029);
        return WriteClip("noise.y4m", 320, 240, (_, _, _) => (byte)(20 + (14 * random.Next(16))));
    }

    // A ClipFrames-long 4:2:0 clip at 30 fps without colour; frame i's luma at (x, y) is lumaAt(i, x, y).
    private string WriteClip(string name, int width, int height, Func<int, int, int, byte> lumaAt)
    {
        string path = Path.Combine(_dir, name);
        using FileStream file = File.Create(path);
        file.Write(Encoding.ASCII.GetBytes($"YUV4MPEG2 W{width} H{height} F30:1 Ip A1:1 C420jpeg\n"));

        int lumaSize = width * height;
        var planes = new byte[lumaSize + (2 * (width / 2) * (height / 2))];
        planes.AsSpan(lumaSize).Fill(128); // no colour
        byte[] frameHeader = Encoding.ASCII.GetBytes("FRAME\n");
        for (int i = 0; i < ClipFrames; i++)
        {
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    planes[(y * width) + x] = lumaAt(i, x, y);
                }
            }

            file.Write(frameHeader);
            file.Write(planes);
        }

        return path;
    }

    // The grey a video-range luma decodes to.
    private static int RgbOfLuma(byte luma) => (int)Math.Round((luma - 16) * 255.0 / 219.0);

    private static SKCodec OpenGif(string path)
    {
        SKCodec? codec = SKCodec.Create(path);
        Assert.NotNull(codec);
        return codec!;
    }

    // Frame index of the GIF as a viewer shows it, built on the frames before it, as BGRA.
    private static byte[] DecodeFrame(SKCodec codec, int index)
    {
        var info = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Bgra8888, SKAlphaType.Unpremul);
        using var bitmap = new SKBitmap(info);
        Assert.Equal(SKCodecResult.Success, codec.GetPixels(info, bitmap.GetPixels(), new SKCodecOptions(index)));
        return bitmap.GetPixelSpan().ToArray();
    }

    // How far the frame's colour strays from the grey, over every pixel and channel; a see-through pixel counts as
    // straying all the way.
    private static int MaxDeviation(byte[] bgra, int grey)
    {
        int max = 0;
        for (int p = 0; p < bgra.Length; p += 4)
        {
            if (bgra[p + 3] != 255)
            {
                return 255;
            }

            max = Math.Max(max, Math.Abs(bgra[p] - grey));
            max = Math.Max(max, Math.Abs(bgra[p + 1] - grey));
            max = Math.Max(max, Math.Abs(bgra[p + 2] - grey));
        }

        return max;
    }

    // Reports on the exporting thread itself, unlike Progress<T>, so a test can act at an exact point of the export.
    private sealed class InlineProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }
}
