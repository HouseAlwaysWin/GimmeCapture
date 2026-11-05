using System;
using System.Threading;
using GimmeCapture.Services.Core.Media;

namespace GimmeCapture.Tests;

/// <summary>
/// The answer of a slow probe run once a process, as the hardware AV1 check is: the Record tab, the Record-mode warm-up
/// and a recording's start could each open the GPU's AV1 encoders at the same time.
/// </summary>
public class ProbedAnswerTests
{
    [Fact]
    public void ACallerComingWhileTheProbeRuns_WaitsForItsAnswer_InsteadOfProbingToo()
    {
        var answer = new ProbedAnswer();
        using var probing = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        int probes = 0;
        Func<bool?> slowProbe = () =>
        {
            Interlocked.Increment(ref probes);
            probing.Set();
            release.Wait(TimeSpan.FromSeconds(10));
            return true;
        };

        bool first = false, second = false;
        var a = new Thread(() => first = answer.Get(slowProbe));
        a.Start();
        Assert.True(probing.Wait(TimeSpan.FromSeconds(10)));
        var b = new Thread(() => second = answer.Get(slowProbe));
        b.Start();
        // Time for the second caller to get as far as it can while the first still probes: before, a probe of its own.
        Thread.Sleep(200);

        release.Set();
        Assert.True(a.Join(TimeSpan.FromSeconds(10)) && b.Join(TimeSpan.FromSeconds(10)));
        Assert.Equal(1, probes);
        Assert.True(first);
        Assert.True(second);
    }

    [Fact]
    public void AProbeThatCouldNotAnswer_IsNotKept()
    {
        // FFmpeg not loaded yet is no "no": a later caller probes again.
        var answer = new ProbedAnswer();
        int probes = 0;

        Assert.False(answer.Get(() => { probes++; return null; }));
        Assert.Null(answer.Known);
        Assert.False(answer.Get(() => { probes++; return false; }));
        Assert.False(answer.Get(() => { probes++; return true; })); // the "no" is kept: not probed again

        Assert.Equal(2, probes);
        Assert.False(answer.Known);
    }

    [Fact]
    public void AnEarlierRunsAnswer_StandsUntilThisRunHasOne()
    {
        var answer = new ProbedAnswer();
        int found = 0;

        Assert.True(answer.Recall(true));
        Assert.True(answer.Get(() => throw new InvalidOperationException("probed"), _ => found++));
        Assert.False(answer.Recall(false));
        Assert.True(answer.Known);
        Assert.Equal(0, found);
    }
}
