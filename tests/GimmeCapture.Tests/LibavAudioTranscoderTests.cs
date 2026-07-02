using System;
using System.IO;
using GimmeCapture.Models;
using GimmeCapture.Services.Core.Media.NativeFFmpeg;
using Xunit;

namespace GimmeCapture.Tests;

/// <summary>
/// Characterization tests for the audio transcoders (<see cref="LibavAacTranscoder"/>,
/// <see cref="LibavOpusTranscoder"/>, <see cref="LibavPinAudioPcmDecoder"/>), which previously had zero
/// direct coverage. GATED like <see cref="CompressIntegrationTests"/>: a no-op pass unless
/// COMPRESS_IT_OUTDIR is set, so it never runs on CI / Linux. Requires Windows + the bundled FFmpeg libs
/// (aac + libopus encoders). Drive via <c>scripts/test-compress.ps1</c>, or set the env var and run:
///   dotnet test --filter FullyQualifiedName~LibavAudioTranscoder
/// The WAV input is synthesized in-process (no source clip needed) via <see cref="WavTestAudio"/>.
/// </summary>
public class LibavAudioTranscoderTests
{
    private static string? OutDir => Environment.GetEnvironmentVariable("COMPRESS_IT_OUTDIR");
    private static bool Enabled => !string.IsNullOrWhiteSpace(OutDir);

    private static string OutPath(string name)
    {
        Directory.CreateDirectory(OutDir!);
        return Path.Combine(OutDir!, name);
    }

    private static string NewWav(string name, double seconds = 1.0) =>
        WavTestAudio.WriteSineWav(OutPath(name), seconds);

    [Fact]
    public void EncodeWavToM4a_ProducesReadableAacAudio()
    {
        if (!Enabled) return;

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
        if (!Enabled) return;

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
        if (!Enabled) return;

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
        if (!Enabled) return;

        string wav = NewWav("opus_src.wav");
        string ogg = OutPath("opus_out.ogg");

        LibavOpusTranscoder.EncodeWavToOpusOgg(wav, ogg, VideoQuality.Medium);

        Assert.True(File.Exists(ogg) && new FileInfo(ogg).Length > 0, "Opus output not produced");
        var decoded = LibavPinAudioPcmDecoder.Decode(ogg, 0);
        Assert.True(decoded.PcmBytes.Length > 0, "Opus output has no decodable audio");
    }

    [Fact]
    public void RoundTrip_WavToAacToPcm_YieldsStereo16BitPcm()
    {
        if (!Enabled) return;

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
}
