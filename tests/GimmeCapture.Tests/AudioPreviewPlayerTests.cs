using System;
using System.IO;
using GimmeCapture.Services.Core.Media;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Xunit;

namespace GimmeCapture.Tests;

// Pure math of the shared preview-audio player (extracted from the Pin video window): the sample-rate
// retiming used when playing at a non-1× effective speed. Actual device playback is manual-test only.
public class AudioPreviewPlayerTests
{
    [Fact]
    public void CreatePlaybackWaveFormat_NormalSpeed_ReturnsSourceFormat()
    {
        var source = new WaveFormat(48_000, 16, 2);

        WaveFormat result = AudioPreviewPlayer.CreatePlaybackWaveFormat(source, 1.0);

        Assert.Same(source, result);
    }

    [Fact]
    public void CreatePlaybackWaveFormat_ScalesSampleRateBySpeed()
    {
        var source = new WaveFormat(48_000, 16, 2);

        Assert.Equal(96_000, AudioPreviewPlayer.CreatePlaybackWaveFormat(source, 2.0).SampleRate);
        Assert.Equal(24_000, AudioPreviewPlayer.CreatePlaybackWaveFormat(source, 0.5).SampleRate);
        Assert.Equal(72_000, AudioPreviewPlayer.CreatePlaybackWaveFormat(source, 1.5).SampleRate);
    }

    [Fact]
    public void CreatePlaybackWaveFormat_ClampsExtremeSpeeds()
    {
        var source = new WaveFormat(48_000, 16, 2);

        // Speed clamps to [0.25, 4]; the resulting rate clamps to [8k, 192k].
        Assert.Equal(192_000, AudioPreviewPlayer.CreatePlaybackWaveFormat(source, 100).SampleRate);
        Assert.Equal(12_000, AudioPreviewPlayer.CreatePlaybackWaveFormat(source, 0.01).SampleRate);
        Assert.Equal(8_000, AudioPreviewPlayer.CreatePlaybackWaveFormat(new WaveFormat(8_000, 16, 1), 0.25).SampleRate);
    }

    [Fact]
    public void CreatePlaybackWaveFormat_PreservesBitsAndChannels()
    {
        var source = new WaveFormat(44_100, 24, 1);

        WaveFormat result = AudioPreviewPlayer.CreatePlaybackWaveFormat(source, 2.0);

        Assert.Equal(24, result.BitsPerSample);
        Assert.Equal(1, result.Channels);
    }

    // A constant 16-bit PCM signal of 16384 → 0.5 in float (16384/32768). Mono, 48 kHz.
    private static WaveStream MakeConstantPcm()
    {
        var bytes = new byte[200 * 2];
        for (int i = 0; i < 200; i++)
        {
            bytes[i * 2] = 0x00;     // 16384 = 0x4000, little-endian
            bytes[i * 2 + 1] = 0x40;
        }

        return new RawSourceWaveStream(new MemoryStream(bytes), new WaveFormat(48_000, 16, 1));
    }

    private static float ReadFirstSample(IWaveProvider provider)
    {
        var buffer = new byte[16];
        int read = provider.Read(buffer, 0, buffer.Length);
        Assert.True(read >= 4);
        return BitConverter.ToSingle(buffer, 0); // SampleToWaveProvider emits 32-bit IEEE float
    }

    [Theory]
    [InlineData(1.0f, 0.5f)]    // full volume → unchanged (0.5)
    [InlineData(0.5f, 0.25f)]   // half volume → 0.25
    [InlineData(0.0f, 0.0f)]    // muted → silence
    public void BuildVolumePipeline_ScalesSamplesByVolume(float volume, float expected)
    {
        (VolumeSampleProvider _, IWaveProvider output) = AudioPreviewPlayer.BuildVolumePipeline(MakeConstantPcm(), volume);

        float sample = ReadFirstSample(output);

        Assert.InRange(sample, expected - 0.01f, expected + 0.01f);
    }

    [Fact]
    public void BuildVolumePipeline_VolumeChangeAppliesLive()
    {
        // The provider reference is what AudioPreviewPlayer.Volume mutates while audio is playing.
        (VolumeSampleProvider vol, IWaveProvider output) = AudioPreviewPlayer.BuildVolumePipeline(MakeConstantPcm(), 1.0f);

        vol.Volume = 0.25f; // e.g. user drags the slider mid-playback

        Assert.InRange(ReadFirstSample(output), 0.115f, 0.135f); // 0.5 * 0.25 = 0.125
    }

    [Fact]
    public void Volume_ClampsToUnitRange()
    {
        // Volume scales samples in-stream (VolumeSampleProvider), never the output device's session
        // volume; the setter just clamps + stores when nothing is playing (no device touched here).
        using var player = new AudioPreviewPlayer();

        player.Volume = 2.5f;
        Assert.Equal(1f, player.Volume);

        player.Volume = -1f;
        Assert.Equal(0f, player.Volume);

        player.Volume = 0.3f;
        Assert.Equal(0.3f, player.Volume, 3);
    }
}
