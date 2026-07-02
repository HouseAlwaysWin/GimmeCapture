using GimmeCapture.Services.Core.Media;
using NAudio.Wave;
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
