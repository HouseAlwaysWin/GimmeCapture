using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using GimmeCapture.Models;
using GimmeCapture.Services.Core.Infrastructure;
using GimmeCapture.Services.Core.Media;
using GimmeCapture.Services.Core.Media.NativeFFmpeg;
using NAudio.Wave;
using Xunit.Abstractions;

namespace GimmeCapture.Tests;

/// <summary>
/// A stop's finalize, from the segments a recording leaves to its file (<see cref="RecordingService.FinalizeForTestAsync"/>).
/// A recording paused and resumed has a video segment and a sound segment for each stretch; MP4, MOV and MKV join the
/// video segments with the sound in their mux, where they were joined into a merged.mkv first, the whole video written
/// once more. Real libav on tiny synthetic clips and tones (<see cref="VideoTestClips"/>), so it runs everywhere.
/// <para>
/// The sound is encoded while the recording goes (<see cref="RecordingSoundEncoder"/>): these write a recording's WAVs a
/// burst at a time, with the encoder following them as it does a real recording, and check that the finalize muxes that
/// sound when it is the one the finalize would have built, and builds its own when it is not.
/// </para>
/// </summary>
public sealed class RecordingFinalizeTests : IDisposable
{
    private const double BytesPerSecond = 48_000 * 4;
    private static readonly WaveFormat Stereo48 = new(48_000, 16, 2);
    private static readonly WaveFormat Mono48 = new(48_000, 16, 1);
    private static readonly WaveFormat Stereo44 = new(44_100, 16, 2);

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

    // The sound encoded while recording is the sound the stop would have built and encoded: the same samples come out of
    // the file. And the stop wrote none of it, no joined or mixed WAV and no AAC of its own, where it does without.
    [Theory]
    [InlineData("system", "mp4")]
    [InlineData("system and microphone, paused twice", "mp4")]
    [InlineData("microphone with a gain, paused once", "mkv")]
    [InlineData("microphone that did not resume", "mp4")]
    public async Task ASoundEncodedWhileRecording_IsTheSoundTheStopWouldBuild(string recording, string format)
    {
        (Stretch[] stretches, double micVolume) = recording switch
        {
            "system" => ([new Stretch(Stereo48, null)], 1.0),
            "system and microphone, paused twice" => (Enumerable.Repeat(new Stretch(Stereo48, Mono48), 3).ToArray(), 0.6),
            "microphone with a gain, paused once" => ([new Stretch(null, Mono48), new Stretch(null, Mono48)], 1.5),
            _ => ([new Stretch(Stereo48, Mono48), new Stretch(Stereo48, null), new Stretch(Stereo48, null)], 1.0),
        };
        var recorder = new RecordingService(new FFmpegDownloaderService());
        Recorded recorded = Record(recorder, format, micVolume, stretches);
        string output = Path.Combine(_saved, "recording." + format);

        await recorder.FinalizeForTestAsync(
            recorded.Video, recorded.System, _session, output, format, recorded.Microphone, micVolume);

        (string stopped, string stopSession) = await FinalizeAtTheStopAsync(recorded, format, micVolume);
        Assert.Equal(Sound(stopped), Sound(output));
        Assert.Empty(SoundTheStopBuilt(_session));
        Assert.Contains("merged_audio.m4a", SoundTheStopBuilt(stopSession));
        Assert.Contains("sound=recorded", recorder.LastFinalizeTimings);
    }

    // The stop resamples a segment whose format changed across a pause, so it builds that sound itself, as it did.
    [Fact]
    public async Task ASoundThatChangedFormatAcrossAPause_IsBuiltAtTheStop()
    {
        var recorder = new RecordingService(new FFmpegDownloaderService());
        Recorded recorded = Record(recorder, "mp4", 1.0, new Stretch(Stereo48, null), new Stretch(Stereo44, null));
        string output = Path.Combine(_saved, "recording.mp4");

        await recorder.FinalizeForTestAsync(recorded.Video, recorded.System, _session, output, "mp4");

        (string stopped, _) = await FinalizeAtTheStopAsync(recorded, "mp4", 1.0);
        Assert.Contains("sound=rebuilt", recorder.LastFinalizeTimings);
        Assert.Equal(Sound(stopped), Sound(output));
        Assert.False(File.Exists(Path.Combine(_session, "recorded_sound.m4a")));
    }

