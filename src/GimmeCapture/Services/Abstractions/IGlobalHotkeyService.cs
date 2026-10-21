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

    /// <summary>Raised on the UI thread for each hotkey press.</summary>
    Action<HotkeyPress>? OnHotkeyPressed { get; set; }

    /// <summary>Raised on the thread that called <see cref="Register"/> when the hotkey could not be registered.</summary>
    Action<int, string, int>? OnHotkeyRegistrationFailed { get; set; }

    /// <summary>
    /// Raised when a window running at a higher integrity level (an elevated app such as Task Manager) gains
    /// focus while this process is not elevated AND at least one hotkey is served ONLY by the low-level keyboard
    /// hook, which UIPI silences there. The argument is that hotkey, so the UI can name it. Hotkeys RegisterHotKey
    /// accepted keep working over an elevated window and never raise this. Raised on the UI thread.
    /// </summary>
    Action<string>? OnElevatedWindowFocused { get; set; }
}
