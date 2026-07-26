using GimmeCapture.Services.Core.Infrastructure;

namespace GimmeCapture.Tests;

// Windows records a user's Task Manager -> "Startup apps" choice in StartupApproved, and when an entry is
// disabled there it IGNORES the Run value entirely. Misreading this blob is the difference between telling the
// user why auto-start silently stopped and showing them an "on" switch that Windows overrides, so pin the
// interpretation down: the low bit of the first byte is the disabled flag.
public class StartupServiceTests
{
    [Theory]
    [InlineData(0x02, false)] // enabled (the value Windows writes when an entry is switched back on)
    [InlineData(0x06, false)] // enabled, alternate form seen in the wild
    [InlineData(0x03, true)]  // disabled  <- the state that silently breaks auto-start
    [InlineData(0x07, true)]  // disabled, alternate form
    public void IsDisabledStateBlob_ReadsTheLowBitOfTheFirstByte(byte stateByte, bool expectedDisabled)
    {
        // Real blobs carry a FILETIME after the state byte; it must not affect the verdict.
        var blob = new byte[] { stateByte, 0, 0, 0, 0xEC, 0x76, 0x7D, 0xD2, 0xBB, 0x0A, 0xDD, 0x01 };

        Assert.Equal(expectedDisabled, StartupService.IsDisabledStateBlob(blob));
    }

    [Fact]
    public void IsDisabledStateBlob_TreatsMissingOrEmptyAsEnabled()
    {
        // No value at all = the user never touched the entry, which Windows treats as enabled. Reporting these
        // as "disabled" would nag every user who has auto-start working perfectly well.
        Assert.False(StartupService.IsDisabledStateBlob(null));
        Assert.False(StartupService.IsDisabledStateBlob([]));
    }
}
