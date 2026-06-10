using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using GimmeCapture.Models;
using GimmeCapture.ViewModels.Main;
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

        RefreshWindowState();
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

    public static IReadOnlyList<UserSelectionRect> GetCaptureSelectionSnapshots(
        PixelPoint targetScreenOffset,
        double targetVisualScaling)
    {
        if (_window?.DataContext is not FloatingTranslationLayerViewModel vm
            || vm.Items.Count == 0
            || targetVisualScaling <= 0)
        {
            return Array.Empty<UserSelectionRect>();
        }

        double sourceScaling = vm.VisualScaling > 0 ? vm.VisualScaling : 1.0;
        var snapshots = new List<UserSelectionRect>(vm.Items.Count);

        foreach (var item in vm.Items)
        {
            string displayText = item.PrimaryText;
            if (string.IsNullOrWhiteSpace(displayText)
                || item.Bounds.Width <= 0
                || item.Bounds.Height <= 0)
            {
                continue;
            }

            double physicalX = vm.ScreenOffset.X + (item.Bounds.X * sourceScaling);
            double physicalY = vm.ScreenOffset.Y + (item.Bounds.Y * sourceScaling);
            var targetBounds = new Rect(
                (physicalX - targetScreenOffset.X) / targetVisualScaling,
                (physicalY - targetScreenOffset.Y) / targetVisualScaling,
                item.Bounds.Width * sourceScaling / targetVisualScaling,
                item.Bounds.Height * sourceScaling / targetVisualScaling);

            snapshots.Add(new UserSelectionRect
            {
                Bounds = targetBounds,
                IsTranslated = true,
                TranslatedText = displayText,
                OriginalText = item.OriginalText,
                InferredFontSize = item.InferredFontSize * sourceScaling / targetVisualScaling,
                DisplayFontSize = item.DisplayFontSize * sourceScaling / targetVisualScaling,
                EstimatedTextHeight = item.EstimatedTextHeight * sourceScaling / targetVisualScaling,
                IsTextOverflowing = item.IsTextOverflowing
            });
        }

        return snapshots;
    }

    public static void RefreshWindowState()
    {
        if (_window == null)
        {
            return;
        }

        if (_window.DataContext is FloatingTranslationLayerViewModel vm && vm.Items.Count == 0)
        {
            _window.Hide();
            return;
        }

        _window.Topmost = !IsTranslationSnipWindowVisible();
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

    private static bool IsTranslationSnipWindowVisible()
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
        {
            return false;
        }

        return desktop.Windows
            .OfType<SnipWindow>()
            .Any(window =>
                window.IsVisible
                && window.DataContext is SnipWindowViewModel vm
                && vm.IsTranslationMode);
    }
}
