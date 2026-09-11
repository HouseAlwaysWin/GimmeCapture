using System;
using System.IO;
using GimmeCapture.Services.Core.Infrastructure;

namespace GimmeCapture.Tests;

/// <summary>
/// The scheduled-task backend for run-on-startup.
///
/// Release builds ship a requireAdministrator manifest, and Windows silently skips elevated entries in
/// HKCU\...\Run at logon — no process, no error, nothing in any log — so auto-start never worked in a shipped
/// build while every self-check reported it as registered. A logon-triggered task with HighestAvailable is the
/// replacement; these cover the parts that decide whether it launches the right thing and whether the app can
/// still tell its own registration apart from another install's.
/// </summary>
public sealed class StartupTaskRegistrationTests : IDisposable
{
    private const string ExePath = @"C:\Program Files\Gimme & Co\GimmeCapture.exe";

    private readonly string _dir;
    private readonly string _realExe;

    public StartupTaskRegistrationTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "GimmeCapture.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _realExe = Path.Combine(_dir, "Installed.exe");
        File.WriteAllText(_realExe, "exe");
    }

    private static string Xml(string exePath) =>
        WindowsStartupTaskService.BuildTaskXml(exePath, @"DESKTOP\user");

    [Fact]
    public void TaskRunsTheAppInTrayModeAtLogon()
    {
        string xml = Xml(ExePath);

        Assert.Contains("<LogonTrigger>", xml, StringComparison.Ordinal);
        Assert.Contains($"<Arguments>{StartupService.RunArgumentForTrayStartup}</Arguments>", xml, StringComparison.Ordinal);
        Assert.Contains(@"<WorkingDirectory>C:\Program Files\Gimme &amp; Co</WorkingDirectory>", xml, StringComparison.Ordinal);
    }

    [Fact]
    public void TaskAsksForElevationWithoutAUacPrompt()
    {
        // The whole point of the task: HighestAvailable is what starts an elevated app at sign-in without a
        // consent prompt, and InteractiveToken is what lets it show a tray icon in the user's session. Lose
        // either and this is just a slower way of not launching.
        string xml = Xml(ExePath);

        Assert.Contains("<RunLevel>HighestAvailable</RunLevel>", xml, StringComparison.Ordinal);
        Assert.Contains("<LogonType>InteractiveToken</LogonType>", xml, StringComparison.Ordinal);
    }

    [Fact]
    public void TaskIsNotTreatedAsADeferrableMaintenanceJob()
    {
        // Scheduler defaults would skip the launch on battery and kill the app after 3 days of uptime.
        string xml = Xml(ExePath);

        Assert.Contains("<DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>", xml, StringComparison.Ordinal);
        Assert.Contains("<StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>", xml, StringComparison.Ordinal);
        Assert.Contains("<ExecutionTimeLimit>PT0S</ExecutionTimeLimit>", xml, StringComparison.Ordinal);
    }

    [Fact]
    public void PathsWithXmlSpecialCharactersSurviveTheDefinition()
    {
        // A raw & in an install path would make schtasks reject the definition, which would silently drop the
        // user back to the Run key that does not work.
        string xml = Xml(ExePath);

        Assert.Contains(@"<Command>C:\Program Files\Gimme &amp; Co\GimmeCapture.exe</Command>", xml, StringComparison.Ordinal);
        Assert.DoesNotContain("Gimme & Co\\GimmeCapture.exe</Command>", xml, StringComparison.Ordinal);
    }

    [Fact]
    public void TheRegisteredCommandReadsBackInRunValueShape()
    {
        // ShouldClaimRegistration is shared with the Run key, so what comes out of a task definition has to look
        // like a Run value or the precedence rules silently stop applying to tasks.
        string readBack = WindowsStartupTaskService.ExtractCommandLine(Xml(ExePath))!;

        Assert.Equal($"\"{ExePath}\" {StartupService.RunArgumentForTrayStartup}", readBack);
    }

    [Fact]
    public void ATaskThisCopyAlreadyOwnsIsNotRewritten()
    {
        // Re-asserting on every launch must be a no-op once the task is correct; otherwise each start rewrites
        // the definition and resets whatever the user changed in Task Scheduler.
        string expected = $"\"{_realExe}\" {StartupService.RunArgumentForTrayStartup}";
        string? registered = WindowsStartupTaskService.ExtractCommandLine(Xml(_realExe));

        Assert.False(StartupService.ShouldClaimRegistration(registered, expected));
    }

    [Fact]
    public void ATaskPointingAtAnotherLiveInstallIsLeftAlone()
    {
        string otherExe = Path.Combine(_dir, "OtherInstall.exe");
        File.WriteAllText(otherExe, "exe");
        string? registered = WindowsStartupTaskService.ExtractCommandLine(Xml(otherExe));

        Assert.False(StartupService.ShouldClaimRegistration(
            registered,
            $"\"{Path.Combine(_dir, "bin", "Debug", "GimmeCapture.exe")}\" {StartupService.RunArgumentForTrayStartup}"));
    }

    [Fact]
    public void ATaskPointingAtAVanishedInstallIsClaimed()
    {
        string? registered = WindowsStartupTaskService.ExtractCommandLine(Xml(Path.Combine(_dir, "MovedAway.exe")));

        Assert.True(StartupService.ShouldClaimRegistration(
            registered,
            $"\"{_realExe}\" {StartupService.RunArgumentForTrayStartup}"));
    }

    [Fact]
    public void ADefinitionWithoutAnExecActionReadsBackAsNothing()
    {
        Assert.Null(WindowsStartupTaskService.ExtractCommandLine(null));
        Assert.Null(WindowsStartupTaskService.ExtractCommandLine(""));
        Assert.Null(WindowsStartupTaskService.ExtractCommandLine("<Task><Actions /></Task>"));
    }

    [Fact]
    public void RetiringTheRunValueSparesAnotherInstallsEntry()
    {
        // Migrating to the task deletes the Run value it replaces. Deleting a DIFFERENT install's value would
        // break that copy's auto-start, which is never this copy's business.
        string otherExe = Path.Combine(_dir, "OtherInstall.exe");
        File.WriteAllText(otherExe, "exe");

        Assert.False(StartupService.OwnsRegistration($"\"{otherExe}\" --startup", _realExe));
    }

    [Fact]
    public void RetiringTheRunValueTakesOurOwnAndAbandonedOnes()
    {
        Assert.True(StartupService.OwnsRegistration($"\"{_realExe}\" --startup", _realExe));
        Assert.True(StartupService.OwnsRegistration($"\"{_realExe.ToUpperInvariant()}\" --startup", _realExe));
        Assert.True(StartupService.OwnsRegistration($"\"{Path.Combine(_dir, "Gone.exe")}\" --startup", _realExe));
        Assert.True(StartupService.OwnsRegistration("", _realExe));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort temp cleanup */ }
    }
}
