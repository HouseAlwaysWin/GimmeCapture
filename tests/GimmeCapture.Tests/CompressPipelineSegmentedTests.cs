using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using GimmeCapture.Models;
using GimmeCapture.Services.Core.Media;
using GimmeCapture.Services.Core.Media.NativeFFmpeg;

namespace GimmeCapture.Tests;

/// <summary>
/// A compress at a quality (CRF) of a whole clip into a plain container, the Compress tab's default: the clip is encoded
/// in chunks that a restart can resume from, then joined and given its sound. Real libav on tiny synthetic clips (<see
/// cref="VideoTestClips"/>); the chunks go to the test run's own data folder (<see cref="TestStorageRoot"/>).
/// </summary>
public sealed class CompressPipelineSegmentedTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "GimmeCapture.Tests", Guid.NewGuid().ToString("N"));

    public CompressPipelineSegmentedTests()
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

    // With no sound to add, the joined chunks were copied to the output as they were, a Matroska file, whatever the
    // output's name said.
    [Theory]
    [InlineData(".mp4", false, false)] // a recording with no sound
    [InlineData(".mp4", true, true)]   // its sound dropped
    [InlineData(".mp4", true, false)]
    [InlineData(".mkv", false, false)]
    [InlineData(".mkv", true, false)]
    public async Task TheOutput_IsTheContainerItsNameSays(string extension, bool withSound, bool dropAudio)
    {
        string clip = withSound
            ? VideoTestClips.WriteWithTone(Path.Combine(_dir, "clip.mkv"), 64, 48, 60, 30)
            : VideoTestClips.WriteConstantRate(Path.Combine(_dir, "clip.mkv"), 64, 48, 60, 30);
        string output = Path.Combine(_dir, "out" + extension);

        bool ok = await CompressPipeline.EncodeOneFileAsync(
            clip, output, Settings() with { DropAudio = dropAudio }, 0, 2.0, new Progress<double>(),
            CancellationToken.None, pauseGate: null);

        Assert.True(ok);
        Assert.Equal(extension == ".mp4" ? "mp4" : "matroska", ContainerOf(output));
        using var player = new LibavVideoFramePlayer();
        Assert.Equal(withSound && !dropAudio, await player.ProbeHasAudioAsync(output));
    }

    // The silent output is the joined chunks remuxed, which must keep every frame. (A clip with the tone is Matroska
    // with millisecond times, which the encode reads as 30.3 frames a second, so it has a frame more.)
    [Theory]
    [InlineData(".mp4")]
    [InlineData(".mkv")]
    public async Task WithNoSound_EveryFrameComesThrough(string extension)
    {
        string clip = VideoTestClips.WriteConstantRate(Path.Combine(_dir, "clip.mkv"), 64, 48, 60, 30);
        string output = Path.Combine(_dir, "out" + extension);

        Assert.True(await CompressPipeline.EncodeOneFileAsync(
            clip, output, Settings(), 0, 2.0, new Progress<double>(), CancellationToken.None, pauseGate: null));

        VideoTestClips.Timeline timeline = VideoTestClips.Read(output);
        Assert.Equal(60, timeline.Frames);
        Assert.Equal(2.0, timeline.DurationSeconds, 2);
    }

    // What the file's first bytes say it is: an MP4 begins with an ftyp box, a Matroska file with the EBML magic.
    private static string ContainerOf(string path)
    {
        var head = new byte[12];
        using (FileStream file = File.OpenRead(path))
        {
            file.ReadExactly(head);
        }

        if (head[4] == 'f' && head[5] == 't' && head[6] == 'y' && head[7] == 'p')
        {
            return "mp4";
        }

        return head[0] == 0x1A && head[1] == 0x45 && head[2] == 0xDF && head[3] == 0xA3 ? "matroska" : "unknown";
    }

    private static CompressSettingsSnapshot Settings() => new(
        VideoCodec.H264, 28, MaxHeight: 0, MaxFps: 0, "ultrafast",
        DropAudio: false, AudioBitrateKbps: 128, AudioChannels: 0,
        UseTargetSize: false, TargetSizeMB: 0, UseTwoPass: false,
        DenoiseMode.Off, SharpenMode.Off, Deblock: false, Grayscale: false);
}
