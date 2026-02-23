using Avalonia.Controls;

namespace GimmeCapture.Services.Abstractions;

public interface IWindowManager
{
    Window? GetMainWindow();
    Window? GetActiveWindow();
    Window? FindWindowByDataContext(object dataContext);
    TWindow? FindWindowOfType<TWindow>() where TWindow : Window;
    TWindow? GetActiveWindowOfType<TWindow>() where TWindow : Window;
    TViewModel? GetWindowDataContext<TWindow, TViewModel>()
        where TWindow : Window
        where TViewModel : class;
}
