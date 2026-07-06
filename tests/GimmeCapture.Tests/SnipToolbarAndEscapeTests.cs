using Avalonia;
using GimmeCapture.Models;
using GimmeCapture.ViewModels.Main;
using Xunit;

namespace GimmeCapture.Tests;

// Pure logic behind the two snip changes: the toolbar's horizontal placement (top-left/center/right, DPI- and
// multi-monitor-agnostic since it works in the active-screen Rect) and the two-stage Esc decision. Both are
// static, so no UI thread / window is needed.
public class SnipToolbarAndEscapeTests
{
    private static readonly Rect Screen = new(0, 0, 1000, 800);
    private const double Margin = 20;
    private const double ToolbarWidth = 200;

    [Fact]
    public void ComputeToolbarLeft_TopLeft_PinsToLeftMargin()
    {
        Assert.Equal(20d, SnipWindowViewModel.ComputeToolbarLeft(Screen, ToolbarWidth, Margin, SnipToolbarPosition.TopLeft));
    }

    [Fact]
    public void ComputeToolbarLeft_TopCenter_Centers()
    {
        Assert.Equal(400d, SnipWindowViewModel.ComputeToolbarLeft(Screen, ToolbarWidth, Margin, SnipToolbarPosition.TopCenter));
    }

    [Fact]
    public void ComputeToolbarLeft_TopRight_PinsToRightMargin()
    {
        Assert.Equal(780d, SnipWindowViewModel.ComputeToolbarLeft(Screen, ToolbarWidth, Margin, SnipToolbarPosition.TopRight));
    }

    [Fact]
    public void ComputeToolbarLeft_HonorsScreenOffset_ForSecondMonitor()
    {
        var second = new Rect(1920, 0, 1000, 800);
        Assert.Equal(1940d, SnipWindowViewModel.ComputeToolbarLeft(second, ToolbarWidth, Margin, SnipToolbarPosition.TopLeft));
        Assert.Equal(2320d, SnipWindowViewModel.ComputeToolbarLeft(second, ToolbarWidth, Margin, SnipToolbarPosition.TopCenter));
        Assert.Equal(2700d, SnipWindowViewModel.ComputeToolbarLeft(second, ToolbarWidth, Margin, SnipToolbarPosition.TopRight));
    }

    [Theory]
    [InlineData(SnipToolbarPosition.TopLeft)]
    [InlineData(SnipToolbarPosition.TopCenter)]
    [InlineData(SnipToolbarPosition.TopRight)]
    public void ComputeToolbarLeft_TooWide_PinsToLeftMargin(SnipToolbarPosition pos)
    {
        // 980 >= 1000 - 2*20 (=960): can't fit with both margins, so it pins to the left margin regardless.
        Assert.Equal(20d, SnipWindowViewModel.ComputeToolbarLeft(Screen, 980, Margin, pos));
    }

    [Theory]
    [InlineData(SnipState.Selected, true, true)]    // a box is drawn -> first Esc clears it, stays in draw mode
    [InlineData(SnipState.Selected, false, false)]  // finalized but empty -> Esc closes
    [InlineData(SnipState.Selecting, true, false)]  // mid-draw / manual mode -> Esc closes (not a finalized box)
    [InlineData(SnipState.Detecting, true, false)]  // auto-detect -> Esc closes
    [InlineData(SnipState.Detecting, false, false)]
    [InlineData(SnipState.Idle, false, false)]
    public void ShouldClearBoxToDraw_OnlyWhenSelectedWithBox(SnipState state, bool hasBox, bool expected)
    {
        Assert.Equal(expected, SnipWindowViewModel.ShouldClearBoxToDraw(state, hasBox));
    }
}
