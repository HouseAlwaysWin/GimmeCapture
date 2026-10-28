using System.Threading;
using GimmeCapture.Services.Core.Media;
using GimmeCapture.Services.Platforms.Windows;
using Xunit;

namespace GimmeCapture.Tests;

// Windows head only (see the csproj): the high-resolution timer is left out of the app's net10.0 head, so a net10.0
// build gets the sleep fallback even when it runs on Windows.
public class FrameDeadlineWaiterWindowsTests
{
    [WindowsFact]
    public void OnWindows_ItIsAHighResolutionTimer()
    {
        using var waiter = FrameDeadlineWaiter.Create(CancellationToken.None);

        Assert.IsType<HighResolutionTimerWaiter>(waiter);
    }
}
