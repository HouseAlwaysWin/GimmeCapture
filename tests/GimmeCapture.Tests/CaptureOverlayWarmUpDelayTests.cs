using GimmeCapture.Composition;

namespace GimmeCapture.Tests;

/// <summary>
/// The capture overlay's idle warm-up waits ten seconds at a start to the tray, the sign-in's, and three at a window
/// launch, after which the user's first capture came 5 and 8 s in and found the overlay's code not loaded yet
/// (<see cref="AppBootstrapper.CaptureOverlayWarmUpDelay"/>).
/// </summary>
public class CaptureOverlayWarmUpDelayTests
{
    [Theory]
    [InlineData(true, 10)]
    [InlineData(false, 3)]
    public void TheWarmUp_WaitsLongerAtSignIn(bool trayLaunch, int seconds)
    {
        Assert.Equal(TimeSpan.FromSeconds(seconds), AppBootstrapper.CaptureOverlayWarmUpDelay(trayLaunch));
    }
}
