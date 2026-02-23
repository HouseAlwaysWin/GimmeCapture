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

    public Window? GetActiveWindow()
    {
        var desktop = Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime;
        return desktop?.Windows.FirstOrDefault(w => w.IsActive);
    }

    public Window? FindWindowByDataContext(object dataContext)
    {
        var desktop = Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime;
        return desktop?.Windows.FirstOrDefault(w => ReferenceEquals(w.DataContext, dataContext));
    }

    public TWindow? FindWindowOfType<TWindow>() where TWindow : Window
    {
        var desktop = Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime;
        return desktop?.Windows.OfType<TWindow>().FirstOrDefault();
    }

    public TWindow? GetActiveWindowOfType<TWindow>() where TWindow : Window
    {
        var desktop = Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime;
        return desktop?.Windows.OfType<TWindow>().FirstOrDefault(w => w.IsActive);
    }
}
