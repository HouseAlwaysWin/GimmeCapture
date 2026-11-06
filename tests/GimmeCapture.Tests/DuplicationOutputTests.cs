using GimmeCapture.Services.Core.Media.NativeFFmpeg;

namespace GimmeCapture.Tests;

/// <summary>
/// Which display Desktop Duplication grabs a recording region from: the one that holds all of it, upright and on the
/// desktop. Anything else is left to gdigrab, which takes the whole virtual screen.
/// </summary>
public sealed class DuplicationOutputTests
{
    private const int Identity = 1;          // DXGI_MODE_ROTATION_IDENTITY
    private const int Rotate90 = 2;          // DXGI_MODE_ROTATION_ROTATE90

    // A laptop panel and, to its left, a monitor on another adapter (its desktop coordinates are negative).
    private static readonly DuplicationOutput Panel = new(0, 0, 0, 0, 1920, 1080, true, Identity);
    private static readonly DuplicationOutput Left = new(1, 0, -2560, 0, 0, 1440, true, Identity);

    [Fact]
    public void ARegionOnOneDisplay_IsTakenFromThatDisplay()
    {
        Assert.Equal(Panel, DuplicationOutput.Holding(new[] { Left, Panel }, 100, 80, 1450, 900));
        Assert.Equal(Left, DuplicationOutput.Holding(new[] { Panel, Left }, -2000, 300, 800, 600));
    }

    [Fact]
    public void ARegionToTheEdges_IsHeldByItsDisplay()
    {
        Assert.Equal(Panel, DuplicationOutput.Holding(new[] { Panel }, 0, 0, 1920, 1080));
    }

    [Theory]
    [InlineData(-100, 80, 400, 300)]   // across the two displays
    [InlineData(1800, 80, 400, 300)]   // past the panel's right edge
    [InlineData(100, 1000, 400, 300)]  // past its bottom
    public void ARegionNoDisplayHoldsWhole_IsNotDuplicated(int x, int y, int width, int height)
    {
        Assert.Null(DuplicationOutput.Holding(new[] { Left, Panel }, x, y, width, height));
    }

    [Fact]
    public void ATurnedDisplay_IsNotDuplicated()
    {
        var turned = Panel with { Rotation = Rotate90 };

        Assert.Null(DuplicationOutput.Holding(new[] { turned }, 100, 80, 400, 300));
    }

    [Fact]
    public void ADisplayOffTheDesktop_IsNotDuplicated()
    {
        var detached = Panel with { AttachedToDesktop = false };

        Assert.Null(DuplicationOutput.Holding(new[] { detached }, 100, 80, 400, 300));
    }

    [Fact]
    public void AnUnspecifiedRotation_CountsAsUpright()
    {
        var unspecified = Panel with { Rotation = 0 };

        Assert.Equal(unspecified, DuplicationOutput.Holding(new[] { unspecified }, 100, 80, 400, 300));
    }
}
