using System;
using Avalonia.Controls;

namespace GimmeCapture.Services;

public interface IGlobalHotkeyService : IDisposable
{
    void Initialize(Window window);
    void Register(int id, string hotkey);
    void Unregister(int id);
    Action<int>? OnHotkeyPressed { get; set; }
}
