using System;
using GimmeCapture.Services.Abstractions;
using GimmeCapture.Services.Core.Infrastructure;

namespace GimmeCapture.ViewModels.Main;

/// <summary>
/// What stands in for the main window while none is built, as in a launch to the tray, where the app starts at sign-in.
/// Its dialog for a hotkey that could not be registered and the Modules tab the capture overlay asks for were both the
/// main window's, so with none built a clash with another app's hotkey went unsaid and the overlay's request to get a
/// model did nothing. A hotkey that could not be registered is told in a toast that opens the main window, and the
/// request opens the main window on its Modules tab. The main window puts its own handling in place while it is up.
/// </summary>
internal sealed class WindowlessMainHandlers
{
    private readonly ILocalizationService _localization;
    private readonly Func<Action?> _showMainWindow;
    private readonly Func<Action<string, MainWindowViewModel.ToastSeverity, Action>?> _clickableToast;
    private readonly Func<Action?> _openModules;
    private readonly Action<string> _setStatus;
    private readonly Action<Action> _post;

    /// <param name="showMainWindow">What opens the main window, read when it is needed: the tray sets it after start-up.</param>
    /// <param name="clickableToast">What shows a toast that does something when clicked, read when it is needed.</param>
    /// <param name="openModules">What opens the Modules tab now: the main window's own once it is up.</param>
    /// <param name="setStatus">Sets the status text, as the main window's handling does.</param>
    /// <param name="post">Runs work on the UI thread later; the hotkey thread reports a failed registration.</param>
    public WindowlessMainHandlers(
        ILocalizationService localization,
        Func<Action?> showMainWindow,
        Func<Action<string, MainWindowViewModel.ToastSeverity, Action>?> clickableToast,
        Func<Action?> openModules,
        Action<string> setStatus,
        Action<Action> post)
    {
        _localization = localization;
        _showMainWindow = showMainWindow;
        _clickableToast = clickableToast;
        _openModules = openModules;
        _setStatus = setStatus;
        _post = post;
    }

    /// <summary>The name a hotkey goes by in what is shown when it could not be registered.</summary>
    internal static string HotkeyName(int id, string hotkey, ILocalizationService localization) => id switch
    {
        HotkeyIds.Snip => localization["StartCapture"],
        HotkeyIds.Record => localization["CaptureModeRecord"],
        HotkeyIds.Translate => localization["TranslateHotkey"],
        HotkeyIds.Copy => localization["TipCopy"],
        HotkeyIds.Pin => localization["TipPin"],
        HotkeyIds.TextCopy => localization["TextCopyHotkey"],
        HotkeyIds.ScrollingCapture => localization["ScrollingCaptureHotkey"],
        _ => hotkey
    };

    /// <summary>The hotkey service's report of a hotkey it could not register, on the thread it reports from.</summary>
    public void HotkeyRegistrationFailed(int id, string hotkey, int error)
    {
        // Temporary registrations (an unfocused overlay's capture keys, a scrolling session's finish and cancel) only
        // mirror keys the keyboard hook already serves, as the main window's dialog skips them too.
        if (HotkeyIds.IsTemporaryOverlayHotkey(id))
        {
            _setStatus($"[RegisterFailed] {hotkey} (temporary overlay hotkey; the keyboard hook still covers it)");
            return;
        }

        string name = HotkeyName(id, hotkey, _localization);
        _setStatus($"[RegisterFailed] {hotkey} -> {name}");
        string message = string.Format(_localization["HotkeyRegisterFailedMessage"], name, hotkey);
        _post(() => _clickableToast()?.Invoke(
            message, MainWindowViewModel.ToastSeverity.Error, () => _showMainWindow()?.Invoke()));
    }

    /// <summary>The capture overlay's request for the Modules tab: the main window opened, then the tab.</summary>
    public void OpenModules()
    {
        _showMainWindow()?.Invoke();
        // Opening the main window put its own handling in place; the tab once the window has been laid out.
        _post(() =>
        {
            if (_openModules() is { } open && !open.Equals((Action)OpenModules))
            {
                open();
            }
        });
    }
}
