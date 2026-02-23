using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using GimmeCapture.Services.Abstractions;

namespace GimmeCapture.Services.Platforms.Desktop;

public class AvaloniaWindowManager : IWindowManager
{
    public Window? GetMainWindow()
    {
        var desktop = Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime;
        return desktop?.MainWindow;
    }

    public TViewModel? GetWindowDataContext<TWindow, TViewModel>()
        where TWindow : Window
        where TViewModel : class
    {
        var desktop = Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime;
        var window = desktop?.Windows.OfType<TWindow>().FirstOrDefault();
        return window?.DataContext as TViewModel;
    }
}
