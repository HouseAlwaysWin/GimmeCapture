using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using GimmeCapture.Models;
using GimmeCapture.Services.Abstractions;
using GimmeCapture.ViewModels.Main;
using GimmeCapture.ViewModels.Floating;
using GimmeCapture.Views.Floating;
using GimmeCapture.Views.Main;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace GimmeCapture.Services.Platforms.Avalonia;

/// <summary>
/// Injected singleton owning the single always-on-top floating translation-overlay window. Formerly the static
/// <c>TranslationResultLayerManager</c>; converting it to an instance service removed the process-global static
/// state and put the <c>Application.Current</c> lookup behind <see cref="ITranslationResultLayerService"/>.
/// </summary>
public sealed class TranslationResultLayerService : ITranslationResultLayerService
{
    private FloatingTranslationWindow? _window;

    public void ShowOrAppend(
        PixelPoint screenOffset,
        Size viewportSize,
        double visualScaling,
        Color borderColor,
        IEnumerable<TranslationResultItem> items,
        Func<object?, Task> copyAction,
        Func<TranslationResultItem, Task> pinAction)
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

    public void ClearAll()
    {
        if (_window?.DataContext is not FloatingTranslationLayerViewModel vm)
        {
            return;
        }

        vm.ClearAll();
        _window.Hide();
    }

    public IReadOnlyList<UserSelectionRect> GetCaptureSelectionSnapshots(
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

    public void RefreshWindowState()
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

    private FloatingTranslationWindow EnsureWindow()
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
