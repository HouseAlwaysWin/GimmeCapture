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
    public void OnTheGraphicsCard_TheDashesFlowAtTheFullRate_HoweverLongThePointerRests()
    {
        var controller = new HoverRectangleAnimationController(TimeSpan.FromMilliseconds(120), -20);
        controller.SetTarget(new Rect(10, 10, 240, 120));

        controller.Advance(TimeSpan.FromSeconds(5));
        double offset = controller.DashOffset;
        controller.Advance(TimeSpan.FromSeconds(1));

        Assert.Equal(HoverRectangleAnimationController.FullInterval, controller.TickInterval);
        Assert.True(controller.NeedsTicks);
        Assert.Equal(offset - 20, controller.DashOffset, 6);
    }

    [Fact]
    public void Economical_TheDashesStepLessOften_AndStandStillOnceThePointerRests()
    {
        var controller = new HoverRectangleAnimationController(TimeSpan.FromMilliseconds(120), -20) { Economical = true };
        controller.SetTarget(new Rect(10, 10, 240, 120));
        controller.PointerMoved();

        Assert.Equal(HoverRectangleAnimationController.EconomicalInterval, controller.TickInterval);
        controller.Advance(TimeSpan.FromMilliseconds(500));
        Assert.Equal(-10, controller.DashOffset, 6);
        Assert.True(controller.NeedsTicks);

        controller.Advance(TimeSpan.FromMilliseconds(600));
        Assert.False(controller.NeedsTicks);
        double rested = controller.DashOffset;
        controller.Advance(TimeSpan.FromSeconds(1));

        Assert.Equal(rested, controller.DashOffset);
        Assert.True(controller.IsVisible);
        Assert.Equal(new Rect(10, 10, 240, 120), controller.CurrentRect);
    }

    [Fact]
    public void Economical_ThePointerMovingAgain_SetsTheDashesFlowing()
    {
        var controller = new HoverRectangleAnimationController(TimeSpan.FromMilliseconds(120), -20) { Economical = true };
        controller.SetTarget(new Rect(10, 10, 240, 120));
        controller.Advance(TimeSpan.FromSeconds(2));
        Assert.False(controller.NeedsTicks);
        double rested = controller.DashOffset;

        controller.PointerMoved();
        Assert.True(controller.NeedsTicks);
        controller.Advance(TimeSpan.FromMilliseconds(100));

        Assert.Equal(rested - 2, controller.DashOffset, 6);
    }

    [Fact]
    public void Economical_AMoveToANewTarget_RunsAtTheFullRate()
    {
        var controller = new HoverRectangleAnimationController(TimeSpan.FromMilliseconds(120), -20) { Economical = true };
        controller.SetTarget(new Rect(0, 0, 100, 80));
        controller.Advance(TimeSpan.FromSeconds(2));
        Assert.False(controller.NeedsTicks);

        controller.SetTarget(new Rect(200, 120, 120, 90));
        Assert.True(controller.NeedsTicks);
        Assert.Equal(HoverRectangleAnimationController.FullInterval, controller.TickInterval);
        controller.Advance(TimeSpan.FromMilliseconds(60));
        Assert.True(controller.CurrentRect.X > 0 && controller.CurrentRect.X < 200);

        controller.Advance(TimeSpan.FromMilliseconds(80));
        Assert.Equal(new Rect(200, 120, 120, 90), controller.CurrentRect);
        Assert.False(controller.NeedsTicks);
        Assert.Equal(HoverRectangleAnimationController.EconomicalInterval, controller.TickInterval);
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