    // The bitrate comes from the quality setting: changed during the recording, the stop encodes at the new one.
    [Fact]
    public async Task AQualityChangedWhileRecording_HasTheSoundEncodedAtTheStop()
    {
        var settings = new AppSettingsService(Directory.CreateDirectory(Path.Combine(_dir, "settings")).FullName);
        var recorder = new RecordingService(new FFmpegDownloaderService(settings), settings);
        Recorded recorded = Record(recorder, "mp4", 1.0, new Stretch(Stereo48, null));
        settings.Settings.VideoQuality = VideoQuality.High;
        string output = Path.Combine(_saved, "recording.mp4");

        await recorder.FinalizeForTestAsync(recorded.Video, recorded.System, _session, output, "mp4");

        (string stopped, _) = await FinalizeAtTheStopAsync(recorded, "mp4", 1.0, settings);
        Assert.Contains("sound=rebuilt", recorder.LastFinalizeTimings);
        Assert.Equal(Sound(stopped), Sound(output));
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

    /// <summary>
    /// How long a stop's finalize takes after FINALIZE_PROBE_SECONDS (120 by default) of system audio, with the sound
    /// encoded while recording and at the stop, ABBA. The sound is written at sixteen times its speed, which the encoder
    /// keeps up with. Opt-in: FINALIZE_PROBE=1.
    /// </summary>
    [OptInFact("FINALIZE_PROBE=1")]
    public async Task AStopsFinalize_WithTheSoundEncodedWhileRecording_Timed()
    {
        double seconds = double.TryParse(Environment.GetEnvironmentVariable("FINALIZE_PROBE_SECONDS"), out double s) ? s : 120;
        foreach (bool whileRecording in new[] { true, false, false, true })
        {
            string session = Directory.CreateDirectory(Path.Combine(_dir, $"session-{Guid.NewGuid():N}")).FullName;
            string video = VideoTestClips.WriteConstantRate(
                Path.Combine(session, "segment_0.mkv"), 320, 240, (int)Math.Round(seconds * 30), 30);
            string wav = Path.Combine(session, "segment_0.wav");
            var recorder = new RecordingService(new FFmpegDownloaderService());
            using (var capture = new GrowingWav(wav, Stereo48, seconds, seed: 7))
            {
                capture.WriteBurst();
                if (whileRecording)
                {
                    recorder.SegmentSoundStartedForTest(true, session, "mp4", 1.0, wav, micSegment: null);
                }

                // A quarter of a second of sound a sleep, which is 15.6 ms: sixteen times its speed.
                GrowingWav.WriteTogether(0.25, TimeSpan.FromMilliseconds(1), capture);
            }

            recorder.SegmentSoundStoppedForTest();
            string output = Path.Combine(_saved, $"recording-{Guid.NewGuid():N}.mp4");
            var watch = Stopwatch.StartNew();
            await recorder.FinalizeForTestAsync([video], [wav], session, output, "mp4");
            _output.WriteLine(
                $"{(whileRecording ? "while recording" : "at the stop    ")}: {watch.Elapsed.TotalMilliseconds,6:F0} ms; " +
                $"{recorder.LastFinalizeTimings}");
        }
    }

    private readonly record struct Stretch(WaveFormat? System, WaveFormat? Microphone);

    private sealed record Recorded(string[] Video, string[] System, string[] Microphone);

    /// <summary>
    /// A recording of one-second stretches, its WAVs written a burst at a time with the recorder's sound encoder
    /// following, handed each stretch's files as StartSegmentAsync and the captures' stop hand them over. The microphone
    /// runs a little short of the system audio, as captures do, so its later stretches land elsewhere in the joined track.
    /// </summary>
    private Recorded Record(RecordingService recorder, string format, double micVolume, params Stretch[] stretches)
    {
        var video = new List<string>();
        var system = new List<string>();
        var microphone = new List<string>();
        for (int i = 0; i < stretches.Length; i++)
        {
            video.Add(VideoTestClips.WriteConstantRate(Path.Combine(_session, $"segment_{i}.mkv"), 64, 48, 30, 30));
            var captures = new List<GrowingWav>();
            if (stretches[i].System is { } systemFormat)
            {
                captures.Add(new GrowingWav(Path.Combine(_session, $"segment_{i}.wav"), systemFormat, 1.0, seed: i));
                system.Add(captures[^1].Path);
            }

            string? micWav = null;
            if (stretches[i].Microphone is { } micFormat)
            {
                captures.Add(new GrowingWav(Path.Combine(_session, $"mic_{i}.wav"), micFormat, 0.98, seed: 100 + i));
                micWav = captures[^1].Path;
                microphone.Add(micWav);
            }

            string? systemWav = stretches[i].System == null ? null : system[^1];
            if (i == 0)
            {
                // The first stretch starts the encoder after its first frame, when the captures are writing already.
                captures.ForEach(c => c.WriteBurst());
            }

            recorder.SegmentSoundStartedForTest(i == 0, _session, format, micVolume, systemWav, micWav);
            GrowingWav.WriteTogether(TimeSpan.FromMilliseconds(5), [.. captures]);
            captures.ForEach(c => c.Dispose());
            recorder.SegmentSoundStoppedForTest();
        }

        return new Recorded([.. video], [.. system], [.. microphone]);
    }

    // The same recording finalized by a recorder that encoded nothing while it recorded, in a session of its own.
    private async Task<(string Output, string Session)> FinalizeAtTheStopAsync(
        Recorded recorded, string format, double micVolume, IAppSettingsService? settings = null)
    {
        string session = Directory.CreateDirectory(Path.Combine(_dir, "stop")).FullName;
        string output = Path.Combine(Directory.CreateDirectory(Path.Combine(_dir, "stop-saved")).FullName, "recording." + format);
        var recorder = settings == null
            ? new RecordingService(new FFmpegDownloaderService())
            : new RecordingService(new FFmpegDownloaderService(settings), settings);
        await recorder.FinalizeForTestAsync(
            recorded.Video, recorded.System, session, output, format, recorded.Microphone, micVolume);
        return (output, session);
    }

    private static byte[] Sound(string path) => LibavPinAudioPcmDecoder.Decode(path, 0).PcmBytes;

    // The files the stop writes when it builds the sound itself: the joined and mixed WAVs, and their AAC.
    private static string[] SoundTheStopBuilt(string session) =>
        Directory.GetFiles(session)
            .Select(Path.GetFileName)
            .Where(name => name is "merged_audio.m4a" or "mixed_audio.wav" or "mic_gain.wav"
                || name!.StartsWith("merged_", StringComparison.Ordinal) && name.EndsWith(".wav", StringComparison.Ordinal))
            .ToArray()!;

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
