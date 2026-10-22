using GimmeCapture.Models;
using GimmeCapture.Services.Core.Infrastructure;
using GimmeCapture.ViewModels.Main;

namespace GimmeCapture.Tests.ViewModels;

public class NullSnipWindowHostTests
{
    [Fact]
    public async Task ACaptureAction_RunsAtOnce_AndCountsAsLaunched()
    {
        // An overlay opened on its own has no countdown to wait for and no window to show again.
        bool ran = false;

        var result = await NullSnipWindowHost.Instance.RunCaptureActionAsync(CaptureMode.Normal, () =>
        {
            ran = true;
            return Task.CompletedTask;
        });

        Assert.True(ran);
        Assert.Equal(CaptureLaunchResult.Launched, result);
    }

    [Fact]
    public void ThereIsNothingToConfirmWith_OrToOpenModulesIn()
    {
        Assert.Null(NullSnipWindowHost.Instance.ConfirmAction);
        Assert.Null(NullSnipWindowHost.Instance.RequestOpenModulesAction);
    }
}
