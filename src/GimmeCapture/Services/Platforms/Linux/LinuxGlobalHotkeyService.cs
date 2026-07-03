using System;
using Avalonia.Controls;
using GimmeCapture.Services.Abstractions;
using GimmeCapture.Services.Core.Infrastructure;

namespace GimmeCapture.Services.Platforms.Linux;

/// <summary>
/// No-op <see cref="IGlobalHotkeyService"/> for non-Windows (Linux) runs. Global hotkeys on Linux
/// need X11 XGrabKey or a desktop portal binding — future work (docs/LINUX_PORT_FEASIBILITY.md,
/// Phase 2). This lets the app start without registering any system-wide hotkeys.
/// </summary>
public sealed class LinuxGlobalHotkeyService : IGlobalHotkeyService
{
    public Action<int>? OnHotkeyPressed { get; set; }
    public Action<int, string, int>? OnHotkeyRegistrationFailed { get; set; }
    public Action? OnElevatedWindowFocused { get; set; }

    public void Initialize(Window window)
    {
        AppLog.Information("LinuxGlobalHotkey.Initialize.NoOp");
    }

    public void Register(int id, string hotkey)
    {
        // Report as a soft failure so the UI can surface "hotkeys unavailable on this platform".
        OnHotkeyRegistrationFailed?.Invoke(id, hotkey, 0);
    }

    public void Unregister(int id)
    {
    }

    public void SuspendAll()
    {
    }

    public void ResumeAll()
    {
    }

    public void Dispose()
    {
    }
}
