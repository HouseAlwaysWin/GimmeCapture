using GimmeCapture.Services.Core.Infrastructure;

namespace GimmeCapture.Tests;

// "Run as administrator": the exe stays asInvoker, and start-up asks UAC for an elevated copy when the setting is on.
public sealed class ElevatedLaunchTests
{
    [Theory]
    [InlineData(true, AdministratorRights.CanElevate, true)]
    [InlineData(false, AdministratorRights.CanElevate, false)]
    [InlineData(true, AdministratorRights.Elevated, false)]
    // A standard user: UAC would ask for another account, whose settings and history are not this user's.
    [InlineData(true, AdministratorRights.Unavailable, false)]
    public void RelaunchesAtStart_OnlyWhenOnAndOnePromptElevatesThisUser(bool runAsAdministrator, AdministratorRights rights, bool expected)
    {
        Assert.Equal(expected, ElevatedLaunch.ShouldRelaunchAtStart(runAsAdministrator, rights, ["--startup"]));
    }

    [Fact]
    public void TheCopyUacWasAskedFor_NeverAsksAgain()
    {
        // Still not elevated after the prompt (UAC switched off, say): asking again would never end.
        Assert.False(ElevatedLaunch.ShouldRelaunchAtStart(true, AdministratorRights.CanElevate, [ElevatedLaunch.RelaunchedArgument]));
        Assert.False(ElevatedLaunch.ShouldRelaunchAtStart(true, AdministratorRights.CanElevate, ["--startup", "--ELEVATED-RELAUNCH"]));
    }

    [WindowsFact]
    public void TheTokenQuery_AnswersWithAnElevationType()
    {
        // 1 Default, 2 Full, 3 Limited. 0 is a failed query, which would make every account look like a standard user
        // and hide the setting.
        Assert.InRange(ElevatedLaunch.ReadElevationType(), 1, 3);
    }

    [Fact]
    public void ASignInLaunch_StaysInTheTray_AsTheElevatedCopy()
    {
        Assert.Equal("--startup --elevated-relaunch", ElevatedLaunch.BuildArguments(["--startup"]));
        Assert.Equal("--elevated-relaunch", ElevatedLaunch.BuildArguments([]));
        Assert.Equal("--elevated-relaunch", ElevatedLaunch.BuildArguments([ElevatedLaunch.RelaunchedArgument, "anything else"]));
    }
}
