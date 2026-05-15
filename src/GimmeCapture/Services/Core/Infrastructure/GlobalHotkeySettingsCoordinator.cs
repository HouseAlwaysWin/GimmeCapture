using System;
using GimmeCapture.Services.Abstractions;

namespace GimmeCapture.Services.Core.Infrastructure;

public sealed class GlobalHotkeySettingsCoordinator : IGlobalHotkeySettingsCoordinator
{
    private readonly IGlobalHotkeyService _hotkeyService;

    public GlobalHotkeySettingsCoordinator(IGlobalHotkeyService hotkeyService)
    {
        _hotkeyService = hotkeyService ?? throw new ArgumentNullException(nameof(hotkeyService));
    }

    public void RegisterGlobalHotkey(int id, string hotkey)
    {
        _hotkeyService.Register(id, hotkey);
    }
}
