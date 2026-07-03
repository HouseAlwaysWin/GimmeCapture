using GimmeCapture.Services.Abstractions;
using GimmeCapture.Services.Core.Infrastructure;

namespace GimmeCapture.Services.Platforms.Linux;

/// <summary>
/// Placeholder <see cref="IStartupRegistrationService"/> for non-Windows (Linux) runs. The Windows
/// impl writes an HKCU Run key; the Linux equivalent is an XDG autostart .desktop file under
/// ~/.config/autostart — future work (docs/LINUX_PORT_FEASIBILITY.md). No-ops for now.
/// </summary>
public sealed class LinuxStartupRegistrationService : IStartupRegistrationService
{
    public void SetStartup(bool runOnStartup)
    {
        AppLog.Information("LinuxStartupRegistration.SetStartup.NoOp");
    }

    public bool IsRegistered() => false;
}
