using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using GimmeCapture.Models;
using GimmeCapture.Services.Core.Media;
using GimmeCapture.Services.Core.Media.NativeFFmpeg;
using NAudio.Wave;
using Xunit.Abstractions;

namespace GimmeCapture.Tests;

/// <summary>
/// <see cref="GifWebmVideoExporter"/>'s WebM audio. The whole track was decoded into one array and then written to the
/// WAV the Opus encoder reads, and that WAV and the video-only WebM stayed beside the output: in a pin's export folder,
/// which a copy keeps for the clipboard. Real libav on tiny synthetic clips (<see cref="VideoTestClips"/>), so the tests
/// need only the bundled FFmpeg and run everywhere.
/// </summary>
public sealed class GifWebmVideoExporterTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "GimmeCapture.Tests", Guid.NewGuid().ToString("N"));
    private readonly ITestOutputHelper _output;

    public GifWebmVideoExporterTests(ITestOutputHelper output)
    {
        _output = output;
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
    public void WriteAudioAsWav_WritesTheTrack_AsTheDecodeIntoMemoryHadIt()
    {
        string clip = VideoTestClips.WriteWithTone(Path.Combine(_dir, "tone.mkv"), 64, 48, 60, 30);
        string wav = Path.Combine(_dir, "audio.wav");

        Assert.True(GifWebmVideoExporter.WriteAudioAsWav(clip, wav, CancellationToken.None));

        LibavPinAudioPcmDecoder.DecodeResult inMemory = LibavPinAudioPcmDecoder.Decode(clip, 0);
        using var reader = new WaveFileReader(wav);
        var written = new byte[reader.Length];
        Assert.Equal(written.Length, reader.Read(written, 0, written.Length));
        Assert.Equal(inMemory.WaveFormat, reader.WaveFormat);
        Assert.Equal(inMemory.PcmBytes, written);
        Assert.InRange(reader.TotalTime.TotalSeconds, 1.9, 2.1);
    }

    [Fact]
    public void TranscodeToWebm_WithTheAudio_KeepsIt_AndLeavesNoStepFiles()
    {
        string clip = VideoTestClips.WriteWithTone(Path.Combine(_dir, "tone.mkv"), 64, 48, 60, 30);
        string work = Directory.CreateDirectory(Path.Combine(_dir, "work")).FullName;
        string output = Path.Combine(work, "output.webm");

        Assert.True(GifWebmVideoExporter.TranscodeToWebm(clip, output, work, VideoQuality.Low, keepAudio: true, CancellationToken.None));

        Assert.Equal(60, VideoTestClips.Read(output).Frames);
        Assert.NotEmpty(LibavPinAudioPcmDecoder.Decode(output, 0).PcmBytes);
        Assert.Equal([output], Directory.GetFiles(work));
    }

    [Fact]
    public void TranscodeToWebm_KeepingTheAudioOfAClipWithNone_WritesTheVideoAlone()
    {
        string clip = VideoTestClips.WriteConstantRate(Path.Combine(_dir, "silent.mkv"), 64, 48, 30, 30);
        string work = Directory.CreateDirectory(Path.Combine(_dir, "work")).FullName;
        string output = Path.Combine(work, "output.webm");

        Assert.True(GifWebmVideoExporter.TranscodeToWebm(clip, output, work, VideoQuality.Low, keepAudio: true, CancellationToken.None));

        Assert.Equal(30, VideoTestClips.Read(output).Frames);
        Assert.Equal([output], Directory.GetFiles(work));
    }

    /// <summary>
    /// The WebM's audio step on a real recording: decoding the track into memory and writing it out, against writing
    /// it as it decodes. The pin did the first on the UI thread. Opt-in: GIFWEBM_AUDIO_PROBE names the clip.
    /// </summary>
    [OptInFact("GIFWEBM_AUDIO_PROBE")]
    public void TheAudioStep_InMemoryAgainstAsItDecodes()
    {
        string clip = Environment.GetEnvironmentVariable("GIFWEBM_AUDIO_PROBE")!;
        string wav = Path.Combine(_dir, "audio.wav");
        for (int run = 0; run < 3; run++)
        {
            (double inMemoryMs, long inMemoryBytes) = Measure(() =>
            {
                LibavPinAudioPcmDecoder.DecodeResult pcm = LibavPinAudioPcmDecoder.Decode(clip, 0);
                using var writer = new WaveFileWriter(wav, pcm.WaveFormat);
                writer.Write(pcm.PcmBytes, 0, pcm.PcmBytes.Length);
            });
            long wavBytes = new FileInfo(wav).Length;
            (double streamedMs, long streamedBytes) = Measure(() =>
                Assert.True(GifWebmVideoExporter.WriteAudioAsWav(clip, wav, CancellationToken.None)));

            _output.WriteLine(
                $"run {run}: {wavBytes / 1048576.0:F1} MB of WAV; in memory {inMemoryMs:F0} ms, " +
                $"{inMemoryBytes / 1048576.0:F1} MB allocated; as it decodes {streamedMs:F0} ms, " +
                $"{streamedBytes / 1048576.0:F1} MB allocated");
        }
    }

    private static (double Milliseconds, long Allocated) Measure(Action step)
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        long before = GC.GetTotalAllocatedBytes(precise: true);
        var watch = Stopwatch.StartNew();
        step();
        watch.Stop();
        return (watch.Elapsed.TotalMilliseconds, GC.GetTotalAllocatedBytes(precise: true) - before);
    }
}
