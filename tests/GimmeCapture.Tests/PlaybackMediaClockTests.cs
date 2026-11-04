using System;
using GimmeCapture.Services.Core.Media;
using Xunit;

namespace GimmeCapture.Tests;

/// <summary>
/// The clock a looping pin's picture is shown by (<see cref="PlaybackMediaClock"/>): the sound's own position while
/// the sound plays, a stopwatch otherwise, and never a jump ahead between the two. The wall times are the test's.
/// </summary>
public class PlaybackMediaClockTests
{
    private static readonly SoundClockReading Pending = SoundClockReading.Pending;
    private static readonly SoundClockReading None = SoundClockReading.Unavailable;

    private static SoundClockReading At(double seconds) => SoundClockReading.At(seconds);

    [Fact]
    public void WithoutSound_TheStopwatchKeepsTheTime_FromTheFirstFrame()
    {
        var clock = new PlaybackMediaClock();

        Assert.Equal(0.5, clock.Begin(10.0, 0.5, None), 9);
        Assert.False(clock.IsWaitingForSound);
        Assert.Equal(0.75, clock.Now(10.25, None), 9);
    }

    [Fact]
    public void ThePictureHoldsItsFirstFrame_UntilTheSoundStarts_ThenKeepsToIt()
    {
        var clock = new PlaybackMediaClock();

        clock.Begin(0, 0.033, Pending);
        Assert.True(clock.IsWaitingForSound);
        Assert.Equal(0.033, clock.Now(0.120, Pending), 9);
        Assert.Equal(0.033, clock.Now(0.130, At(0)), 9); // the device has not played anything yet

        Assert.Equal(0.033, clock.Now(0.150, At(0.020)), 9); // the first frame stays up until the sound passes it
        Assert.True(clock.IsFollowingSound);
        Assert.Equal(0.040, clock.Now(0.170, At(0.040)), 9);
    }

    [Fact]
    public void ASoundThatDoesNotStart_IsWaitedForASecondAtMost_ThenTheStopwatchGoesOnFromThere()
    {
        var clock = new PlaybackMediaClock();

        clock.Begin(0, 0.1, Pending);
        Assert.Equal(0.1, clock.Now(0.999, Pending), 9);
        Assert.Equal(0.1, clock.Now(1.0, Pending), 9);
        Assert.False(clock.IsWaitingForSound);
        Assert.Equal(0.6, clock.Now(1.5, Pending), 9);
    }

    [Theory]
    [InlineData(0.999, 0.001)] // a device clock a thousandth slow, worse than any real one
    [InlineData(0.98, 0.001)]  // 2% slow: the stopwatch would be 200 ms ahead after ten seconds
    [InlineData(0.5, 0.001)]
    public void TheTimeIsTheSounds_WhenTheSoundRunsSlowerThanTheStopwatch(double soundRate, double within)
    {
        var clock = new PlaybackMediaClock();
        clock.Begin(0, 0, Pending);
        double now = 0;
        for (int i = 1; i <= 300; i++)
        {
            double wall = i / 30.0;
            now = clock.Now(wall, At(wall * soundRate));
        }

        Assert.InRange(now, 10 * soundRate, (10 * soundRate) + within);
    }

    [Fact]
    public void ASoundReportedInSteps_IsFollowedSmoothly()
    {
        // A device reports its position in steps, here 10 ms: the clock runs on between them and never runs back.
        var clock = new PlaybackMediaClock();
        clock.Begin(0, 0, Pending);
        double previous = 0;
        double worst = 0;
        for (int i = 1; i <= 1000; i++)
        {
            double wall = (i * 0.003) + 0.0004;
            double now = clock.Now(wall, At(Math.Floor(wall / 0.010) * 0.010));
            if (wall > 0.5)
            {
                Assert.True(now >= previous, $"the clock went back at {wall:0.000} s");
                worst = Math.Max(worst, Math.Abs(now - wall));
            }

            previous = now;
        }

        Assert.InRange(worst, 0, 0.004); // a step is seen up to a reading late: 3 ms here
    }

    [Fact]
    public void ASoundThatStops_HandsTheTimeBackToTheStopwatch_WithoutAJump()
    {
        var clock = new PlaybackMediaClock();
        clock.Begin(0, 0, Pending);
        clock.Now(0.5, At(0.48));
        double whenStopped = clock.Now(0.6, At(0.58));

        Assert.Equal(whenStopped, clock.Now(0.6, None), 9);
        Assert.Equal(whenStopped + 0.4, clock.Now(1.0, None), 9);
    }

    [Fact]
    public void AStalledSound_IsWaitedFor_ThenLeft_UntilItMovesAgain()
    {
        var clock = new PlaybackMediaClock();
        clock.Begin(0, 0, Pending);
        clock.Now(0.1, At(0.1));
        clock.Now(0.2, At(0.2));

        Assert.Equal(0.25, clock.Now(0.6, At(0.2)), 3);  // runs on a little from the sound, then waits for it
        Assert.Equal(0.25, clock.Now(0.71, At(0.2)), 3); // half a second without moving: the stopwatch goes on from there
        Assert.Equal(0.35, clock.Now(0.81, At(0.2)), 3);
        Assert.False(clock.IsFollowingSound);

        Assert.Equal(0.35, clock.Now(1.0, At(0.25)), 3); // moving again, behind the picture: waited for, never run back to
        Assert.True(clock.IsFollowingSound);
        Assert.Equal(0.45, clock.Now(1.2, At(0.45)), 3);
    }
}
