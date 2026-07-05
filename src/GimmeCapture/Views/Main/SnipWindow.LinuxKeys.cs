using System;

namespace GimmeCapture.Views.Main;

// Linux snip-overlay action-key priority. On Windows the low-level keyboard hook keeps the snip action
// keys working while snipping regardless of focus. On Linux, once the click-through input shape lets the
// user click a window beneath the overlay, X focus moves there and Avalonia KeyDown no longer reaches the
// overlay. So we register the action key as a TEMPORARY global hotkey through the working
// LinuxGlobalHotkeyService (routed via HandleGlobalHotkey → ActiveAction), exactly like the manual
// scrolling-capture session does — focus-independent, and it reuses the single proven X grab client.
public partial class SnipWindow
{
    private void StartLinuxSnipKeyGrab()
    {
        if (OperatingSystem.IsLinux())
        {
            _viewModel?.RegisterLinuxSnipActionHotkeys();
        }
    }

    private void StopLinuxSnipKeyGrab()
    {
        if (OperatingSystem.IsLinux())
        {
            _viewModel?.UnregisterLinuxSnipActionHotkeys();
        }
    }
}
