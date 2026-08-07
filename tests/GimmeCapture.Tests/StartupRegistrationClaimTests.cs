using System;
using System.IO;
using GimmeCapture.Services.Core.Infrastructure;

namespace GimmeCapture.Tests;

/// <summary>
/// Which copy of the app owns the Run entry. This matters because more than one copy exists in practice — a
/// deployed build plus whatever is in bin\Debug — and the re-assert on every launch used to hand the entry to
/// whichever ran last. A dev build winning that race points Windows at a build-output path, which the next clean
/// deletes, and auto-start then fails at boot with nothing to show for it.
/// </summary>
public sealed class StartupRegistrationClaimTests : IDisposable
{
    private readonly string _dir;
    private readonly string _realExe;

    public StartupRegistrationClaimTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "GimmeCapture.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _realExe = Path.Combine(_dir, "Installed.exe");
        File.WriteAllText(_realExe, "exe");
    }

    private string Value(string exe) => $"\"{exe}\" --startup";

    [Fact]
    public void ClaimsWhenNothingIsRegistered()
    {
        Assert.True(StartupService.ShouldClaimRegistration(null, Value(_realExe)));
        Assert.True(StartupService.ShouldClaimRegistration("", Value(_realExe)));
        Assert.True(StartupService.ShouldClaimRegistration("   ", Value(_realExe)));
    }

    [Fact]
    public void DoesNotRewriteAnIdenticalEntry()
    {
        string value = Value(_realExe);

        Assert.False(StartupService.ShouldClaimRegistration(value, value));
    }

    [Fact]
    public void ClaimsWhenTheRegisteredExecutableIsGone()
    {
        // The reinstall / update case the launch-time re-assert exists for: the old path no longer resolves, so
        // taking it over is the repair.
        string stale = Value(Path.Combine(_dir, "MovedAway.exe"));

        Assert.True(StartupService.ShouldClaimRegistration(stale, Value(_realExe)));
    }

    [Fact]
    public void LeavesAnotherInstallAloneWhenItStillExists()
    {
        // The bug this guards: a dev build launching would otherwise repoint Windows at bin\Debug, which the next
        // clean build deletes.
        string otherExe = Path.Combine(_dir, "OtherInstall.exe");
        File.WriteAllText(otherExe, "exe");
        string devBuild = Value(Path.Combine(_dir, "bin", "Debug", "GimmeCapture.exe"));

        Assert.False(StartupService.ShouldClaimRegistration(Value(otherExe), devBuild));
    }

    [Fact]
    public void MatchesCaseInsensitively()
    {
        // Windows paths are case-insensitive; a case difference must not count as "a different install".
        Assert.False(StartupService.ShouldClaimRegistration(Value(_realExe.ToUpperInvariant()), Value(_realExe)));
    }

    [Theory]
    [InlineData("\"C:\\Apps\\Gimme.exe\" --startup", "C:\\Apps\\Gimme.exe")]
    [InlineData("\"C:\\Program Files\\A B\\Gimme.exe\" --startup", "C:\\Program Files\\A B\\Gimme.exe")]
    [InlineData("C:\\Apps\\Gimme.exe --startup", "C:\\Apps\\Gimme.exe")]
    [InlineData("C:\\Apps\\Gimme.exe", "C:\\Apps\\Gimme.exe")]
    [InlineData("", null)]
    [InlineData("\"unterminated --startup", null)]
    public void ExtractsTheExecutableFromBothQuotedAndBareValues(string runValue, string? expected)
    {
        Assert.Equal(expected, StartupService.ExtractExecutablePath(runValue));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort temp cleanup */ }
    }
}
