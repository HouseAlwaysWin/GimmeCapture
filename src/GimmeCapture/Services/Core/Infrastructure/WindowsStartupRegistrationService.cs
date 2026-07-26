using GimmeCapture.Services.Abstractions;

namespace GimmeCapture.Services.Core.Infrastructure;

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class WindowsStartupRegistrationService : IStartupRegistrationService
{
    public void SetStartup(bool runOnStartup)
    {
        StartupService.SetStartup(runOnStartup);
    }

    public bool IsRegistered()
    {
        return StartupService.IsRegistered();
    }

    public bool IsDisabledByOs()
    {
        return StartupService.IsDisabledByWindows();
    }
}
