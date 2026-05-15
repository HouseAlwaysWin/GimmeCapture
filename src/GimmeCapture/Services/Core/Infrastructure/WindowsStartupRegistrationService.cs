using GimmeCapture.Services.Abstractions;

namespace GimmeCapture.Services.Core.Infrastructure;

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
}
