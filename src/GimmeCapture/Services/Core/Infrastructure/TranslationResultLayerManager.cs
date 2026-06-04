using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using GimmeCapture.Models;
using GimmeCapture.ViewModels.Floating;
using GimmeCapture.Views.Floating;
using GimmeCapture.Views.Main;
using System;
using System.Collections.Generic;
using System.Linq;

namespace GimmeCapture.Services.Core.Infrastructure;

internal static class TranslationResultLayerManager
{
    private static FloatingTranslationWindow? _window;

    public static void ShowOrAppend(
        PixelPoint screenOffset,
        Size viewportSize,
        double visualScaling,
        Color borderColor,
        IEnumerable<TranslationResultItem> items,
        Func<object?, System.Threading.Tasks.Task> copyAction,
        Func<TranslationResultItem, System.Threading.Tasks.Task> pinAction)
    {
        var window = EnsureWindow();
        if (window.DataContext is not FloatingTranslationLayerViewModel vm)
        {
            return;
        }

        vm.ScreenOffset = screenOffset;
        vm.ViewportSize = viewportSize;
        vm.VisualScaling = visualScaling;
        vm.BorderColor = borderColor;
        vm.CopyAction = copyAction;
        vm.PinAction = pinAction;
        vm.AddItems(items);

        window.Position = screenOffset;
        window.Width = Math.Max(1, viewportSize.Width);
        window.Height = Math.Max(1, viewportSize.Height);

        if (!window.IsVisible)
        {
            window.Show();
        }

        window.Topmost = !IsSnipWindowVisible();
    }

    public static void ClearAll()
    {
        if (_window?.DataContext is not FloatingTranslationLayerViewModel vm)
        {
            return;
        }

        vm.ClearAll();
        _window.Hide();
    }

    private static FloatingTranslationWindow EnsureWindow()
    {
        if (_window != null)
        {
            return _window;
        }

        var window = new FloatingTranslationWindow
        {
            DataContext = new FloatingTranslationLayerViewModel()
        };
        window.Closed += (_, _) => _window = null;
        _window = window;
        return window;
    }

    private static bool IsSnipWindowVisible()
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
        {
            return false;
        }

        return desktop.Windows.OfType<SnipWindow>().Any(window => window.IsVisible);
    }
}
