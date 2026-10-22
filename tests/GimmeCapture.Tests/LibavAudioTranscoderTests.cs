using System;
using System.IO;
using GimmeCapture.Models;
using GimmeCapture.Services.Core.Media.NativeFFmpeg;
using Xunit;

namespace GimmeCapture.Tests;

/// <summary>
/// Characterization tests for the audio transcoders (<see cref="LibavAacTranscoder"/>,
/// <see cref="LibavOpusTranscoder"/>, <see cref="LibavMp3Transcoder"/>) and <see cref="LibavPinAudioPcmDecoder"/>.
/// The WAV input is synthesized in-process via <see cref="WavTestAudio"/>, so all they need is the bundled FFmpeg
/// (aac, libopus and libmp3lame encoders), which every CI job provisions; like
/// <see cref="RecordingContractTests"/>' GIF test they run everywhere. Each test writes to its own temporary folder
/// and deletes it. Set COMPRESS_IT_OUTDIR to write to that folder and keep the files instead, as
/// <c>scripts/test-compress.ps1</c> does so they can be listened to.
/// </summary>
public sealed class LibavAudioTranscoderTests : IDisposable
{
    private const string OutDirVariable = "COMPRESS_IT_OUTDIR";

    // xUnit creates an instance per test, so each test gets its own folder.
    private readonly string _outDir;
    private readonly bool _keepOutputs;

    public LibavAudioTranscoderTests()
    {
        string? requested = Environment.GetEnvironmentVariable(OutDirVariable);
        _keepOutputs = !string.IsNullOrWhiteSpace(requested);
        _outDir = _keepOutputs
            ? requested!
            : Path.Combine(Path.GetTempPath(), "GimmeCapture.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_outDir);
    }

    public void Dispose()
    {
        if (!_keepOutputs && Directory.Exists(_outDir))
        {
            Directory.Delete(_outDir, recursive: true);
        }
    }

    private string OutPath(string name) => Path.Combine(_outDir, name);

    private string NewWav(string name, double seconds = 1.0) =>
        WavTestAudio.WriteSineWav(OutPath(name), seconds);

    [Fact]
    public void EncodeWavToM4a_ProducesReadableAacAudio()
    {
        string wav = NewWav("aac_src.wav");
        string m4a = OutPath("aac_out.m4a");

        LibavAacTranscoder.EncodeWavToM4a(wav, m4a, VideoQuality.Medium);

        Assert.True(File.Exists(m4a) && new FileInfo(m4a).Length > 0, "AAC output not produced");
        // The .m4a must carry a decodable audio stream — decode it back to PCM as the proof.
        var decoded = LibavPinAudioPcmDecoder.Decode(m4a, 0);
        Assert.True(decoded.PcmBytes.Length > 0, "AAC output has no decodable audio");
    }

    [Fact]
    public void EncodeWavToM4a_HonorsBitrate_HigherQualityIsLarger()
    {
        // A 2s tone at 192k vs 96k: the higher-bitrate file must be meaningfully larger.
        string wav = NewWav("aac_rate_src.wav", seconds: 2.0);
        string hi = OutPath("aac_high.m4a");
        string lo = OutPath("aac_low.m4a");

        LibavAacTranscoder.EncodeWavToM4a(wav, hi, VideoQuality.High); // 192 kbps
        LibavAacTranscoder.EncodeWavToM4a(wav, lo, VideoQuality.Low);  // 96 kbps

        long hiLen = new FileInfo(hi).Length;
        long loLen = new FileInfo(lo).Length;
        Assert.True(hiLen > loLen, $"High-bitrate AAC ({hiLen}) should exceed low-bitrate ({loLen})");
    }

    [Fact]
    public void EncodeWavToM4a_MonoMixdown_ProducesReadableAudio()
    {
        string wav = NewWav("aac_mono_src.wav");
        string m4a = OutPath("aac_mono.m4a");

        LibavAacTranscoder.EncodeWavToM4a(wav, m4a, VideoQuality.Medium, targetChannels: 1);

        Assert.True(File.Exists(m4a) && new FileInfo(m4a).Length > 0, "mono AAC output not produced");
        var decoded = LibavPinAudioPcmDecoder.Decode(m4a, 0);
        Assert.True(decoded.PcmBytes.Length > 0, "mono AAC output has no decodable audio");
    }

    [Fact]
    public void EncodeWavToOpusOgg_ProducesReadableAudio()
    {
        string wav = NewWav("opus_src.wav");
        string ogg = OutPath("opus_out.ogg");

        LibavOpusTranscoder.EncodeWavToOpusOgg(wav, ogg, VideoQuality.Medium);

        Assert.True(File.Exists(ogg) && new FileInfo(ogg).Length > 0, "Opus output not produced");
        var decoded = LibavPinAudioPcmDecoder.Decode(ogg, 0);
        Assert.True(decoded.PcmBytes.Length > 0, "Opus output has no decodable audio");
    }

    [Fact]
    public void EncodeWavToOpusOgg_HonorsQuality_HigherQualityIsLarger()
    {
        // Opus maps quality to its own bitrates (High 192 kbps, Low 64 kbps), not AAC's.
        string wav = NewWav("opus_rate_src.wav", seconds: 2.0);
        string hi = OutPath("opus_high.ogg");
        string lo = OutPath("opus_low.ogg");

        LibavOpusTranscoder.EncodeWavToOpusOgg(wav, hi, VideoQuality.High);
        LibavOpusTranscoder.EncodeWavToOpusOgg(wav, lo, VideoQuality.Low);

        long hiLen = new FileInfo(hi).Length;
        long loLen = new FileInfo(lo).Length;
        Assert.True(hiLen > loLen, $"High-quality Opus ({hiLen}) should exceed low-quality ({loLen})");
    }

    [Fact]
    public void RoundTrip_WavToAacToPcm_YieldsStereo16BitPcm()
    {
        string wav = NewWav("rt_src.wav", seconds: 1.0);
        string m4a = OutPath("rt_out.m4a");
        LibavAacTranscoder.EncodeWavToM4a(wav, m4a, VideoQuality.Medium);

        var decoded = LibavPinAudioPcmDecoder.Decode(m4a, 0);
        Assert.True(decoded.PcmBytes.Length > 0, "round-trip produced no PCM");
        // The decoder always emits interleaved S16 stereo (see LibavPinAudioPcmDecoder).
        Assert.Equal(16, decoded.WaveFormat.BitsPerSample);
        Assert.Equal(2, decoded.WaveFormat.Channels);
        Assert.Equal(48_000, decoded.WaveFormat.SampleRate); // 48k source preserved through AAC
        // s16 stereo => 4 bytes/frame; the byte count must be frame-aligned.
        Assert.Equal(0, decoded.PcmBytes.Length % 4);
    }

    [Fact]
    public void EncodeWavToMp3_ProducesReadableMp3Audio()
    {
        string wav = NewWav("mp3_src.wav");
        string mp3 = OutPath("mp3_out.mp3");

        LibavMp3Transcoder.EncodeWavToMp3(wav, mp3, VideoQuality.Medium);

        Assert.True(File.Exists(mp3) && new FileInfo(mp3).Length > 0, "MP3 output not produced");
        // Prove the .mp3 carries a decodable audio stream by decoding it back to PCM.
        var decoded = LibavPinAudioPcmDecoder.Decode(mp3, 0);
        Assert.True(decoded.PcmBytes.Length > 0, "MP3 output has no decodable audio");
    }
}
