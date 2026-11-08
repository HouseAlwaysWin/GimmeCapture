using System;
using GimmeCapture.Services.Core.Infrastructure;

namespace GimmeCapture.Tests;

// A scrolling capture's closing log line gives how long each frame took to grab and to stitch, from these.
public class DurationTallyTests
{
    [Fact]
    public void ATally_GivesTheCountTotalAverageAndLongest()
    {
        var tally = new DurationTally();

        tally.Add(TimeSpan.FromMilliseconds(10));
        tally.Add(TimeSpan.FromMilliseconds(30));
        tally.Add(TimeSpan.FromMilliseconds(20));

        Assert.Equal(3, tally.Count);
        Assert.Equal(TimeSpan.FromMilliseconds(60), tally.Total);
        Assert.Equal(TimeSpan.FromMilliseconds(20), tally.Average);
        Assert.Equal(TimeSpan.FromMilliseconds(30), tally.Longest);
        Assert.Equal("20.0/30ms", tally.ToString());
    }

    [Fact]
    public void AnEmptyTally_SaysNone()
    {
        var tally = new DurationTally();

        Assert.Equal(0, tally.Count);
        Assert.Equal(TimeSpan.Zero, tally.Average);
        Assert.Equal("none", tally.ToString());
    }
}
