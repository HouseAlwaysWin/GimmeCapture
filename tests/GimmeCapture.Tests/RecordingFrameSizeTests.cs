using Avalonia;
using GimmeCapture.Services.Core.Media;
using GimmeCapture.Views.Floating;

namespace GimmeCapture.Tests;

/// <summary>
/// The size a recording's pin decodes and shows it at (<see cref="RecordingFrameSize"/>), and when the pin draws it
/// pixel for pixel (<see cref="FloatingVideoWindow.IsPixelForPixel"/>). The pin was made at the region's size in DIPs
/// and decoded at that many pixels: at 125% a 1134x624 recording played at 907x499, stretched back up, and the
/// border round it took 4 more pixels off, so every frame was smoothed on its way to the screen.
/// </summary>
public class RecordingFrameSizeTests
{
    [Theory]
    [InlineData(907.2, 1.25, 1134)]
    [InlineData(906.4, 1.25, 1132)] // 1133 px: rounded down to even, as encoders need
    [InlineData(640, 1.0, 640)]
    [InlineData(641, 1.0, 640)]
    [InlineData(500, 1.5, 750)]
    public void ARegion_IsRecordedAtItsPixels_RoundedDownToEven(double dips, double scaling, int pixels)
    {
        Assert.Equal(pixels, RecordingFrameSize.EvenPixels(dips, scaling));
    }

    [Fact]
    public void APin_DecodesARecordingAtItsPixels_AndShowsThemOneForOne()
    {
        var (pixelWidth, pixelHeight, dipWidth, dipHeight) = RecordingFrameSize.Pin(907.2, 499.2, 1.25);

        Assert.Equal(1134, pixelWidth);
        Assert.Equal(624, pixelHeight);
        Assert.Equal(907.2, dipWidth, 9);
        Assert.Equal(499.2, dipHeight, 9);
    }

    [Fact]
    public void APin_OfARegionWithAnOddPixelSize_ShowsTheEvenFramesOneForOne()
    {
        var (pixelWidth, _, dipWidth, _) = RecordingFrameSize.Pin(906.4, 499.2, 1.25);

        Assert.Equal(1132, pixelWidth);
        Assert.Equal(1132, dipWidth * 1.25, 9); // 905.6 DIPs, not the region's 906.4
    }

    [Theory]
    [InlineData(0.5, 0.5, 1.0)]
    [InlineData(100, 100, 0)] // no scaling known: as at 100%
    public void ATinyRegion_OrAnUnknownScaling_StillMakesAPin(double width, double height, double scaling)
    {
        var (pixelWidth, pixelHeight, dipWidth, dipHeight) = RecordingFrameSize.Pin(width, height, scaling);

        Assert.True(pixelWidth >= 2 && pixelHeight >= 2);
        Assert.Equal(pixelWidth, dipWidth * (scaling > 0 ? scaling : 1.0), 9);
        Assert.Equal(pixelHeight, dipHeight * (scaling > 0 ? scaling : 1.0), 9);
    }

    [Fact]
    public void AFrameLaidOutAtItsOwnPixels_IsDrawnPixelForPixel()
    {
        Assert.True(FloatingVideoWindow.IsPixelForPixel(new Size(907.2, 499.2), 1.25, new PixelSize(1134, 624)));
        Assert.True(FloatingVideoWindow.IsPixelForPixel(new Size(907.1, 499.3), 1.25, new PixelSize(1134, 624)));
    }

    [Theory]
    [InlineData(904.0, 496.0, 1.25)]  // the border's 4 px taken off, as the pin used to lay it out
    [InlineData(907.2, 499.2, 1.5)]   // moved to a screen at another scaling
    [InlineData(1000.0, 550.0, 1.25)] // resized
    public void AFrameScaledOnItsWay_IsSmoothed(double width, double height, double scaling)
    {
        Assert.False(FloatingVideoWindow.IsPixelForPixel(new Size(width, height), scaling, new PixelSize(1134, 624)));
    }
}
