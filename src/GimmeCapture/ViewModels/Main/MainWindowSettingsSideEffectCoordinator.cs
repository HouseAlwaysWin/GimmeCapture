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
