using GimmeCapture.ViewModels.Main;
using Xunit;

namespace GimmeCapture.Tests;

public class VideoCompressBitrateTests
{
    [Fact]
    public void TargetBitrate_HitsRequestedSize_WithinMargin()
    {
        // 25 MB over 60 s -> ~3495 kbps total; minus 128 kbps audio, times 0.97 safety.
        int kbps = MainWindowViewModel.ComputeTargetVideoBitrateKbps(25, 60);

        // Re-derive the expected encoded size and assert it is at or under the requested target.
        double producedBytes = (kbps + 128) * 1000.0 / 8.0 * 60.0;
        double targetBytes = 25.0 * 1024 * 1024;
        Assert.True(producedBytes <= targetBytes, $"{producedBytes} should be <= {targetBytes}");
        Assert.True(kbps > 0);
    }

    [Fact]
    public void TargetBitrate_ShorterClip_NeedsHigherBitrate()
    {
        int shortClip = MainWindowViewModel.ComputeTargetVideoBitrateKbps(25, 30);
        int longClip = MainWindowViewModel.ComputeTargetVideoBitrateKbps(25, 120);
        Assert.True(shortClip > longClip);
    }

    [Theory]
    [InlineData(0, 60)]
    [InlineData(25, 0)]
    [InlineData(-5, 60)]
    public void TargetBitrate_InvalidInputs_ReturnZero(double sizeMB, double durationSeconds)
    {
        Assert.Equal(0, MainWindowViewModel.ComputeTargetVideoBitrateKbps(sizeMB, durationSeconds));
    }

    [Fact]
    public void TargetBitrate_NeverBelowFloor()
    {
        // Tiny target over a long duration would compute a near-zero bitrate; the floor keeps it usable.
        int kbps = MainWindowViewModel.ComputeTargetVideoBitrateKbps(1, 100000);
        Assert.True(kbps >= 50);
    }

    [Fact]
    public void Refine_AfterOvershoot_LowersBitrate()
    {
        // First pass aimed at 25 MB / 60 s but produced 30 MB -> refined bitrate must drop.
        int attempted = MainWindowViewModel.ComputeTargetVideoBitrateKbps(25, 60);
        double targetBytes = 25.0 * 1024 * 1024;
        double actualBytes = 30.0 * 1024 * 1024;

        int refined = MainWindowViewModel.RefineTargetVideoBitrateKbps(attempted, actualBytes, targetBytes, 60);
        Assert.True(refined < attempted, $"{refined} should be < {attempted}");
        Assert.True(refined >= 50);
    }

    [Fact]
    public void Refine_BiggerOvershoot_LowersMore()
    {
        int attempted = MainWindowViewModel.ComputeTargetVideoBitrateKbps(25, 60);
        double targetBytes = 25.0 * 1024 * 1024;

        int small = MainWindowViewModel.RefineTargetVideoBitrateKbps(attempted, 28.0 * 1024 * 1024, targetBytes, 60);
        int big = MainWindowViewModel.RefineTargetVideoBitrateKbps(attempted, 40.0 * 1024 * 1024, targetBytes, 60);
        Assert.True(big < small);
    }

    [Theory]
    [InlineData(0, 1000, 800, 60)]
    [InlineData(2000, 0, 800, 60)]
    [InlineData(2000, 1000, 0, 60)]
    [InlineData(2000, 1000, 800, 0)]
    public void Refine_InvalidInputs_ReturnAttempted(int attempted, double actual, double target, double duration)
    {
        Assert.Equal(attempted, MainWindowViewModel.RefineTargetVideoBitrateKbps(attempted, actual, target, duration));
    }
}
