using System.Threading;
using GimmeCapture.Services.Core.Infrastructure;

namespace GimmeCapture.Tests;

/// <summary>
/// <see cref="TimedUiRoute"/> is how the snip overlay's keyboard hook asks the UI thread about a key while Windows
/// holds it. A dedicated thread stands in for the UI thread, so each case controls exactly when the question runs.
/// </summary>
public class TimedUiRouteTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan Grace = TimeSpan.FromSeconds(2);

    [Fact]
    public void AnAnswerInTime_IsReturned()
    {
        bool handled = TimedUiRoute.Ask(RunOnNewThread, () => true, Timeout, Grace, out bool timedOut);

        Assert.True(handled);
        Assert.False(timedOut);
    }

    [Fact]
    public void AQuestionTheUiThreadNeverStarted_IsDropped_AndNeverRunsLater()
    {
        Func<bool>? queued = null;
        int routed = 0;

        bool handled = TimedUiRoute.Ask(
            call =>
            {
                queued = call; // the "UI thread" is busy: the call only runs once the test runs it
                return new TaskCompletionSource<bool>().Task;
            },
            () =>
            {
                Interlocked.Increment(ref routed);
                return true;
            },
            Timeout, Grace, out bool timedOut);

        Assert.False(handled);
        Assert.True(timedOut);

        // The UI thread finally gets to it: the key already went to the focused app, so it must not act on it.
        Assert.False(queued!());
        Assert.Equal(0, routed);
    }

    [Fact]
    public void AQuestionAlreadyRunningAtTheTimeout_GetsTheGraceToAnswer()
    {
        bool handled = TimedUiRoute.Ask(
            RunOnNewThread,
            () =>
            {
                Thread.Sleep(Timeout * 2); // started at once, finishes after the timeout but within the grace
                return true;
            },
            Timeout, Grace, out bool timedOut);

        Assert.True(handled);
        Assert.False(timedOut);
    }

    [Fact]
    public void AQuestionThatOutlastsTheGrace_CountsAsUnanswered()
    {
        // Not disposed: the stand-in UI thread may still be waking from it when the test ends.
        var release = new ManualResetEventSlim();
        bool handled = TimedUiRoute.Ask(
            RunOnNewThread,
            () =>
            {
                release.Wait();
                return true;
            },
            Timeout, TimeSpan.FromMilliseconds(100), out bool timedOut);
        release.Set();

        Assert.False(handled);
        Assert.True(timedOut);
    }

    [Fact]
    public void AnExceptionFromTheQuestion_ReachesTheCaller()
    {
        var ex = Assert.Throws<AggregateException>(() =>
            TimedUiRoute.Ask(RunOnNewThread, () => throw new InvalidOperationException("routing failed"), Timeout, Grace, out _));

        Assert.IsType<InvalidOperationException>(ex.InnerException);
    }

    private static Task<bool> RunOnNewThread(Func<bool> call)
    {
        var answer = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        new Thread(() =>
        {
            try
            {
                answer.SetResult(call());
            }
            catch (Exception ex)
            {
                answer.SetException(ex);
            }
        })
        { IsBackground = true }.Start();
        return answer.Task;
    }
}
