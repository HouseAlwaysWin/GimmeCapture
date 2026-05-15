namespace GimmeCapture.Services.Abstractions;

public interface IStartupRegistrationService
{
    void SetStartup(bool runOnStartup);

    bool IsRegistered();
}
