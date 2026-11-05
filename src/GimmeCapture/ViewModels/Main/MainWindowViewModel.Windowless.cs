namespace GimmeCapture.ViewModels.Main;

public partial class MainWindowViewModel
{
    private WindowlessMainHandlers? _windowless;

    /// <summary>
    /// Puts the stand-ins for the main window's own handling in place (<see cref="WindowlessMainHandlers"/>): before the
    /// hotkeys are registered at start-up, and again when a main window lets go of the view model.
    /// </summary>
    internal void UseWindowlessHandlers()
    {
        _windowless ??= new WindowlessMainHandlers(
            Localization,
            () => ShowMainWindowAction,
            () => ShowClickableToastAction,
            () => RequestOpenModulesAction,
            text => StatusText = text,
            work => Avalonia.Threading.Dispatcher.UIThread.Post(work));
        HotkeyService.OnHotkeyRegistrationFailed = _windowless.HotkeyRegistrationFailed;
        RequestOpenModulesAction = _windowless.OpenModules;
    }
}
