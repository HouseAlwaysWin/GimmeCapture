using Avalonia.Media;
using GimmeCapture.Services.Core.Infrastructure;

namespace GimmeCapture.Tests;

public class ThemeColorPaletteTests
{
    [Fact]
    public void GetDeepColor_Gold_ReturnsGoldDeep()
    {
        Assert.Equal(Color.Parse("#8B7500"), ThemeColorPalette.GetDeepColor(Color.Parse("#D4AF37")));
    }

    [Fact]
    public void GetDeepColor_Silver_ReturnsSilverDeep()
    {
        Assert.Equal(Color.Parse("#606060"), ThemeColorPalette.GetDeepColor(Color.Parse("#E0E0E0")));
    }

    [Fact]
    public void GetDeepColor_UnknownColor_ReturnsDefaultDeepRed()
    {
        Assert.Equal(Color.Parse("#900000"), ThemeColorPalette.GetDeepColor(Color.Parse("#123456")));
    }

    [Fact]
    public void GetDeepColor_DefaultRedAccent_ReturnsDefaultDeepRed()
    {
        // The app's default accent (#FF0000-ish) is not one of the named colors,
        // so it falls through to the default deep red.
        Assert.Equal(Color.Parse("#900000"), ThemeColorPalette.GetDeepColor(Color.Parse("#FF0000")));
    }
}
