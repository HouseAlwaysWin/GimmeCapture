using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using GimmeCapture.Services.Core.Media;
using GimmeCapture.Services.Core.Media.NativeFFmpeg;

namespace GimmeCapture.Tests;

/// <summary>
/// A recording saved as WebM: its video is encoded to a video-only WebM, then muxed with the audio as Opus. When the
/// mux failed, the finalize encoded the whole video a second time, the slowest step of it, only to write the same
/// video-only file it already had. Real libav on tiny synthetic clips (<see cref="VideoTestClips"/>), so the tests need
/// only the bundled FFmpeg and run everywhere.
/// </summary>
public sealed class RecordingWebmFinalizeTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "GimmeCapture.Tests", Guid.NewGuid().ToString("N"));

    public RecordingWebmFinalizeTests()
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
    public async Task AFailedAudioMux_KeepsTheVideoItEncoded()
    {
        string merged = VideoTestClips.WriteConstantRate(Path.Combine(_dir, "merged.mkv"), 64, 48, 30, 30);
        string audio = Path.Combine(_dir, "merged_audio.wav");
        File.WriteAllBytes(audio, Encoding.ASCII.GetBytes("not a wave file"));
        string output = Path.Combine(Directory.CreateDirectory(Path.Combine(_dir, "saved")).FullName, "recording.webm");

        await new RecordingService(new FFmpegDownloaderService()).FinalizeAsWebmForTestAsync(merged, audio, _dir, output);

        Assert.Equal(30, VideoTestClips.Read(output).Frames);
        Assert.Throws<InvalidOperationException>(() => LibavPinAudioPcmDecoder.Decode(output, 0)); // no audio stream
        Assert.False(File.Exists(Path.Combine(_dir, "webm_video_only.webm")));
    }

    [Fact]
    public async Task WithTheAudio_TheWebmHasBoth_AndTheVideoOnlyFileGoes()
    {
        string merged = VideoTestClips.WriteConstantRate(Path.Combine(_dir, "merged.mkv"), 64, 48, 30, 30);
        string audio = VideoTestClips.WriteTone(Path.Combine(_dir, "merged_audio.wav"), 1.0);
        string output = Path.Combine(Directory.CreateDirectory(Path.Combine(_dir, "saved")).FullName, "recording.webm");

        await new RecordingService(new FFmpegDownloaderService()).FinalizeAsWebmForTestAsync(merged, audio, _dir, output);

        Assert.Equal(30, VideoTestClips.Read(output).Frames);
        Assert.NotEmpty(LibavPinAudioPcmDecoder.Decode(output, 0).PcmBytes);
        Assert.False(File.Exists(Path.Combine(_dir, "webm_video_only.webm")));
    }
}
