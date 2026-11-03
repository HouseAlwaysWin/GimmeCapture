using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using GimmeCapture.Models;
using GimmeCapture.Services.Core.Media;
using GimmeCapture.Services.Core.Media.NativeFFmpeg;
using NAudio.Wave;
using Xunit.Abstractions;

namespace GimmeCapture.Tests;

/// <summary>
/// The WAV an export's audio is written into as it decodes (<see cref="DecodedWavFile"/>), and the exports that build
/// their sound with it: the clip exporter's, for its kept ranges, and the audio-only export's. Real libav on tones and
/// clips written in-process (<see cref="VideoTestClips"/>); needs only the bundled FFmpeg.
/// </summary>
public sealed class DecodedWavFileTests : IDisposable
{
    private const int BytesPerSecond = 48_000 * 4;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "GimmeCapture.Tests", Guid.NewGuid().ToString("N"));
    private readonly ITestOutputHelper _output;

    public DecodedWavFileTests(ITestOutputHelper output)
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
    public void Ranges_AreWritten_AsTheirDecodesIntoMemoryHaveThem()
    {
        string tone = VideoTestClips.WriteTone(Path.Combine(_dir, "tone.wav"), 3.0);
        string path = Path.Combine(_dir, "out.wav");

        using (var wav = new DecodedWavFile(path))
        {
            wav.AppendRange(tone, 0.5, 1.0, CancellationToken.None);
            wav.AppendRange(tone, 2.0, 0.5, CancellationToken.None);
            wav.Append([1, 2, 3, 4], new WaveFormat(48_000, 16, 2));
        }

        byte[] expected = LibavPinAudioPcmDecoder.DecodeRange(tone, 0.5, 1.0).PcmBytes
            .Concat(LibavPinAudioPcmDecoder.DecodeRange(tone, 2.0, 0.5).PcmBytes)
            .Concat(new byte[] { 1, 2, 3, 4 })
            .ToArray();
        Assert.Equal(expected, ReadPcm(path, out WaveFormat format));
        Assert.Equal(new WaveFormat(48_000, 16, 2), format);
        Assert.Equal(1.5 * BytesPerSecond + 4, expected.Length);
    }

    // What the decode and the WAV take is the same for any length (the file's 1 MB buffer the most of it), so two
    // minutes of sound, 23 MB of PCM, leave them a small part of it.
    [Fact]
    public void ARange_IsWrittenAChunkAtATime_NotHeldWhole()
    {
        string tone = VideoTestClips.WriteTone(Path.Combine(_dir, "tone.wav"), 120);
        using var wav = new DecodedWavFile(Path.Combine(_dir, "out.wav"));

        long before = GC.GetAllocatedBytesForCurrentThread();
        wav.AppendRange(tone, 0, double.PositiveInfinity, CancellationToken.None);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(120L * BytesPerSecond, wav.BytesWritten);
        Assert.True(allocated < wav.BytesWritten / 10, $"{allocated:N0} bytes allocated to write {wav.BytesWritten:N0}");
    }

    [Fact]
    public void AFileWithNoSound_Throws_AndLeavesNoFile()
    {
        string clip = VideoTestClips.WriteConstantRate(Path.Combine(_dir, "clip.mkv"), 64, 48, 30, 30);
        string path = Path.Combine(_dir, "out.wav");
        using var wav = new DecodedWavFile(path);

        Assert.Throws<InvalidOperationException>(
            () => wav.AppendRange(clip, 0, double.PositiveInfinity, CancellationToken.None));

        Assert.Equal(0, wav.BytesWritten);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void AnExportsSound_IsEachRangeInOrder_RetimedWhereSpedUp()
    {
        string tone = VideoTestClips.WriteTone(Path.Combine(_dir, "tone.wav"), 3.0);
        string video = VideoTestClips.WriteConstantRate(Path.Combine(_dir, "video.mkv"), 64, 48, 60, 30);
        string output = Path.Combine(_dir, "out.mkv");
        LibavClipExporter.SourceRange[] ranges = [new(0, 1.0), new(1.0, 3.0, 2.0)];

        Assert.True(LibavClipExporter.TryMuxAudioForRanges(
            tone, ranges, video, output, VideoQuality.Medium, new LibavExportOptions(), CancellationToken.None));

        // A second at its own speed, then two at twice it; AAC adds a few milliseconds of its own.
        double seconds = LibavPinAudioPcmDecoder.Decode(output, 0).PcmBytes.Length / (double)BytesPerSecond;
        Assert.InRange(seconds, 1.98, 2.1);
    }

    [Fact]
    public void AnExportsSound_IsNotHeldWhole()
    {
        string tone = VideoTestClips.WriteTone(Path.Combine(_dir, "tone.wav"), 120);
        string video = VideoTestClips.WriteConstantRate(Path.Combine(_dir, "video.mkv"), 64, 48, 30, 30);
        string output = Path.Combine(_dir, "out.mkv");

        long before = GC.GetAllocatedBytesForCurrentThread();
        Assert.True(LibavClipExporter.TryMuxAudioForRanges(
            tone, [new(0, 120)], video, output, VideoQuality.Medium, new LibavExportOptions(), CancellationToken.None));
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(allocated < 120L * BytesPerSecond / 10, $"{allocated:N0} bytes allocated for two minutes of sound");
    }

    [Fact]
    public void AnAudioOnlyExport_ToWav_IsTheTrack_WrittenAsItDecodes()
    {
        string clip = VideoTestClips.WriteWithTone(Path.Combine(_dir, "clip.mkv"), 64, 48, 1800, 30);
        string output = Path.Combine(_dir, "out.wav");

        long before = GC.GetAllocatedBytesForCurrentThread();
        Assert.True(ExtractAudio(clip, output));
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        byte[] track = LibavPinAudioPcmDecoder.Decode(clip, 0).PcmBytes;
        Assert.Equal(track, ReadPcm(output, out _));
        Assert.True(allocated < track.Length / 4, $"{allocated:N0} bytes allocated to write {track.Length:N0}");
    }

    [Fact]
    public void AnAudioOnlyExport_OfAClipWithNoSound_Throws_AndLeavesNoFile()
    {
        string clip = VideoTestClips.WriteConstantRate(Path.Combine(_dir, "clip.mkv"), 64, 48, 30, 30);
        string output = Path.Combine(_dir, "out.m4a");

        Assert.Throws<InvalidOperationException>(() => ExtractAudio(clip, output));

        Assert.False(File.Exists(output));
        Assert.False(File.Exists(Path.Combine(_dir, "extract.wav")));
    }

    /// <summary>
    /// What building an export's sound costs as it decodes, against the array it was decoded into, copied into a stream
    /// and out of it before it was written. Opt-in: AUDIO_BUILD_PROBE=1, with AUDIO_BUILD_PROBE_MINUTES (30 by default)
    /// of a tone encoded as AAC first, as a recording's sound is.
    /// </summary>
    [OptInFact("AUDIO_BUILD_PROBE=1")]
    public void BuildingTheSound_Timed()
    {
        double minutes = double.TryParse(Environment.GetEnvironmentVariable("AUDIO_BUILD_PROBE_MINUTES"), out double m) ? m : 30;
        string toneWav = VideoTestClips.WriteTone(Path.Combine(_dir, "tone.wav"), minutes * 60);
        string source = Path.Combine(_dir, "tone.m4a");
        LibavAacTranscoder.EncodeWavToM4a(toneWav, source, VideoQuality.Medium);
        File.Delete(toneWav);
        double seconds = minutes * 60;

        for (int run = 0; run < 2; run++)
        {
            foreach (bool asItDecodes in run == 0 ? new[] { false, true } : new[] { true, false })
            {
                string wavPath = Path.Combine(_dir, $"build-{asItDecodes}.wav");
                GC.Collect();
                GC.WaitForPendingFinalizers();
                long allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
                var watch = Stopwatch.StartNew();
                long pcm = asItDecodes ? WriteAsItDecodes(source, seconds, wavPath) : WriteThroughMemory(source, seconds, wavPath);
                double elapsed = watch.Elapsed.TotalSeconds;
                long allocated = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
                _output.WriteLine(
                    $"{(asItDecodes ? "as it decodes " : "through memory")}: {elapsed:F2} s, {allocated / 1048576.0:F1} MB " +
                    $"allocated for {pcm / 1048576.0:F1} MB of PCM ({minutes} min)");
                File.Delete(wavPath);
            }
        }
    }

    private static long WriteAsItDecodes(string source, double seconds, string wavPath)
    {
        using var wav = new DecodedWavFile(wavPath);
        wav.AppendRange(source, 0, seconds, CancellationToken.None);
        return wav.BytesWritten;
    }

    // The exporter's former steps: the range decoded into an array, written into a stream, copied out of it, written.
    private static long WriteThroughMemory(string source, double seconds, string wavPath)
    {
        LibavPinAudioPcmDecoder.DecodeResult decoded = LibavPinAudioPcmDecoder.DecodeRange(source, 0, seconds);
        using var pcm = new MemoryStream();
        pcm.Write(decoded.PcmBytes, 0, decoded.PcmBytes.Length);
        byte[] bytes = pcm.ToArray();
        using var writer = new WaveFileWriter(wavPath, decoded.WaveFormat);
        writer.Write(bytes, 0, bytes.Length);
        return bytes.Length;
    }

    private static byte[] ReadPcm(string path, out WaveFormat format)
    {
        using var reader = new WaveFileReader(path);
        format = reader.WaveFormat;
        var pcm = new byte[reader.Length];
        Assert.Equal(pcm.Length, reader.Read(pcm, 0, pcm.Length));
        return pcm;
    }

    // The audio-only export as the compress pipeline calls it, writing its steps into the test's folder.
    private bool ExtractAudio(string source, string output) =>
        AudioOnlyExporter.ExtractAudio(source, output, _dir, VideoQuality.Medium, bitrateKbps: 0, channels: 0, CancellationToken.None);
}
