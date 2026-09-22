using System;
using GimmeCapture.Services.Core.Infrastructure;

namespace GimmeCapture.Tests;

/// <summary>
/// Reclaiming memory is an idle-time job. Short delays are how it stopped being one: a trim five seconds after
/// the capture overlay closed ran between two captures, where EmptyWorkingSet evicts the whole process (measured:
/// 593 MB of working set to 5.8 MB, private bytes unchanged) and the next capture faults all of it back in.
/// </summary>
public class MemoryTrimPolicyTests
{
    [Fact]
    public void NoDelayAsksForTheIdleWindow()
    {
        Assert.Equal(ProcessMemoryTrimService.IdleTrimDelay, ProcessMemoryTrimService.ClampToIdleWindow(null));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    [InlineData(30)]
    public void ShorterRequestsAreRaisedToTheIdleWindow(int seconds)
    {
        Assert.Equal(
            ProcessMemoryTrimService.IdleTrimDelay,
            ProcessMemoryTrimService.ClampToIdleWindow(TimeSpan.FromSeconds(seconds)));
    }

    [Fact]
    public void ACallerMayStillChooseToWaitLonger()
    {
        var anHour = TimeSpan.FromHours(1);

        Assert.Equal(anHour, ProcessMemoryTrimService.ClampToIdleWindow(anHour));
    }

    [Fact]
    public void TheIdleWindowIsMinutesNotSeconds()
    {
        // The actual regression this guards: 5 s / 30 s windows sit inside a normal capture rhythm.
        Assert.True(ProcessMemoryTrimService.IdleTrimDelay >= TimeSpan.FromMinutes(1));
    }

    [Fact]
    public void ACaptureRequestIsReportedOnceAndOnlyOnce()
    {
        // The stamp has to be consumed on read: an overlay that opens without one (tray menu, toolbar button)
        // must report "n/a" rather than the age of some earlier hotkey press.
        CaptureOpenTrace.Requested();

        Assert.NotNull(CaptureOpenTrace.TakeWaitedForUiThreadMs());
        Assert.Null(CaptureOpenTrace.TakeWaitedForUiThreadMs());
    }
}
