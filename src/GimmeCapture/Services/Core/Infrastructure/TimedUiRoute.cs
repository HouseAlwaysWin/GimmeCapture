using System;
using System.Threading;
using System.Threading.Tasks;

namespace GimmeCapture.Services.Core.Infrastructure;

/// <summary>
/// Asks the UI thread a yes/no question from a thread that cannot wait long for the answer: the snip overlay's keyboard
/// hook asking whether the overlay acted on a key, while Windows holds that key and every key after it.
/// </summary>
internal static class TimedUiRoute
{
    private const int Pending = 0;
    private const int Running = 1;
    private const int Abandoned = 2;

    /// <param name="invokeOnUi">Queues a call on the UI thread and returns its result as a task.</param>
    /// <param name="route">The question, run on the UI thread.</param>
    /// <param name="timeout">How long to wait for the UI thread to answer.</param>
    /// <param name="grace">How much longer to wait when the UI thread has started on the question by then.</param>
    /// <param name="timedOut">True when there was no answer: the result is then false, and a question the UI thread
    /// had not started on is never run at all.</param>
    /// <returns>The answer, or false without one. An exception the question throws is rethrown, wrapped in an
    /// <see cref="AggregateException"/>.</returns>
    public static bool Ask(
        Func<Func<bool>, Task<bool>> invokeOnUi, Func<bool> route, TimeSpan timeout, TimeSpan grace, out bool timedOut)
    {
        int state = Pending;
        Task<bool> answer = invokeOnUi(() =>
            Interlocked.CompareExchange(ref state, Running, Pending) == Pending && route());

        timedOut = false;
        if (answer.Wait(timeout))
        {
            return answer.Result;
        }

        if (Interlocked.CompareExchange(ref state, Abandoned, Pending) == Pending)
        {
            timedOut = true;
            return false;
        }

        // The UI thread has just started on it, so its answer is worth a short wait.
        if (answer.Wait(grace))
        {
            return answer.Result;
        }

        timedOut = true;
        return false;
    }
}
