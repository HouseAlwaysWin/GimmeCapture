using GimmeCapture.Views.Main;

namespace GimmeCapture.Tests;

public class PinFitMathTests
{
    // Use margins of 1.0 so the expected scale math is exact and easy to reason about;
    // the production defaults (0.95 / 0.90) are covered separately below.
    private const double FullW = 1.0;
    private const double FullH = 1.0;

    [Fact]
    public void ComputeFitScale_SmallerThanWorkArea_ReturnsOne()
    {
        // 400x300 content fits inside a 1920x1080 work area -> never enlarge.
        double fit = PinFitMath.ComputeFitScale(400, 300, 1920, 1080, FullW, FullH);
        Assert.Equal(1.0, fit);
    }

    [Fact]
    public void ComputeFitScale_WiderThanWorkArea_ScalesToWidthAspectPreserved()
    {
        // 4000 wide vs 2000 work area, height fits -> width is the limiting dimension.
        double fit = PinFitMath.ComputeFitScale(4000, 500, 2000, 1080, FullW, FullH);
        Assert.Equal(0.5, fit, 6); // 2000 / 4000
    }

    [Fact]
    public void ComputeFitScale_TallerThanWorkArea_ScalesToHeightAspectPreserved()
    {
        // 8000 tall (scrolling stitch) vs 2000 work area, width fits -> height limits.
        double fit = PinFitMath.ComputeFitScale(500, 8000, 1920, 2000, FullW, FullH);
        Assert.Equal(0.25, fit, 6); // 2000 / 8000
    }

    [Fact]
    public void ComputeFitScale_LargerInBothDimensions_PicksSmallerScale()
    {
        // 4000x8000 vs 2000x2000: width factor 0.5, height factor 0.25 -> min wins.
        double fit = PinFitMath.ComputeFitScale(4000, 8000, 2000, 2000, FullW, FullH);
        Assert.Equal(0.25, fit, 6);
    }

    [Fact]
    public void ComputeFitScale_ScaledSizeFitsWithinWorkArea()
    {
        double contentW = 4000, contentH = 8000, workW = 2000, workH = 2000;
        double fit = PinFitMath.ComputeFitScale(contentW, contentH, workW, workH, FullW, FullH);

        Assert.True(contentW * fit <= workW + 1e-6);
        Assert.True(contentH * fit <= workH + 1e-6);
    }

    [Theory]
    [InlineData(0, 100, 100, 100)]
    [InlineData(100, 0, 100, 100)]
    [InlineData(100, 100, 0, 100)]
    [InlineData(100, 100, 100, 0)]
    [InlineData(-50, 100, 100, 100)]
    public void ComputeFitScale_NonPositiveInput_ReturnsOne(
        double contentW, double contentH, double workW, double workH)
    {
        double fit = PinFitMath.ComputeFitScale(contentW, contentH, workW, workH, FullW, FullH);
        Assert.Equal(1.0, fit);
    }

    [Fact]
    public void ComputeFitScale_DefaultMargins_LeaveSlackAroundContent()
    {
        // Content exactly the work-area size still gets scaled down because the
        // default margins (0.95 / 0.90) reserve slack for chrome / taskbar.
        double fit = PinFitMath.ComputeFitScale(2000, 1000, 2000, 1000);
        Assert.True(fit < 1.0);
        Assert.Equal(0.90, fit, 6); // min(0.95, 0.90)
    }
}
