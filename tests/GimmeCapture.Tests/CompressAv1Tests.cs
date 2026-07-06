using GimmeCapture.Models;
using GimmeCapture.Services.Core.Media.NativeFFmpeg;
using GimmeCapture.ViewModels.Main;
using Xunit;

namespace GimmeCapture.Tests;

// AV1 (SVT-AV1) compress path: the CRF-scale offset, the SVT preset mapping, and that the size estimate
// ranks AV1 below H.265 at equivalent quality. All pure/static — no view model or libav needed.
public class CompressAv1Tests
{
    [Fact]
    public void EncoderScaleCrf_H264AndH265_PassThroughWithClamp()
    {
        Assert.Equal(22, MainWindowViewModel.EncoderScaleCrf(VideoCodec.H264, 22));
        Assert.Equal(18, MainWindowViewModel.EncoderScaleCrf(VideoCodec.H265, 18));
        Assert.Equal(51, MainWindowViewModel.EncoderScaleCrf(VideoCodec.H265, 99)); // clamp to x265 max
        Assert.Equal(1, MainWindowViewModel.EncoderScaleCrf(VideoCodec.H264, 0));   // clamp to min
    }

    [Fact]
    public void EncoderScaleCrf_Av1_AddsOffset_AndClampsToSixtyThree()
    {
        Assert.Equal(22 + MainWindowViewModel.Av1CrfOffset, MainWindowViewModel.EncoderScaleCrf(VideoCodec.Av1, 22));
        Assert.Equal(63, MainWindowViewModel.EncoderScaleCrf(VideoCodec.Av1, 60)); // 60 + 8 = 68 -> clamp 63
    }

    [Theory]
    [InlineData("ultrafast", "10")]
    [InlineData("veryfast", "8")]
    [InlineData("fast", "7")]
    [InlineData("medium", "6")]
    [InlineData("slow", "4")]
    [InlineData("slower", "2")]
    [InlineData("something-else", "6")] // unknown -> medium-equivalent default
    public void MapPreset_Av1_UsesIntegerScale(string preset, string expected)
    {
        Assert.Equal(expected, LibavClipExporter.MapPreset(preset, isAv1: true));
    }

    [Fact]
    public void MapPreset_NonAv1_PassesWordThrough()
    {
        Assert.Equal("slow", LibavClipExporter.MapPreset("slow", isAv1: false));
        Assert.Equal("veryfast", LibavClipExporter.MapPreset(null, isAv1: false)); // default word
    }

    [Theory]
    [InlineData("slow", "2")]   // libaom cpu-used: 0 = slowest/best .. 8 = fastest
    [InlineData("medium", "4")]
    [InlineData("veryfast", "6")]
    [InlineData("ultrafast", "8")]
    [InlineData("something-else", "4")] // unknown -> medium-equivalent default
    public void MapLibaomCpuUsed_MapsWordToCpuUsedScale(string preset, string expected)
    {
        Assert.Equal(expected, LibavClipExporter.MapLibaomCpuUsed(preset));
    }

    [Fact]
    public void Estimate_Av1_SmallerThanH265_AtEquivalentQuality()
    {
        // Same UI quality (x265 CRF 23): H.265 encodes at 23, AV1 at 23 + offset. AV1 must estimate smaller.
        long h265 = MainWindowViewModel.EstimateOutputSizeBytes(1920, 1080, 30, 60, 0, 0, VideoCodec.H265, 23, 128);
        long av1 = MainWindowViewModel.EstimateOutputSizeBytes(
            1920, 1080, 30, 60, 0, 0, VideoCodec.Av1, MainWindowViewModel.EncoderScaleCrf(VideoCodec.Av1, 23), 128);
        Assert.True(av1 < h265, $"AV1 estimate ({av1}) should be < H.265 ({h265})");
    }
}
