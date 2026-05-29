using System;
using Avalonia.Controls;

namespace GimmeCapture.Services.Abstractions;

public interface IGlobalHotkeyService : IDisposable
{
    void Initialize(Window window);
    void Register(int id, string hotkey);
    void Unregister(int id);
    void SuspendAll();
    void ResumeAll();
    Action<int>? OnHotkeyPressed { get; set; }
    Action<int, string, int>? OnHotkeyRegistrationFailed { get; set; }

    /// <summary>
    /// Raised when a window running at a higher integrity level (e.g. an elevated app such as
    /// Task Manager) gains focus while this process is not elevated. Global hotkeys cannot be
    /// delivered in that situation (Windows UIPI), so the UI can surface a hint to the user.
    /// </summary>
    Action? OnElevatedWindowFocused { get; set; }
}
