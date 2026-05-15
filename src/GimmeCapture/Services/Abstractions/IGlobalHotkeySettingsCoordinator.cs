namespace GimmeCapture.Services.Abstractions;

public interface IGlobalHotkeySettingsCoordinator
{
    void RegisterGlobalHotkey(int id, string hotkey);
}
