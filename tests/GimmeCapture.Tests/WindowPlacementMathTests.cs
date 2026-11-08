using Avalonia;
using GimmeCapture.Views.Main;

namespace GimmeCapture.Tests;

public class WindowPlacementMathTests
{
    private static readonly Size Preferred = new(1180, 760);
    private static readonly Size Minimum = new(1000, 560);

    [Fact]
    public void ARoomyScreen_GetsThePreferredSize_Centred()
    {
        var (size, position) = WindowPlacementMath.FitCentred(Preferred, Minimum, new PixelRect(0, 0, 1920, 1040), 1.0);

        Assert.Equal(Preferred, size);
        // The frame adds 16 x 40 around the content.
        Assert.Equal(new PixelPoint((1920 - 1196) / 2, (1040 - 800) / 2), position);
    }

    [Fact]
    public void ALaptopScreenAt150Percent_ShrinksTheHeightToFit_AndKeepsTheTitleBarOnScreen()
    {
        // 1920 x 1080 at 150% leaves 1280 x 693 logical pixels above the taskbar.
        var (size, position) = WindowPlacementMath.FitCentred(Preferred, Minimum, new PixelRect(0, 0, 1920, 1040), 1.5);

        Assert.Equal(1180, size.Width);
        Assert.Equal((1040 / 1.5) - 40, size.Height, 3);
        Assert.Equal(0, position.Y);
    }

    [Fact]
    public void AScreenLeftOfThePrimary_IsCentredOnInItsOwnCoordinates()
    {
        var (_, position) = WindowPlacementMath.FitCentred(Preferred, Minimum, new PixelRect(-2560, 100, 2560, 1400), 1.0);

        Assert.Equal(new PixelPoint(-2560 + ((2560 - 1196) / 2), 100 + ((1400 - 800) / 2)), position);
    }

    [Fact]
    public void AScreenSmallerThanTheMinimum_KeepsTheMinimum_StartingAtItsTopLeft()
    {
        var (size, position) = WindowPlacementMath.FitCentred(Preferred, Minimum, new PixelRect(10, 20, 800, 500), 1.0);

        Assert.Equal(Minimum, size);
        Assert.Equal(new PixelPoint(10, 20), position);
    }
}
