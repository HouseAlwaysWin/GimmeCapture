using GimmeCapture.Models;
using GimmeCapture.Services.Core.Media;
using GimmeCapture.ViewModels.Main;
using Xunit;

namespace GimmeCapture.Tests;

public class VideoCompressBitrateTests
{
    [Fact]
    public void Estimate_HigherCrf_IsSmaller()
    {
        long crf18 = CompressEncodeMath.EstimateOutputSizeBytes(1920, 1080, 30, 60, 0, 0, VideoCodec.H264, 18, 128);
        long crf30 = CompressEncodeMath.EstimateOutputSizeBytes(1920, 1080, 30, 60, 0, 0, VideoCodec.H264, 30, 128);
        Assert.True(crf30 < crf18, $"crf30 estimate ({crf30}) should be < crf18 ({crf18})");
    }

    [Fact]
    public void Estimate_H265_SmallerThanH264_AtSameCrf()
    {
        long h264 = CompressEncodeMath.EstimateOutputSizeBytes(1920, 1080, 30, 60, 0, 0, VideoCodec.H264, 23, 128);
        long h265 = CompressEncodeMath.EstimateOutputSizeBytes(1920, 1080, 30, 60, 0, 0, VideoCodec.H265, 23, 128);
        Assert.True(h265 < h264);
    }

    [Fact]
    public void Estimate_Downscale_AndFpsCap_Shrink()
    {
        long full = CompressEncodeMath.EstimateOutputSizeBytes(1920, 1080, 60, 60, 0, 0, VideoCodec.H264, 23, 128);
        long scaled = CompressEncodeMath.EstimateOutputSizeBytes(1920, 1080, 60, 60, 720, 0, VideoCodec.H264, 23, 128);
        long capped = CompressEncodeMath.EstimateOutputSizeBytes(1920, 1080, 60, 60, 0, 30, VideoCodec.H264, 23, 128);
        Assert.True(scaled < full, "downscale should reduce the estimate");
        Assert.True(capped < full, "fps cap should reduce the estimate");
    }

    [Fact]
    public void Estimate_DropAudio_IsSmaller()
    {
        long withAudio = CompressEncodeMath.EstimateOutputSizeBytes(1280, 720, 30, 60, 0, 0, VideoCodec.H264, 23, 128);
        long noAudio = CompressEncodeMath.EstimateOutputSizeBytes(1280, 720, 30, 60, 0, 0, VideoCodec.H264, 23, 0);
        Assert.True(noAudio < withAudio);
    }

    [Theory]
    [InlineData(0, 1080, 60)]
    [InlineData(1920, 0, 60)]
    [InlineData(1920, 1080, 0)]
    public void Estimate_InvalidInputs_ReturnZero(int w, int h, double duration)
    {
        Assert.Equal(0, CompressEncodeMath.EstimateOutputSizeBytes(w, h, 30, duration, 0, 0, VideoCodec.H264, 23, 128));
    }

    [Fact]
    public void TargetBitrate_HitsRequestedSize_WithinMargin()
    {
        // 25 MB over 60 s -> ~3495 kbps total; minus 128 kbps audio, times 0.97 safety.
        int kbps = CompressEncodeMath.ComputeTargetVideoBitrateKbps(25, 60);

        // Re-derive the expected encoded size and assert it is at or under the requested target.
        double producedBytes = (kbps + 128) * 1000.0 / 8.0 * 60.0;
        double targetBytes = 25.0 * 1024 * 1024;
        Assert.True(producedBytes <= targetBytes, $"{producedBytes} should be <= {targetBytes}");
        Assert.True(kbps > 0);
    }

    [Fact]
    public void TargetBitrate_ShorterClip_NeedsHigherBitrate()
    {
        int shortClip = CompressEncodeMath.ComputeTargetVideoBitrateKbps(25, 30);
        int longClip = CompressEncodeMath.ComputeTargetVideoBitrateKbps(25, 120);
        Assert.True(shortClip > longClip);
    }

    [Theory]
    [InlineData(0, 60)]
    [InlineData(25, 0)]
    [InlineData(-5, 60)]
    public void TargetBitrate_InvalidInputs_ReturnZero(double sizeMB, double durationSeconds)
    {
        Assert.Equal(0, CompressEncodeMath.ComputeTargetVideoBitrateKbps(sizeMB, durationSeconds));
    }

    [Fact]
    public void TargetBitrate_NeverBelowFloor()
    {
        // Tiny target over a long duration would compute a near-zero bitrate; the floor keeps it usable.
        int kbps = CompressEncodeMath.ComputeTargetVideoBitrateKbps(1, 100000);
        Assert.True(kbps >= 50);
    }

    [Fact]
    public void Refine_AfterOvershoot_LowersBitrate()
    {
        // First pass aimed at 25 MB / 60 s but produced 30 MB -> refined bitrate must drop.
        int attempted = CompressEncodeMath.ComputeTargetVideoBitrateKbps(25, 60);
        double targetBytes = 25.0 * 1024 * 1024;
        double actualBytes = 30.0 * 1024 * 1024;

        int refined = CompressEncodeMath.RefineTargetVideoBitrateKbps(attempted, actualBytes, targetBytes, 60);
        Assert.True(refined < attempted, $"{refined} should be < {attempted}");
        Assert.True(refined >= 50);
    }

    [Fact]
    public void Refine_BiggerOvershoot_LowersMore()
    {
        int attempted = CompressEncodeMath.ComputeTargetVideoBitrateKbps(25, 60);
        double targetBytes = 25.0 * 1024 * 1024;

        int small = CompressEncodeMath.RefineTargetVideoBitrateKbps(attempted, 28.0 * 1024 * 1024, targetBytes, 60);
        int big = CompressEncodeMath.RefineTargetVideoBitrateKbps(attempted, 40.0 * 1024 * 1024, targetBytes, 60);
        Assert.True(big < small);
    }

    [Theory]
    [InlineData(0, 1000, 800, 60)]
    [InlineData(2000, 0, 800, 60)]
    [InlineData(2000, 1000, 0, 60)]
    [InlineData(2000, 1000, 800, 0)]
    public void Refine_InvalidInputs_ReturnAttempted(int attempted, double actual, double target, double duration)
    {
        Assert.Equal(attempted, CompressEncodeMath.RefineTargetVideoBitrateKbps(attempted, actual, target, duration));
    }
}
