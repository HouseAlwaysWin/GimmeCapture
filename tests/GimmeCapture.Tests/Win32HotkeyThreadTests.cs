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

        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(1), $"Dispose took {elapsed.ElapsedMilliseconds} ms");
        Assert.False(thread.TryInvoke(() => { }, TimeSpan.FromMilliseconds(100)));
    }
}
