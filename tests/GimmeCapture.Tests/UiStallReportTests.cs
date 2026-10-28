using System;
using System.Diagnostics;

namespace GimmeCapture.Tests;

// A UiStall line used to give only the stall's length, which cannot tell a UI thread that was busy from one that was
// blocked or one a garbage collection held. One 2.69 s stall was the last line its process ever wrote.
public sealed class UiStallReportTests
{
    private static long Timestamp(double milliseconds) => (long)(milliseconds * Stopwatch.Frequency / 1000);

    private static UiThreadSample Sample(
        double atMs,
        double? cpuMs,
        double gcPauseMs = 0,
        int gen0 = 10,
        int gen1 = 5,
        int gen2 = 1) =>
        new(
            Timestamp(atMs),
            cpuMs is { } cpu ? TimeSpan.FromMilliseconds(cpu) : null,
            TimeSpan.FromMilliseconds(gcPauseMs),
            gen0,
            gen1,
            gen2);

    [Theory]
    [InlineData(2790.0, 2700.0, 0.0, nameof(UiStallCause.Busy))]
    [InlineData(2790.0, 31.0, 0.0, nameof(UiStallCause.Blocked))]
    [InlineData(2790.0, 10.0, 2500.0, nameof(UiStallCause.GarbageCollection))]
    [InlineData(2790.0, null, 0.0, nameof(UiStallCause.Unknown))]
    [InlineData(2790.0, null, 2500.0, nameof(UiStallCause.GarbageCollection))]
    [InlineData(0.0, 0.0, 0.0, nameof(UiStallCause.Unknown))]
    public void Classify_TellsBusyFromBlockedFromTheGc(double windowMs, double? cpuMs, double gcPauseMs, string expected)
    {
        var cause = UiStallReport.Classify(
            TimeSpan.FromMilliseconds(windowMs),
            cpuMs is { } cpu ? TimeSpan.FromMilliseconds(cpu) : null,
            TimeSpan.FromMilliseconds(gcPauseMs));

        Assert.Equal(Enum.Parse<UiStallCause>(expected), cause);
    }

    [Fact]
    public void DescribeEnded_KeepsTheLinesOldStartAndSaysWhatTheUiThreadDid()
    {
        string line = UiStallReport.DescribeEnded(
            TimeSpan.FromMilliseconds(2690),
            Sample(atMs: 1000, cpuMs: 500),
            Sample(atMs: 3790, cpuMs: 531));

        // Greps of older logs look for this.
        Assert.StartsWith("The UI thread did not pump for 2690ms (", line, StringComparison.Ordinal);
        Assert.Contains("blocked: UI thread CPU 31ms (1%)", line, StringComparison.Ordinal);
        Assert.Contains("GC paused 0ms, collections gen0 +0 gen1 +0 gen2 +0", line, StringComparison.Ordinal);
        Assert.Contains("only got through afterwards.", line, StringComparison.Ordinal);
    }

    [Fact]
    public void DescribeEnded_CountsTheCollectionsDuringTheStall()
    {
        string line = UiStallReport.DescribeEnded(
            TimeSpan.FromMilliseconds(600),
            Sample(atMs: 0, cpuMs: 100, gcPauseMs: 40, gen0: 10, gen1: 5, gen2: 1),
            Sample(atMs: 700, cpuMs: 120, gcPauseMs: 590, gen0: 13, gen1: 6, gen2: 2));

        Assert.Contains("(GC: UI thread CPU 20ms (3%), GC paused 550ms, collections gen0 +3 gen1 +1 gen2 +1)", line, StringComparison.Ordinal);
    }

    [Fact]
    public void Describe_SaysSoWhenTheUiThreadsCpuTimeCannotBeRead()
    {
        string line = UiStallReport.DescribeEnded(
            TimeSpan.FromMilliseconds(400),
            Sample(atMs: 0, cpuMs: null),
            Sample(atMs: 500, cpuMs: null));

        Assert.Contains("(cause unknown: UI thread CPU unknown, GC paused 0ms", line, StringComparison.Ordinal);
    }

    [Fact]
    public void Describe_CapsTheCpuShareAtAHundredPercent()
    {
        // CPU time advances in scheduler ticks, so over a short window it can read more than the wall clock.
        string line = UiStallReport.DescribeEnded(
            TimeSpan.FromMilliseconds(250),
            Sample(atMs: 0, cpuMs: 0),
            Sample(atMs: 350, cpuMs: 359));

        Assert.Contains("busy: UI thread CPU 359ms (100%)", line, StringComparison.Ordinal);
    }

    [Fact]
    public void DescribeOngoing_SaysTheStallIsStillGoing()
    {
        string line = UiStallReport.DescribeOngoing(
            TimeSpan.FromMilliseconds(2016),
            Sample(atMs: 0, cpuMs: 0),
            Sample(atMs: 2116, cpuMs: 2003));

        Assert.StartsWith("The UI thread has not pumped for 2016ms so far (busy: UI thread CPU 2003ms (95%)", line, StringComparison.Ordinal);
    }

    [Fact]
    public void ShouldReportOngoing_OncePerStallAndOnlyPastTheThreshold()
    {
        long firstStall = Timestamp(1000);
        long secondStall = Timestamp(9000);
        var threshold = UiStallReport.OngoingReportThreshold;

        Assert.False(UiStallReport.ShouldReportOngoing(threshold - TimeSpan.FromMilliseconds(1), firstStall, null));
        Assert.True(UiStallReport.ShouldReportOngoing(threshold, firstStall, null));
        // Reported already: the watchdog keeps checking the same stall, and must not write it again.
        Assert.False(UiStallReport.ShouldReportOngoing(threshold + TimeSpan.FromSeconds(5), firstStall, firstStall));
        // A tick came in between: a new stall, reported in its own right.
        Assert.True(UiStallReport.ShouldReportOngoing(threshold, secondStall, firstStall));
    }
}
