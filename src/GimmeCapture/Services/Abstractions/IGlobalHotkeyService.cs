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
}
