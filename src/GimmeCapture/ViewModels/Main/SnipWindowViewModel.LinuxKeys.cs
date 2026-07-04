using System;
using GimmeCapture.Services.Core.Infrastructure;

namespace GimmeCapture.ViewModels.Main;

// Linux: register the snip action key (Pin / recording action) as a temporary GLOBAL hotkey while the
// overlay is open, so it keeps working after click-through moves X focus to a window beneath the overlay.
// XGrabKey (via LinuxGlobalHotkeyService) delivers regardless of focus; HandleGlobalHotkey routes
// HotkeyIds.Pin → SnipGlobalHotkeyAction.ActiveAction → the same command OnKeyDown fires. This mirrors the
// manual scrolling-capture session's temporary Pin/Close hotkeys. Windows keeps using its LL keyboard hook.
public partial class SnipWindowViewModel
{
    public void RegisterLinuxSnipActionHotkeys()
    {
        if (!OperatingSystem.IsLinux() || _mainVm?.HotkeyService == null)
        {
            return;
        }

        _mainVm.HotkeyService.Register(HotkeyIds.Pin, ActiveActionHotkey);
        AppLog.Information($"SnipWindow.RegisterLinuxSnipActionHotkeys action='{ActiveActionHotkey}'");
    }

    public void UnregisterLinuxSnipActionHotkeys()
    {
        if (!OperatingSystem.IsLinux() || _mainVm?.HotkeyService == null)
        {
            return;
        }

        _mainVm.HotkeyService.Unregister(HotkeyIds.Pin);
    }
}
