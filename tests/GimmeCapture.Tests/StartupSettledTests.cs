using GimmeCapture.Services.Core.Infrastructure;

namespace GimmeCapture.Tests;

/// <summary>
/// The app goes back to normal priority, and reads the fonts' file ahead, only once both the start's timings are
/// complete and the UI thread's start-up work after the hotkeys (the tray icon) is done (<see cref="StartupSettled"/>):
/// at the hotkeys alone, that last work took 2.7 s at a busy sign-in.
/// </summary>
public class StartupSettledTests
{
    [Fact]
    public void TheAction_RunsOnce_WhenTheLastConditionIsReleased_InEitherOrder()
    {
        int settled = 0;
        var latch = new SettleLatch(2, () => settled++);

        latch.Release();
        Assert.Equal(0, settled);

        latch.Release();
        Assert.Equal(1, settled);

        latch.Release();
        Assert.Equal(1, settled);
    }
}
