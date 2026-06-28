using GimmeCapture.Services.Core.Media.NativeFFmpeg;

namespace GimmeCapture.Tests;

public class LibavClipExporterScaleTests
{
    [Fact]
    public void ZeroMaxHeight_KeepsSourceUnchanged()
    {
        Assert.Equal((1920, 1080), LibavClipExporter.ScaleToMaxHeight(1920, 1080, 0));
    }

    [Fact]
    public void SourceShorterThanMax_KeepsUnchanged()
    {
        Assert.Equal((1280, 720), LibavClipExporter.ScaleToMaxHeight(1280, 720, 1080));
    }

    [Fact]
    public void Downscale_PreservesAspect_16x9()
    {
        // 1920x1080 -> height 720 keeps 16:9 => 1280x720.
        Assert.Equal((1280, 720), LibavClipExporter.ScaleToMaxHeight(1920, 1080, 720));
    }

    [Fact]
    public void Downscale_SnapsToEvenDimensions()
    {
        // 1920x1080 -> 480 high: width 853.33 -> rounds to 853 -> snapped even 852.
        var (w, h) = LibavClipExporter.ScaleToMaxHeight(1920, 1080, 480);
        Assert.Equal(0, w % 2);
        Assert.Equal(0, h % 2);
        Assert.Equal(480, h);
        Assert.Equal(852, w);
    }

    [Theory]
    [InlineData(0, 1080, 720)]
    [InlineData(1920, 0, 720)]
    public void InvalidDimensions_ReturnedUnchanged(int width, int height, int maxHeight)
    {
        Assert.Equal((width, height), LibavClipExporter.ScaleToMaxHeight(width, height, maxHeight));
    }
}
