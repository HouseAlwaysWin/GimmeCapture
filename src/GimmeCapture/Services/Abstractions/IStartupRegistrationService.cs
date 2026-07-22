namespace GimmeCapture.Services.Abstractions;

public interface IStartupRegistrationService
{
    void SetStartup(bool runOnStartup);

    bool IsRegistered();

    /// <summary>
    /// True when the OS itself has switched our startup entry off, so it will NOT launch at login even though
    /// <see cref="IsRegistered"/> reports true (Windows: Task Manager → "Startup apps" → Disable). Lets the UI
    /// tell the user why auto-start silently stopped working instead of showing a switch the OS overrides.
    /// Returns false where the platform has no such concept.
    /// </summary>
    bool IsDisabledByOs();
}
