using Avalonia;
using GimmeCapture.Views.Controls;

namespace GimmeCapture.Tests;

/// <summary>
/// Where <see cref="CandidateBoxesLayer"/> runs a box's stroke: where a Border at the box, set on a Canvas by its
/// corner, draws its own once layout rounding has placed it. The pictures are compared in
/// <see cref="CandidateBoxesLayerRenderTests"/>, opt-in; this pins the arithmetic.
/// </summary>
public sealed class CandidateBoxesLayerTests
{
    [Fact]
    public void AtOneToOne_TheCornerRoundsToAPixel_TheSizeUp_AndTheStrokeRunsHalfAPixelIn()
    {
        Rect stroke = CandidateBoxesLayer.StrokeRect(new Rect(10.4, 20.6, 30.2, 10.1), 1.0, 1.0, useLayoutRounding: true);

        Assert.Equal(new Rect(10.5, 21.5, 30, 10), stroke);
    }

    [Fact]
    public void AtAQuarterMore_ItRoundsToDevicePixels_AndTheStrokeIsOneOfThem()
    {
        double thickness = CandidateBoxesLayer.StrokeThickness(1, 1.25, useLayoutRounding: true);
        Rect stroke = CandidateBoxesLayer.StrokeRect(new Rect(10.4, 20.6, 30.2, 10.1), 1.25, thickness, useLayoutRounding: true);

        Assert.Equal(0.8, thickness, 9); // one device pixel
        // Corner 13, 26 device pixels; size 38 x 13 rounded up; less half the stroke.
        Assert.Equal(10.8, stroke.X, 9);
        Assert.Equal(21.2, stroke.Y, 9);
        Assert.Equal(29.6, stroke.Width, 9);
        Assert.Equal(9.6, stroke.Height, 9);
    }

    [Fact]
    public void WithoutLayoutRounding_TheBoxIsTakenAsItIs()
    {
        Rect stroke = CandidateBoxesLayer.StrokeRect(new Rect(10.4, 20.6, 30.2, 10.1), 1.25, 2, useLayoutRounding: false);

        Assert.Equal(11.4, stroke.X, 9);
        Assert.Equal(21.6, stroke.Y, 9);
        Assert.Equal(28.2, stroke.Width, 9);
        Assert.Equal(8.1, stroke.Height, 9);
        Assert.Equal(2, CandidateBoxesLayer.StrokeThickness(2, 1.25, useLayoutRounding: false));
    }
}
