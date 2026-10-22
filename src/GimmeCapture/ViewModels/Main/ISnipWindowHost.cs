using System;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using GimmeCapture.Models;
using GimmeCapture.Services.Core.Infrastructure;

namespace GimmeCapture.ViewModels.Main;

/// <summary>
/// What the capture overlay asks of the window that opened it: the status line and its toasts, a confirmation, running
/// a capture action through the main window's countdown, and opening the Modules tab. <see cref="MainWindowViewModel"/>
/// implements it; an overlay opened without one uses <see cref="NullSnipWindowHost"/>.
/// </summary>
public interface ISnipWindowHost
{
    /// <summary>Shows a status (a localization key), with a toast unless it is routine.</summary>
    void SetStatus(string key);

    /// <summary>Shows an OCR outcome, naming the recognized language when the user asked for auto-detect.</summary>
    void SetOcrStatus(string key, OCRLanguage? recognizedLanguage);

    /// <summary>Shows whether an image copy landed; the host takes ownership of <paramref name="preview"/>.</summary>
    void SetCopyStatus(bool copied, Bitmap? preview);

    /// <summary>Shows a toast for a localization key without changing the status.</summary>
    void ShowToast(string localizationKey);

    /// <summary>Runs <paramref name="captureAsync"/> after the capture delay the user set, unless it is cancelled.</summary>
    Task<CaptureLaunchResult> RunCaptureActionAsync(CaptureMode mode, Func<Task> captureAsync);

    /// <summary>Asks a question (title, message, whether to offer Cancel); null when there is no window to ask in.</summary>
    Func<string, string, bool, Task<bool>>? ConfirmAction { get; }

    /// <summary>Opens the Modules tab, to download a missing module; null when there is none.</summary>
    Action? RequestOpenModulesAction { get; }
}

/// <summary>
/// The host of an overlay opened on its own (in a test or the designer): statuses go nowhere, nothing can be confirmed,
/// and a capture action runs at once.
/// </summary>
public sealed class NullSnipWindowHost : ISnipWindowHost
{
    public static NullSnipWindowHost Instance { get; } = new();

    private NullSnipWindowHost()
    {
    }

    public void SetStatus(string key)
    {
    }

    public void SetOcrStatus(string key, OCRLanguage? recognizedLanguage)
    {
    }

    public void SetCopyStatus(bool copied, Bitmap? preview) => preview?.Dispose();

    public void ShowToast(string localizationKey)
    {
    }

    public async Task<CaptureLaunchResult> RunCaptureActionAsync(CaptureMode mode, Func<Task> captureAsync)
    {
        await captureAsync();
        return CaptureLaunchResult.Launched;
    }

    public Func<string, string, bool, Task<bool>>? ConfirmAction => null;

    public Action? RequestOpenModulesAction => null;
}
