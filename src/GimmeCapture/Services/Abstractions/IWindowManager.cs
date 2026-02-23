using Avalonia.Controls;

namespace GimmeCapture.Services.Abstractions;

public interface IWindowManager
{
    Window? GetMainWindow();
    TViewModel? GetWindowDataContext<TWindow, TViewModel>()
        where TWindow : Window
        where TViewModel : class;
}
