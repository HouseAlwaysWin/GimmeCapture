using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using GimmeCapture.Services.Core.Infrastructure;
using GimmeCapture.Services.Core.Media;
using GimmeCapture.Services.Core.Media.NativeFFmpeg;
using Xunit;

namespace GimmeCapture.Tests;

/// <summary>
/// The preview audio of a clip with no audio stream, end to end: the real probe, and the real player starting such a
/// clip. A silent recording logged a warning at every play and seek of its pin. The clips are written in-process, a
/// YUV4MPEG2 video with no audio and a WAV tone, so like <see cref="LibavVideoFramePlayerFrameTests"/> these need only
/// the bundled FFmpeg and run everywhere. No audio device is opened: a clip without audio fails before that.
/// </summary>
public sealed class AudioPreviewPlayerNoAudioStreamTests : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "GimmeCapture.Tests", Guid.NewGuid().ToString("N"));

    public AudioPreviewPlayerNoAudioStreamTests()
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
    public void TheProbe_FindsNoAudioStreamInAVideoOnlyClip()
    {
        Assert.False(LibavAudioStreamProbe.HasAudioStream(WriteVideoOnlyClip()));
    }

    [Fact]
    public void TheProbe_FindsTheAudioStreamOfASoundFile()
    {
        string tone = WavTestAudio.WriteSineWav(Path.Combine(_dir, "tone.wav"), seconds: 0.5);

        Assert.True(LibavAudioStreamProbe.HasAudioStream(tone));
    }

    [Fact]
    public void TheProbe_CannotTellForAFileItCannotOpen()
    {
        Assert.Throws<FFmpegNativeException>(() => LibavAudioStreamProbe.HasAudioStream(Path.Combine(_dir, "gone.mp4")));
    }

    [Theory]
    [InlineData(1.0)] // Media Foundation's reader on Windows
    [InlineData(1.5)] // FFmpeg's decode, which plays every speed on Linux
    public async Task StartingAClipWithNoAudioStream_FindsThat_AndLogsNoWarning(double speed)
    {
        string clip = WriteVideoOnlyClip();
        using var player = new AudioPreviewPlayer();
        int warningsBefore = PinAudioWarnings();

        player.Start(clip, 0, speed);

        Assert.True(await WaitUntilAsync(() => player.FoundNoAudioStream(clip)), "the player never found the clip has no audio");
        Assert.True(PinAudioWarnings() <= warningsBefore, "a clip with no audio stream was logged as a failure");
    }

    // The warnings a failed start logs (PinAudio.PrimaryOutput, PinAudio.FfmpegFallback) go into the error summary that
    // a bug report carries; expected failures, logged at Debug, do not. No other test starts preview audio.
    private static int PinAudioWarnings() =>
        AppLog.GetAnonymousErrorSummary(20).Count(line => line.Contains("| PinAudio.", StringComparison.Ordinal));

    private static async Task<bool> WaitUntilAsync(Func<bool> condition)
    {
        DateTime deadline = DateTime.UtcNow + Timeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                return false;
            }

            await OwnThread.Delay(TimeSpan.FromMilliseconds(20));
        }

        return true;
    }

    // Eight flat grey 64x48 frames at 30 fps: a video stream and nothing else.
    private string WriteVideoOnlyClip()
    {
        const int width = 64;
        const int height = 48;
        string path = Path.Combine(_dir, "video-only.y4m");
        using FileStream file = File.Create(path);
        file.Write(Encoding.ASCII.GetBytes($"YUV4MPEG2 W{width} H{height} F30:1 Ip A1:1 C420jpeg\n"));

        var planes = new byte[(width * height) + (2 * (width / 2) * (height / 2))];
        planes.AsSpan(0, width * height).Fill(128);
        planes.AsSpan(width * height).Fill(128);
        byte[] frameHeader = Encoding.ASCII.GetBytes("FRAME\n");
        for (int i = 0; i < 8; i++)
        {
            file.Write(frameHeader);
            file.Write(planes);
        }

        return path;
    }
}
