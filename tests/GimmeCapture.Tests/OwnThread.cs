using System;
using System.Threading;
using System.Threading.Tasks;

namespace GimmeCapture.Tests;

/// <summary>
/// <see cref="Task.Run(Action)"/> and <see cref="Task.Delay(TimeSpan)"/> on threads of their own, for tests that time a
/// call which blocks on purpose. The rest of the suite keeps the thread pool busy, and both of those wait for it: a
/// queued call could start, and a delay end, after the test's timeout had already run out. ResourceUseGateTests
/// failed under load that way with nothing wrong with the gate.
/// </summary>
internal static class OwnThread
{
    public static Task Run(Action work) =>
        Task.Factory.StartNew(work, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    public static Task<T> Run<T>(Func<T> work) =>
        Task.Factory.StartNew(work, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    public static Task Delay(TimeSpan delay) => Run(() => Thread.Sleep(delay));
}
