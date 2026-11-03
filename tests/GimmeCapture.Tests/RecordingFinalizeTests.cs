using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using GimmeCapture.Services.Core.Media;
using GimmeCapture.Services.Core.Media.NativeFFmpeg;
using Xunit.Abstractions;

namespace GimmeCapture.Tests;

/// <summary>
/// A stop's finalize, from the segments a recording leaves to its file (<see cref="RecordingService.FinalizeForTestAsync"/>).
/// A recording paused and resumed has a video segment and a sound segment for each stretch; MP4, MOV and MKV join the
/// video segments with the sound in their mux, where they were joined into a merged.mkv first, the whole video written
/// once more. Real libav on tiny synthetic clips and tones (<see cref="VideoTestClips"/>), so it runs everywhere.
/// </summary>
public sealed class RecordingFinalizeTests : IDisposable
{
    private const double BytesPerSecond = 48_000 * 4;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "GimmeCapture.Tests", Guid.NewGuid().ToString("N"));
    private readonly string _session;
    private readonly string _saved;
    private readonly ITestOutputHelper _output;

    public RecordingFinalizeTests(ITestOutputHelper output)
    {
        _output = output;
        _session = Directory.CreateDirectory(Path.Combine(_dir, "session")).FullName;
        _saved = Directory.CreateDirectory(Path.Combine(_dir, "saved")).FullName;
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    [Theory]
    [InlineData("mp4")]
    [InlineData("mkv")]
    public async Task APausedRecording_IsJoinedWithItsSound_InTheMux(string format)
    {
        (string[] video, string[] sound) = Stretches(3);
        string output = Path.Combine(_saved, "recording." + format);
        var recorder = new RecordingService(new FFmpegDownloaderService());

        await recorder.FinalizeForTestAsync(video, sound, _session, output, format);

        VideoTestClips.Timeline picture = VideoTestClips.Read(output);
        double heard = LibavPinAudioPcmDecoder.Decode(output, 0).PcmBytes.Length / BytesPerSecond;
        Assert.Equal(90, picture.Frames);
        Assert.Equal(3.0, picture.DurationSeconds, 0.01);
        Assert.Equal(picture.DurationSeconds, heard, 0.05);
        Assert.False(File.Exists(Path.Combine(_session, "merged.mkv")));
        Assert.StartsWith("segments=3 audio=", recorder.LastFinalizeTimings);
    }

    [Fact]
    public async Task APausedRecording_WithNoSound_IsJoinedAlone()
    {
        (string[] video, _) = Stretches(3);
        string output = Path.Combine(_saved, "recording.mp4");

        await new RecordingService(new FFmpegDownloaderService()).FinalizeForTestAsync(video, [], _session, output, "mp4");

        Assert.Equal(90, VideoTestClips.Read(output).Frames);
        Assert.Throws<InvalidOperationException>(() => LibavPinAudioPcmDecoder.Decode(output, 0)); // no audio stream
        Assert.False(File.Exists(Path.Combine(_session, "merged.mkv")));
    }

    [Fact]
    public async Task ARecordingThatWasNotPaused_IsMuxedAsItWas()
    {
        (string[] video, string[] sound) = Stretches(1);
        string output = Path.Combine(_saved, "recording.mp4");

        await new RecordingService(new FFmpegDownloaderService()).FinalizeForTestAsync(video, sound, _session, output, "mp4");

        Assert.Equal(30, VideoTestClips.Read(output).Frames);
        Assert.Equal(1.0, LibavPinAudioPcmDecoder.Decode(output, 0).PcmBytes.Length / BytesPerSecond, 0.05);
    }

    // A GIF is transcoded from one file, so a paused recording's segments are still joined into merged.mkv for it.
    [Fact]
    public async Task APausedRecordingSavedAsGif_IsStillJoinedFirst()
    {
        (string[] video, string[] sound) = Stretches(2);
        string output = Path.Combine(_saved, "recording.gif");

        await new RecordingService(new FFmpegDownloaderService()).FinalizeForTestAsync(video, sound, _session, output, "gif");

        Assert.True(VideoTestClips.Read(output).Frames > 0);
        Assert.Equal(2.0, VideoTestClips.Read(Path.Combine(_session, "merged.mkv")).DurationSeconds, 0.01);
    }

    /// <summary>
    /// How long a stop's finalize takes, for ten seconds at 1280x720 recorded in one stretch and in three. Opt-in:
    /// FINALIZE_PROBE=1.
    /// </summary>
    [OptInFact("FINALIZE_PROBE=1")]
    public async Task AStopsFinalize_Timed()
    {
        foreach (int stretches in new[] { 1, 3, 3, 1 })
        {
            string session = Directory.CreateDirectory(Path.Combine(_dir, $"session-{Guid.NewGuid():N}")).FullName;
            (string[] video, string[] sound) = Stretches(stretches, session, 1280, 720, seconds: 10.0 / stretches);
            string output = Path.Combine(_saved, $"recording-{Guid.NewGuid():N}.mp4");
            var recorder = new RecordingService(new FFmpegDownloaderService());

            var watch = Stopwatch.StartNew();
            await recorder.FinalizeForTestAsync(video, sound, session, output, "mp4");
            _output.WriteLine(
                $"{stretches} stretch(es): {watch.Elapsed.TotalMilliseconds:F0} ms, {new FileInfo(output).Length / 1048576.0:F1} MB; " +
                $"{recorder.LastFinalizeTimings}");
        }
    }

    // Each stretch a second of video, at 30 fps, and a second of sound, as the recorder writes them into its session.
    private (string[] Video, string[] Sound) Stretches(int count) => Stretches(count, _session, 64, 48, seconds: 1.0);

    private static (string[] Video, string[] Sound) Stretches(
        int count, string session, int width, int height, double seconds) =>
    (
        Enumerable.Range(0, count)
            .Select(i => VideoTestClips.WriteConstantRate(
                Path.Combine(session, $"segment_{i}.mkv"), width, height, (int)Math.Round(seconds * 30), 30,
                detailed: width > 64))
            .ToArray(),
        Enumerable.Range(0, count)
            .Select(i => VideoTestClips.WriteTone(Path.Combine(session, $"system_audio_{i}.wav"), seconds))
            .ToArray()
    );
}
