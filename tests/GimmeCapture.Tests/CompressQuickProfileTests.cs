using System.Linq;
using GimmeCapture.Models;
using GimmeCapture.ViewModels.Main;
using Xunit;

namespace GimmeCapture.Tests;

// Guards the built-in quick-recipe table for the batch compress 輸出設定 tab. Static, so no view model
// construction (or localization) is needed — asserts the recipe values, not the localized display names.
public class CompressQuickProfileTests
{
    private static MainWindowViewModel.CompressQuickProfile[] Profiles =>
        MainWindowViewModel.BuildCompressQuickProfiles();

    [Fact]
    public void FirstIsCustom_AndTheRestAreNot()
    {
        MainWindowViewModel.CompressQuickProfile[] profiles = Profiles;
        Assert.True(profiles.Length >= 4);
        Assert.True(profiles[0].IsCustom);
        Assert.All(profiles.Skip(1), p => Assert.False(p.IsCustom));
    }

    [Fact]
    public void Discord_TargetsTwentyFiveMb_H264_Mp4()
    {
        MainWindowViewModel.CompressQuickProfile discord = Profiles.Single(p => p.Key == "CompressQuickDiscord");
        Assert.True(discord.UseTargetSize);
        Assert.Equal(25m, discord.TargetSizeMB);
        Assert.Equal(VideoCodec.H264, discord.Codec);
        Assert.Equal("MP4", discord.Format);
    }

    [Fact]
    public void Smallest_IsLowResHevc_WithHigherCrf_ThanHighQuality()
    {
        MainWindowViewModel.CompressQuickProfile smallest = Profiles.Single(p => p.Key == "CompressQuickSmallest");
        MainWindowViewModel.CompressQuickProfile highQuality = Profiles.Single(p => p.Key == "CompressQuickHighQuality");
        Assert.Equal(VideoCodec.H265, smallest.Codec);
        Assert.InRange(smallest.MaxHeight, 1, 480);
        Assert.True(smallest.Crf > highQuality.Crf, "smallest should use a higher (worse-quality/smaller) CRF");
    }

    [Fact]
    public void AllNonCustom_UseValidEncoderPresets()
    {
        string[] valid = { "ultrafast", "veryfast", "fast", "medium", "slow" };
        Assert.All(Profiles.Where(p => !p.IsCustom), p => Assert.Contains(p.Preset, valid));
    }

    [Fact]
    public void SmallestAv1_UsesAv1_FullResolution_SlowPreset()
    {
        MainWindowViewModel.CompressQuickProfile av1 = Profiles.Single(p => p.Key == "CompressQuickSmallestAv1");
        Assert.Equal(VideoCodec.Av1, av1.Codec);
        Assert.Equal("slow", av1.Preset);
        Assert.False(av1.UseTargetSize);
        Assert.Equal(1080, av1.MaxHeight);
        Assert.InRange(av1.Crf, 14, 40); // UI (x265) scale; the +8 AV1 offset is applied on encode
    }
}
