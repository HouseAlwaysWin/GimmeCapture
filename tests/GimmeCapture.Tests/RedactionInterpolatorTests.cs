using GimmeCapture.Models;
using GimmeCapture.Services.Core.Rendering;

namespace GimmeCapture.Tests;

public class RedactionInterpolatorTests
{
    private static RedactionKeyframe Kf(double t, double x, double y, double w, double h) =>
        new() { TimeSeconds = t, X = x, Y = y, Width = w, Height = h };

    // Component-wise comparison so floating-point lerp rounding (e.g. 0.2 + 0.1 != 0.3) doesn't make
    // these brittle — the record struct's exact equality would.
    private static void AssertBox(double x, double y, double w, double h, RedactionBox? actual)
    {
        Assert.NotNull(actual);
        Assert.Equal(x, actual!.Value.X, 6);
        Assert.Equal(y, actual.Value.Y, 6);
        Assert.Equal(w, actual.Value.Width, 6);
        Assert.Equal(h, actual.Value.Height, 6);
    }

    [Fact]
    public void NoKeyframes_ReturnsNull()
    {
        Assert.Null(RedactionInterpolator.EvaluateAt(new RedactionTrack(), 1.0));
    }

    [Fact]
    public void SingleKeyframe_IsConstantAtAllTimes()
    {
        var track = new RedactionTrack();
        track.Keyframes.Add(Kf(5.0, 0.2, 0.3, 0.1, 0.4));

        AssertBox(0.2, 0.3, 0.1, 0.4, RedactionInterpolator.EvaluateAt(track, 0.0));
        AssertBox(0.2, 0.3, 0.1, 0.4, RedactionInterpolator.EvaluateAt(track, 5.0));
        AssertBox(0.2, 0.3, 0.1, 0.4, RedactionInterpolator.EvaluateAt(track, 100.0));
    }

    [Fact]
    public void BeforeFirst_MultiKeyframe_IsNull()
    {
        var track = new RedactionTrack();
        track.Keyframes.Add(Kf(2.0, 0.1, 0.1, 0.2, 0.2));
        track.Keyframes.Add(Kf(4.0, 0.5, 0.5, 0.2, 0.2));

        // Bounded to [2,4]: before the first keyframe there is no redaction.
        Assert.Null(RedactionInterpolator.EvaluateAt(track, 0.0));
        // …but exactly at the first keyframe the box is present.
        AssertBox(0.1, 0.1, 0.2, 0.2, RedactionInterpolator.EvaluateAt(track, 2.0));
    }

    [Fact]
    public void AfterLast_MultiKeyframe_IsNull()
    {
        var track = new RedactionTrack();
        track.Keyframes.Add(Kf(2.0, 0.1, 0.1, 0.2, 0.2));
        track.Keyframes.Add(Kf(4.0, 0.5, 0.5, 0.3, 0.3));

        // Bounded to [2,4]: after the last keyframe there is no redaction.
        Assert.Null(RedactionInterpolator.EvaluateAt(track, 10.0));
        AssertBox(0.5, 0.5, 0.3, 0.3, RedactionInterpolator.EvaluateAt(track, 4.0));
    }

    [Fact]
    public void Midpoint_LerpsEveryEdge()
    {
        var track = new RedactionTrack();
        track.Keyframes.Add(Kf(0.0, 0.0, 0.0, 0.2, 0.4));
        track.Keyframes.Add(Kf(2.0, 0.4, 0.2, 0.4, 0.8));

        AssertBox(0.2, 0.1, 0.3, 0.6, RedactionInterpolator.EvaluateAt(track, 1.0));
    }

    [Fact]
    public void QuarterPoint_LerpsProportionally()
    {
        var track = new RedactionTrack();
        track.Keyframes.Add(Kf(0.0, 0.0, 0.0, 0.0, 0.0));
        track.Keyframes.Add(Kf(4.0, 0.8, 0.4, 0.4, 0.2));

        AssertBox(0.2, 0.1, 0.1, 0.05, RedactionInterpolator.EvaluateAt(track, 1.0));
    }

    [Fact]
    public void UnsortedKeyframes_AreHandledChronologically()
    {
        var track = new RedactionTrack();
        // Added out of time order on purpose.
        track.Keyframes.Add(Kf(4.0, 0.5, 0.5, 0.2, 0.2));
        track.Keyframes.Add(Kf(0.0, 0.1, 0.1, 0.2, 0.2));

        // Midway should interpolate between the t=0 and t=4 boxes, not the author order.
        AssertBox(0.3, 0.3, 0.2, 0.2, RedactionInterpolator.EvaluateAt(track, 2.0));

        // Evaluate must not have reordered the caller's list.
        Assert.Equal(4.0, track.Keyframes[0].TimeSeconds);
        Assert.Equal(0.0, track.Keyframes[1].TimeSeconds);
    }
}
