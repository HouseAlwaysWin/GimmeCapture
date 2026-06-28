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
}
