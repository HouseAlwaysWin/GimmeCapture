using System;
using Avalonia.Media;
using GimmeCapture.Services.Abstractions;

namespace GimmeCapture.ViewModels.Main;

internal sealed class MainWindowSettingsSideEffectCoordinator
{
    private readonly IGlobalHotkeySettingsCoordinator _globalHotkeySettingsCoordinator;
    private readonly IStartupRegistrationService _startupRegistrationService;
    private readonly IThemeResourceService _themeResourceService;
    private readonly Action _requestSave;
    private readonly Action _markModified;

    public MainWindowSettingsSideEffectCoordinator(
        IGlobalHotkeySettingsCoordinator globalHotkeySettingsCoordinator,
        IStartupRegistrationService startupRegistrationService,
        IThemeResourceService themeResourceService,
        Action requestSave,
        Action markModified)
    {
        _globalHotkeySettingsCoordinator = globalHotkeySettingsCoordinator;
        _startupRegistrationService = startupRegistrationService;
        _themeResourceService = themeResourceService;
        _requestSave = requestSave;
        _markModified = markModified;
    }

    public void RegisterGlobalHotkey(int id, string hotkey)
    {
        _globalHotkeySettingsCoordinator.RegisterGlobalHotkey(id, hotkey);
    }

    public void ApplyRunOnStartup(bool runOnStartup)
    {
        _startupRegistrationService.SetStartup(runOnStartup);
    }

    /// <summary>
    /// True when the OS has switched our startup entry off, so it won't launch at login no matter what we write
    /// to the registry (Windows: Task Manager → "Startup apps" → Disable).
    /// </summary>
    public bool IsStartupDisabledByOs()
    {
        return _startupRegistrationService.IsDisabledByOs();
    }

    public void ApplyThemeColors(Color themeColor, Color themeDeepColor)
    {
        _themeResourceService.UpdateThemeColors(themeColor, themeDeepColor);
    }

    public void QueueSave(bool markModified)
    {
        if (markModified)
        {
            _markModified();
        }

        _requestSave();
    }
}
