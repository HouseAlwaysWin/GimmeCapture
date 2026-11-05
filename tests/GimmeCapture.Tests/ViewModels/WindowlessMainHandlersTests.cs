using System;
using System.Collections.Generic;
using GimmeCapture.Services.Abstractions;
using GimmeCapture.Services.Core.Infrastructure;
using GimmeCapture.ViewModels.Main;

namespace GimmeCapture.Tests.ViewModels;

/// <summary>
/// The stand-ins for the main window's own handling while none is built, as in a launch to the tray at sign-in: a
/// hotkey another app holds went unsaid there, and the capture overlay's request for the Modules tab did nothing.
/// </summary>
public class WindowlessMainHandlersTests
{
    private readonly List<string> _status = new();
    private readonly List<(string Message, MainWindowViewModel.ToastSeverity Severity, Action OnClick)> _toasts = new();
    private int _mainWindowShown;
    private Action? _openModules;

    private WindowlessMainHandlers Handlers()
    {
        var localization = new Mock<ILocalizationService>();
        localization.Setup(l => l[It.IsAny<string>()]).Returns((string key) => key);
        localization.Setup(l => l["HotkeyRegisterFailedMessage"]).Returns("{0} ({1}) is taken");
        return new WindowlessMainHandlers(
            localization.Object,
            () => () => _mainWindowShown++,
            () => (message, severity, onClick) => _toasts.Add((message, severity, onClick)),
            () => _openModules,
            _status.Add,
            work => work());
    }

    [Fact]
    public void AHotkeyAnotherAppHolds_IsToldInAToast_ThatOpensTheMainWindow()
    {
        var handlers = Handlers();

        handlers.HotkeyRegistrationFailed(HotkeyIds.Snip, "Ctrl+Shift+A", 1409);

        var toast = Assert.Single(_toasts);
        Assert.Equal("StartCapture (Ctrl+Shift+A) is taken", toast.Message);
        Assert.Equal(MainWindowViewModel.ToastSeverity.Error, toast.Severity);
        Assert.Equal(0, _mainWindowShown);
        toast.OnClick();
        Assert.Equal(1, _mainWindowShown);
        Assert.Contains(_status, s => s.Contains("RegisterFailed", StringComparison.Ordinal));
    }

    [Fact]
    public void ATemporaryOverlayHotkey_IsNotToasted()
    {
        // It only mirrors a key the keyboard hook serves, as the main window's dialog skips it too.
        Handlers().HotkeyRegistrationFailed(HotkeyIds.OverlayCopy, "Ctrl+C", 1409);

        Assert.Empty(_toasts);
        Assert.Single(_status);
    }

    [Fact]
    public void TheOverlaysRequestForModules_OpensTheMainWindow_ThenItsTab()
    {
        int tabOpened = 0;
        Action windowsOwn = () => tabOpened++;

        // Showing the main window wires its own handling, as MainWindow.OnDataContextChanged does.
        var withWindow = new WindowlessMainHandlers(
            Mock.Of<ILocalizationService>(),
            () => () =>
            {
                _mainWindowShown++;
                _openModules = windowsOwn;
            },
            () => null,
            () => _openModules,
            _status.Add,
            work => work());
        _openModules = withWindow.OpenModules; // the stand-in is in place until the main window wires its own

        withWindow.OpenModules();

        Assert.Equal(1, _mainWindowShown);
        Assert.Equal(1, tabOpened);
    }

    [Fact]
    public void WithNoMainWindowToOpen_TheRequestDoesNotCallItself()
    {
        var handlers = Handlers();
        _openModules = handlers.OpenModules;

        handlers.OpenModules(); // the window shown wired nothing: the stand-in is still the current one

        Assert.Equal(1, _mainWindowShown);
    }
}
