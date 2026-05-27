using System;
using Avalonia;
using GimmeCapture.Services.Core.Interaction;

namespace GimmeCapture.Tests;

public class HoverRectangleAnimationControllerTests
{
    [Fact]
    public void SetTarget_FirstHover_ShowsTargetImmediately()
    {
        var controller = new HoverRectangleAnimationController(TimeSpan.FromMilliseconds(160), -14);
        var target = new Rect(10, 20, 100, 80);

        controller.SetTarget(target);

        Assert.True(controller.IsVisible);
        Assert.Equal(target, controller.CurrentRect);
    }

    [Fact]
    public void Advance_WhenTargetChanges_InterpolatesRectAndDashOffset()
    {
        var controller = new HoverRectangleAnimationController(TimeSpan.FromMilliseconds(160), -14);
        controller.SetTarget(new Rect(0, 0, 100, 80));
        controller.SetTarget(new Rect(200, 120, 120, 90));

        controller.Advance(TimeSpan.FromMilliseconds(80));

        Assert.True(controller.CurrentRect.X > 0 && controller.CurrentRect.X < 200);
        Assert.True(controller.CurrentRect.Y > 0 && controller.CurrentRect.Y < 120);
        Assert.True(controller.DashOffset < 0);

        controller.Advance(TimeSpan.FromMilliseconds(80));

        Assert.Equal(new Rect(200, 120, 120, 90), controller.CurrentRect);
    }

    [Fact]
    public void Hide_ClearsVisibleState()
    {
        var controller = new HoverRectangleAnimationController(TimeSpan.FromMilliseconds(160), -14);
        controller.SetTarget(new Rect(10, 10, 40, 40));

        controller.Hide();

        Assert.False(controller.IsVisible);
        Assert.Equal(default, controller.CurrentRect);
    }
}
