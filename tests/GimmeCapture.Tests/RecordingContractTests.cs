using System;
using System.IO;
using GimmeCapture.Services.Core.Media;
using GimmeCapture.Services.Core.Media.NativeFFmpeg;
using SkiaSharp;

namespace GimmeCapture.Tests;

public class RecordingContractTests
{
    [Theory]
    [InlineData(false, "mp4", false)]
    [InlineData(true, "mp4", true)]
    [InlineData(true, "gif", false)]
    public void AudioPolicy_RespectsSwitchAndGifContract(bool requested, string format, bool expected)
    {
        Assert.Equal(expected, RecordingAudioPolicy.ShouldRecordSystemAudio(requested, format));
    }

    [Fact]
    public void TempCleanup_DeletesOnlyRecordingSessionDirectories()
    {
        string parent = Path.Combine(Path.GetTempPath(), "GimmeCapture.Tests", Guid.NewGuid().ToString("N"));
        string session = Path.Combine(parent, $"Recordings_{Guid.NewGuid()}");
        string unrelated = Path.Combine(parent, "KeepMe");
        Directory.CreateDirectory(session);
        Directory.CreateDirectory(unrelated);
        File.WriteAllText(Path.Combine(session, "segment.mkv"), "data");

        try
        {
            Assert.True(RecordingTempDirectory.TryDeleteSessionDirectory(session, parent));
            Assert.False(Directory.Exists(session));
            Assert.False(RecordingTempDirectory.TryDeleteSessionDirectory(unrelated, parent));
            Assert.True(Directory.Exists(unrelated));
        }
        finally
        {
            if (Directory.Exists(parent))
            {
                Directory.Delete(parent, recursive: true);
            }
        }
    }

    [Fact]
    public void ConcatVideoSegments_WithNoSegments_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() =>
            LibavMuxer.ConcatVideoSegments(Array.Empty<string>(), "out.mkv", "matroska"));
        Assert.Throws<ArgumentException>(() =>
            LibavMuxer.ConcatVideoSegments(null!, "out.mkv", "matroska"));
    }

    [Fact]
    public void GifTranscoder_ProducesReadableAnimatedGif()
    {
        Assert.True(RecordingFormatCapabilities.IsGifAvailable());

        string root = Path.Combine(Path.GetTempPath(), "GimmeCapture.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string input = Path.Combine(root, "input.gif");
        string output = Path.Combine(root, "output.gif");
        File.WriteAllBytes(input, CreateTwoFrameGif());

        try
        {
            LibavGifTranscoder.TranscodeToGif(input, output, targetFps: 10, maxWidth: 0);

            Assert.True(File.Exists(output));
            Assert.True(new FileInfo(output).Length > 0);
            using var stream = File.OpenRead(output);
            using var codec = SKCodec.Create(stream);
            Assert.NotNull(codec);
            Assert.True(codec!.FrameCount >= 2);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void GifFrameTimeline_DropsFramesWithoutStretchingDuration()
    {
        var timeline = new GifFrameTimeline(
            inputTimeBaseSeconds: 1d / 30d,
            targetFps: 15,
            fallbackSourceFps: 30);
        var outputPts = new List<long>();

        for (long inputPts = 0; inputPts < 120; inputPts++)
        {
            if (timeline.TrySchedule(inputPts, out long scheduledPts))
            {
                outputPts.Add(scheduledPts);
            }
        }

        Assert.Equal(60, outputPts.Count);
        Assert.Equal(0, outputPts[0]);
        Assert.Equal(59, outputPts[^1]);
    }

    private static byte[] CreateTwoFrameGif()
    {
        byte[] headerAndPalette =
        [
            0x47, 0x49, 0x46, 0x38, 0x39, 0x61,
            0x01, 0x00, 0x01, 0x00, 0x80, 0x00, 0x00,
            0x00, 0x00, 0x00, 0xff, 0xff, 0xff
        ];
        byte[] loop =
        [
            0x21, 0xff, 0x0b,
            0x4e, 0x45, 0x54, 0x53, 0x43, 0x41, 0x50, 0x45, 0x32, 0x2e, 0x30,
            0x03, 0x01, 0x00, 0x00, 0x00
        ];
        byte[] frame =
        [
            0x21, 0xf9, 0x04, 0x00, 0x0a, 0x00, 0x00, 0x00,
            0x2c, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00,
            0x02, 0x01, 0x4c, 0x00
        ];

        var bytes = new byte[headerAndPalette.Length + loop.Length + (frame.Length * 2) + 1];
        int offset = 0;
        headerAndPalette.CopyTo(bytes, offset);
        offset += headerAndPalette.Length;
        loop.CopyTo(bytes, offset);
        offset += loop.Length;
        frame.CopyTo(bytes, offset);
        offset += frame.Length;
        frame.CopyTo(bytes, offset);
        bytes[^1] = 0x3b;
        return bytes;
    }
}
