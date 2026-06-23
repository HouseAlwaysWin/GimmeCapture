namespace GimmeCapture.Tests;

public class IntParameterValidatorTests
{
    [Theory]
    [InlineData(100, 512)]    // below min -> min
    [InlineData(512, 512)]    // at min
    [InlineData(4096, 4096)]  // within range
    [InlineData(8192, 8192)]  // at max
    [InlineData(99999, 8192)] // above max -> max
    public void ClampLlamaContextSize_ClampsTo512To8192(int input, int expected)
    {
        Assert.Equal(expected, IntParameterValidator.ClampLlamaContextSize(input));
    }

    [Theory]
    [InlineData(0, 1)]     // below min -> min
    [InlineData(1, 1)]     // at min
    [InlineData(60, 60)]   // within range
    [InlineData(120, 120)] // at max
    [InlineData(999, 120)] // above max -> max
    public void ClampPlaybackFps_ClampsTo1To120(int input, int expected)
    {
        Assert.Equal(expected, IntParameterValidator.ClampPlaybackFps(input));
    }

    [Theory]
    [InlineData(-5, 0)]  // negative -> 0
    [InlineData(0, 0)]   // at floor
    [InlineData(40, 40)] // no upper bound
    public void ClampGpuLayers_FloorsAtZero(int input, int expected)
    {
        Assert.Equal(expected, IntParameterValidator.ClampGpuLayers(input));
    }
}
