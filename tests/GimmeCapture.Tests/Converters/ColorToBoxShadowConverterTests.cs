using System.Globalization;
using Avalonia.Media;
using GimmeCapture.Converters;

namespace GimmeCapture.Tests;

public class ColorToBoxShadowConverterTests
{
    private readonly ColorToBoxShadowConverter _sut = new();

    private object? Convert(object? value) =>
        _sut.Convert(value, typeof(BoxShadows), null, CultureInfo.InvariantCulture);

    [Fact]
    public void TransparentColor_ProducesNoShadow()
    {
        // Colors.Transparent is 0x00FFFFFF — WHITE with zero alpha. Without the alpha guard the converter
        // force-sets alpha 0x80 and turns it into a visible white halo (the stray glow that lingered around a
        // finalized / recording selection, since SelectionShadowColor => Transparent once the region is Selected).
        var shadows = Assert.IsType<BoxShadows>(Convert(Colors.Transparent));
        Assert.Equal(0, shadows.Count);
    }

    [Fact]
    public void ZeroAlphaColoredInput_ProducesNoShadow()
    {
        var shadows = Assert.IsType<BoxShadows>(Convert(Color.FromArgb(0, 200, 30, 30)));
        Assert.Equal(0, shadows.Count);
    }

    [Fact]
    public void OpaqueColor_ProducesHalfAlphaGlow()
    {
        var shadows = Assert.IsType<BoxShadows>(Convert(Colors.Red));
        Assert.Equal(1, shadows.Count);

        var shadow = shadows[0];
        Assert.Equal(0x80, shadow.Color.A); // 50% opacity
        Assert.Equal(0xFF, shadow.Color.R); // preserves the source hue (red)
        Assert.Equal(0x00, shadow.Color.G);
        Assert.Equal(0x00, shadow.Color.B);
        Assert.Equal(10d, shadow.Blur);
        Assert.Equal(1d, shadow.Spread);
    }

    [Fact]
    public void NonColorValue_ReturnsNull()
    {
        Assert.Null(Convert("not a color"));
    }
}
