using GimmeCapture.ViewModels.Floating;
using Xunit;

namespace GimmeCapture.Tests.ViewModels;

/// <summary>
/// The frames a pinned recording's seek bar shows as it is dragged. It showed nothing until the drag had rested 300 ms,
/// as each paused seek opened the file again (130-195 ms); from the decoder a paused pin keeps, a step takes 10-20 ms.
/// </summary>
public class FloatingVideoScrubTests
{
    private const long Never = long.MinValue / 2;

    [Fact]
    public void TheFirstMoveOfADrag_ShowsItsFrameAtOnce()
    {
        Assert.Equal(12.5, FloatingVideoViewModel.ScrubFrameTarget(1_000, Never, 12.5, 30));
    }

    [Fact]
    public void FramesWhileDragging_AreShownAtMostOneAnInterval()
    {
        int interval = FloatingVideoViewModel.ScrubFrameIntervalMs;

        Assert.Null(FloatingVideoViewModel.ScrubFrameTarget(1_000 + interval - 1, 1_000, 13.0, 30));
        Assert.Equal(13.0, FloatingVideoViewModel.ScrubFrameTarget(1_000 + interval, 1_000, 13.0, 30));
    }

    [Fact]
    public void BeforeTheDurationIsKnown_NoneIsShown()
    {
        // A paused seek then still opens the file, which reports the duration: the rested drag's seek does that.
        Assert.Null(FloatingVideoViewModel.ScrubFrameTarget(1_000, Never, 5.0, 0));
    }

    [Fact]
    public void AtTheEnd_TheFrameIsTheLast_NotAWrapToTheStart()
    {
        // A seek to the end wraps back to the start, which would move the thumb away from under the pointer.
        double? target = FloatingVideoViewModel.ScrubFrameTarget(1_000, Never, 30.0, 30);

        Assert.NotNull(target);
        Assert.True(target < 30.0 && target > 29.99);
        Assert.Equal(0, FloatingVideoViewModel.ScrubFrameTarget(1_000, Never, -1.0, 30));
    }
}
