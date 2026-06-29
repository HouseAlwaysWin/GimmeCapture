using System.Text.Json;
using GimmeCapture.Models;
using Xunit;

namespace GimmeCapture.Tests;

public class CompressPresetTests
{
    [Fact]
    public void Preset_RoundTrips_AllFields_ThroughJson()
    {
        var original = new CompressPreset
        {
            Name = "My 720p H.265",
            Codec = VideoCodec.H265,
            MaxHeight = 720,
            MaxFps = 30,
            Preset = "slow",
            Format = "MKV",
            Crf = 26,
            UseTargetSize = true,
            TargetSizeMB = 12.5m,
            DropAudio = true,
            AudioBitrateKbps = 96,
            AudioChannels = 1
        };

        string json = JsonSerializer.Serialize(original);
        CompressPreset? round = JsonSerializer.Deserialize<CompressPreset>(json);

        Assert.NotNull(round);
        Assert.Equal(original.Name, round!.Name);
        Assert.Equal(original.Codec, round.Codec);
        Assert.Equal(original.MaxHeight, round.MaxHeight);
        Assert.Equal(original.MaxFps, round.MaxFps);
        Assert.Equal(original.Preset, round.Preset);
        Assert.Equal(original.Format, round.Format);
        Assert.Equal(original.Crf, round.Crf);
        Assert.Equal(original.UseTargetSize, round.UseTargetSize);
        Assert.Equal(original.TargetSizeMB, round.TargetSizeMB);
        Assert.Equal(original.DropAudio, round.DropAudio);
        Assert.Equal(original.AudioBitrateKbps, round.AudioBitrateKbps);
        Assert.Equal(original.AudioChannels, round.AudioChannels);
    }
}
