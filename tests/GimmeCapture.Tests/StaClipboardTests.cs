using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using GimmeCapture.Services.Core.Infrastructure;

namespace GimmeCapture.Tests;

// Guards the mechanism that keeps a synchronous WinForms/OLE clipboard write from freezing the Avalonia UI
// thread: the write runs on a dedicated background STA thread bounded by a timeout, so a wedged write resolves
// to a failed result instead of blocking the caller.
public sealed class StaClipboardTests
{
    [Fact]
    public async Task RunAsync_RunsWorkOffCallingThread_OnStaApartment_AndReturnsTrue()
    {
        int callingThreadId = Environment.CurrentManagedThreadId;
        int workThreadId = -1;
        ApartmentState apartment = ApartmentState.Unknown;

        var result = await StaClipboard.RunAsync(
            () =>
            {
                workThreadId = Environment.CurrentManagedThreadId;
                apartment = Thread.CurrentThread.GetApartmentState();
            },
            TimeSpan.FromSeconds(5),
            "Test.RunsOffThread");

        Assert.True(result);
        Assert.NotEqual(callingThreadId, workThreadId);

        // The STA apartment is only requested/valid on Windows; the test suite runs on Windows.
        if (OperatingSystem.IsWindows())
        {
            Assert.Equal(ApartmentState.STA, apartment);
        }
    }

    [Fact]
    public async Task RunAsync_WhenWorkWedgesPastTimeout_ReturnsFalseWithoutBlockingCaller()
    {
        // A write that never completes stands in for a clipboard call wedged behind the Windows
        // clipboard-history listeners — the exact situation that froze the UI thread.
        using var release = new ManualResetEventSlim(false);
        var timeout = TimeSpan.FromMilliseconds(200);

        var stopwatch = Stopwatch.StartNew();
        var result = await StaClipboard.RunAsync(
            () => release.Wait(),
            timeout,
            "Test.Wedged");
        stopwatch.Stop();

        // The caller was released by the timeout, not by the (still-blocked) work — so it can never hang the UI.
        Assert.False(result);
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(4),
            $"RunAsync should return shortly after the {timeout.TotalMilliseconds:0}ms timeout, but took {stopwatch.ElapsedMilliseconds}ms.");

        // Let the abandoned background STA thread unwind cleanly.
        release.Set();
    }

    [Fact]
    public async Task RunAsync_WhenWorkThrows_ReturnsFalse()
    {
        var result = await StaClipboard.RunAsync(
            () => throw new InvalidOperationException("clipboard boom"),
            TimeSpan.FromSeconds(5),
            "Test.Throws");

        Assert.False(result);
    }
}
