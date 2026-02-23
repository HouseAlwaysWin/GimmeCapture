using System.Collections.Generic;
using Avalonia.Controls;

namespace GimmeCapture.Services.Abstractions;

public interface IWindowLayerService
{
    IReadOnlyList<Window> LowerTopmostWindowsOfType<TWindow>() where TWindow : Window;

    void RestoreTopmostWindows(IEnumerable<Window> windows);
}
