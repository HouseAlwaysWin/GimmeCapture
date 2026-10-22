using System;
using System.Diagnostics;
using GimmeCapture.Services.Platforms.Windows;
using Xunit;

namespace GimmeCapture.Tests;

public class Win32HotkeyThreadTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    [WindowsFact]
    public void SetupAndWork_RunOnTheHotkeyThread()
    {
        using var thread = new Win32HotkeyThread("test");
        int setupThread = 0;

        Assert.True(thread.Start(() => setupThread = Environment.CurrentManagedThreadId, Timeout));
        Assert.True(thread.TryInvoke(() => Environment.CurrentManagedThreadId, Timeout, out int workThread));

        Assert.Equal(setupThread, workThread);
        Assert.NotEqual(Environment.CurrentManagedThreadId, workThread);
    }

    [WindowsFact]
    public void AnExceptionInTheWork_ReachesTheCaller_AndTheLoopCarriesOn()
    {
        using var thread = new Win32HotkeyThread("test");
        thread.Start(() => { }, Timeout);

        Assert.Throws<InvalidOperationException>(() => thread.TryInvoke(() => throw new InvalidOperationException("boom"), Timeout));
        Assert.True(thread.TryInvoke(() => { }, Timeout));
    }

    [WindowsFact]
    public void Dispose_EndsTheLoopPromptly_AndRefusesLaterWork()
    {
        var thread = new Win32HotkeyThread("test");
        thread.Start(() => { }, Timeout);

        var elapsed = Stopwatch.StartNew();
        thread.Dispose();

        // Dispose gives the loop 2 s to end, then gives up on it, so a loop that never ends shows as a Dispose of 2 s or
        // more. A loop that does end can still take over a second in a loaded run (coverage on, other classes encoding
        // video), so the bound is the deadline itself rather than a guess below it.
        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(2), $"Dispose took {elapsed.ElapsedMilliseconds} ms");
        Assert.False(thread.TryInvoke(() => { }, TimeSpan.FromMilliseconds(100)));
    }
}
