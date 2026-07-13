using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media;
using GimmeCapture.Models;

namespace GimmeCapture.Services.Abstractions;

/// <summary>
/// Owns the single always-on-top floating translation-overlay window (the layer that shows persisted
/// translation results over the screen). Previously a static manager with global state + a direct
/// <c>Application.Current</c> reference; now an injected singleton so the static state and platform coupling
/// live behind an abstraction.
/// </summary>
public interface ITranslationResultLayerService
{
    void ShowOrAppend(
        PixelPoint screenOffset,
        Size viewportSize,
        double visualScaling,
        Color borderColor,
        IEnumerable<TranslationResultItem> items,
        Func<object?, Task> copyAction,
        Func<TranslationResultItem, Task> pinAction);

    void ClearAll();

    IReadOnlyList<UserSelectionRect> GetCaptureSelectionSnapshots(
        PixelPoint targetScreenOffset,
        double targetVisualScaling);

    void RefreshWindowState();
}
