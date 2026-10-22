using GimmeCapture.Services.Core.Infrastructure;
using GimmeCapture.Services.Platforms.Linux;

namespace GimmeCapture.Tests;

/// <summary>The command the XDG autostart entry written by <see cref="LinuxStartupRegistrationService"/> runs.</summary>
public class LinuxStartupRegistrationServiceTests
{
    private const string TrayArgument = StartupService.RunArgumentForTrayStartup;

    [Fact]
    public void ExecCommand_ForAnApphost_RunsTheApphostAlone()
    {
        // The entry dll sits beside the apphost, which loads it by itself.
        string exec = LinuxStartupRegistrationService.BuildExecCommand(
            "/opt/GimmeCapture/GimmeCapture", "/opt/GimmeCapture/GimmeCapture.dll");

        Assert.Equal($"\"/opt/GimmeCapture/GimmeCapture\" {TrayArgument}", exec);
    }

    [Fact]
    public void ExecCommand_ForTheDotnetHost_PassesTheEntryDll()
    {
        string exec = LinuxStartupRegistrationService.BuildExecCommand(
            "/usr/lib/dotnet/dotnet", "/home/me/My Apps/GimmeCapture.dll");

        Assert.Equal($"\"/usr/lib/dotnet/dotnet\" \"/home/me/My Apps/GimmeCapture.dll\" {TrayArgument}", exec);
    }
}
