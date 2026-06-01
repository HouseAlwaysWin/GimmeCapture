using Avalonia;
using GimmeCapture.Models;
using GimmeCapture.ViewModels.Floating;
using GimmeCapture.Views.Floating;
using System;
using System.Collections.Generic;

namespace GimmeCapture.Services.Core.Infrastructure;

internal static class TranslationResultLayerManager
{
    private static FloatingTranslationWindow? _window;

    public static void ShowOrAppend(
        PixelPoint screenOffset,
        Size viewportSize,
        double visualScaling,
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

        window.Topmost = true;
        window.Activate();
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
}
