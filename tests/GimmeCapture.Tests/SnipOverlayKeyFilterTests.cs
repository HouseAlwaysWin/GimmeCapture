using GimmeCapture.Services.Core.Infrastructure;

namespace GimmeCapture.Tests;

/// <summary>
/// <see cref="SnipOverlayKeyFilter"/> decides on the overlay's hook thread which keys wait for the UI thread to be
/// routed. These pin it against the routing it mirrors (<c>SnipWindow.HandleGlobalKeyboardEvent</c>), with the
/// default hotkeys from AppSettings: every key routing acts on must get through, and plain typing must not wait.
/// </summary>
public class SnipOverlayKeyFilterTests
{
    // The default recording-mode hotkeys (AppSettings.RecordHotkeys), plus the translation ones the record carries.
    private static readonly SnipOverlayHotkeys Recording = new(
        IsTranslationMode: false,
        SelectionHoldModifier: "Ctrl",
        Close: "Escape",
        Toolbar: "F4",
        Action: "F6",
        FullscreenSelect: "F",
        SwitchToSnip: "F1",
        SwitchToRecord: "F2",
        SwitchToTranslate: "F3",
        Playback: "Space",
        Stop: "F9",
        Copy: "Ctrl+C",
        Save: "Ctrl+S",
        Undo: "Ctrl+Z",
        Redo: "Ctrl+Y",
        Clear: "Delete",
        Rectangle: "R",
        Ellipse: "E",
        Arrow: "A",
        Line: "L",
        Mosaic: "M",
        Blur: "B",
        Pen: "P",
        Text: "T",
        TranslateAll: "T",
        TranslatePin: "F6",
        ScanAll: "S",
        ClearAll: "Delete");

    // TranslateHotkeys' defaults.
    private static readonly SnipOverlayHotkeys Translation = Recording with
    {
        IsTranslationMode = true,
        Action = "F8",
    };

    [Theory]
    [InlineData("Escape")] // dismiss
    [InlineData("F4")]     // toolbar
    [InlineData("F6")]     // pin, or finish a manual scroll
    [InlineData("F")]      // not a safe single key, but the routing only takes F-keys, Esc and PrintScreen here
    [InlineData("F1")]     // switch to snip
    [InlineData("Space")]  // pause, even from another app
    [InlineData("F9")]     // stop
    public void Recording_WithAnotherAppFocused_RoutesTheCaptureFlowKeys(string key)
    {
        var filter = SnipOverlayKeyFilter.Build(Recording);

        bool expected = key != "F";
        Assert.Equal(expected, filter.ShouldRoute(key, isKeyDown: true, overlayHasFocus: false, modifierDown: false));
    }

    [Theory]
    [InlineData("R")] // drawing tools only act while the translation overlay has focus
    [InlineData("E")]
    [InlineData("T")]
    [InlineData("C")]
    [InlineData("S")]
    [InlineData("Enter")]
    [InlineData("5")]
    public void Recording_WithAnotherAppFocused_LetsPlainTypingThrough(string key)
    {
        var filter = SnipOverlayKeyFilter.Build(Recording);

        Assert.False(filter.ShouldRoute(key, isKeyDown: true, overlayHasFocus: false, modifierDown: false));
    }

    [Fact]
    public void Recording_CopyAndSaveCombos_AreRoutedOnlyWithAModifierHeld()
    {
        var filter = SnipOverlayKeyFilter.Build(Recording);

        Assert.True(filter.ShouldRoute("C", isKeyDown: true, overlayHasFocus: false, modifierDown: true));
        Assert.True(filter.ShouldRoute("S", isKeyDown: true, overlayHasFocus: false, modifierDown: true));
        Assert.False(filter.ShouldRoute("C", isKeyDown: true, overlayHasFocus: false, modifierDown: false));
    }

    [Fact]
    public void Recording_SingleLetterCopyHotkey_IsNeverRoutedFromAnotherApp()
    {
        // The routing takes Copy and Save from another app only as modifier combos, so a bare letter stays typing.
        var filter = SnipOverlayKeyFilter.Build(Recording with { Copy = "C" });

        Assert.False(filter.ShouldRoute("C", isKeyDown: true, overlayHasFocus: false, modifierDown: false));
    }

    [Theory]
    [InlineData("Escape")]
    [InlineData("F4")]
    [InlineData("R")]
    [InlineData("Space")]
    public void ScreenshotOrRecording_WithTheOverlayFocused_RoutesNothing(string key)
    {
        // The overlay's own key bindings handle every key while it has focus.
        var filter = SnipOverlayKeyFilter.Build(Recording);

        Assert.False(filter.ShouldRoute(key, isKeyDown: true, overlayHasFocus: true, modifierDown: false));
    }

    [Theory]
    [InlineData("R", false)] // drawing tools
    [InlineData("T", false)] // translate all
    [InlineData("S", false)] // scan all
    [InlineData("Delete", false)]
    [InlineData("F8", false)]
    [InlineData("F1", false)]
    [InlineData("Z", true)]  // undo, Ctrl+Z
    [InlineData("Escape", false)]
    public void Translation_WithTheOverlayFocused_RoutesItsHotkeys(string key, bool modifierDown)
    {
        var filter = SnipOverlayKeyFilter.Build(Translation);

        Assert.True(filter.ShouldRoute(key, isKeyDown: true, overlayHasFocus: true, modifierDown));
    }

    [Theory]
    [InlineData("Escape", true)] // dismiss from the target app
    [InlineData("F4", true)]     // toolbar
    [InlineData("R", false)]
    [InlineData("T", false)]
    [InlineData("Space", false)]
    [InlineData("F8", false)]
    public void Translation_WithTheTargetAppFocused_RoutesOnlyDismissAndToolbar(string key, bool expected)
    {
        var filter = SnipOverlayKeyFilter.Build(Translation);

        Assert.Equal(expected, filter.ShouldRoute(key, isKeyDown: true, overlayHasFocus: false, modifierDown: false));
    }

    [Fact]
    public void Translation_CtrlSelectionHold_IsTrackedOnPressAndRelease_FocusedOrNot()
    {
        var filter = SnipOverlayKeyFilter.Build(Translation);

        foreach (bool focused in new[] { true, false })
        {
            Assert.True(filter.ShouldRoute("Ctrl", isKeyDown: true, overlayHasFocus: focused, modifierDown: false));
            Assert.True(filter.ShouldRoute("Ctrl", isKeyDown: false, overlayHasFocus: focused, modifierDown: true));
        }
    }

    [Fact]
    public void Translation_ShiftSelectionHold_IsTrackedOnlyWhileTheOverlayHasFocus()
    {
        // The routing follows a Shift or Alt hold only on the focused path; from the target app, only Ctrl.
        var filter = SnipOverlayKeyFilter.Build(Translation with { SelectionHoldModifier = "Shift" });

        Assert.True(filter.ShouldRoute("Shift", isKeyDown: true, overlayHasFocus: true, modifierDown: false));
        Assert.False(filter.ShouldRoute("Shift", isKeyDown: true, overlayHasFocus: false, modifierDown: false));
        Assert.False(filter.ShouldRoute("Ctrl", isKeyDown: true, overlayHasFocus: false, modifierDown: false));
    }

    [Fact]
    public void ReleasesOfOrdinaryKeys_AreNeverRouted()
    {
        // The routing acts on a release only for the selection-hold modifier.
        var filter = SnipOverlayKeyFilter.Build(Translation);

        Assert.False(filter.ShouldRoute("R", isKeyDown: false, overlayHasFocus: true, modifierDown: false));
        Assert.False(filter.ShouldRoute("Escape", isKeyDown: false, overlayHasFocus: false, modifierDown: false));
    }

    [Fact]
    public void ACombinationToolbarHotkey_IsNotRoutedFromAnotherApp()
    {
        // Only a single safe key toggles the toolbar from outside; a combo could swallow the other app's shortcut.
        var filter = SnipOverlayKeyFilter.Build(Recording with { Toolbar = "Ctrl+F4" });

        Assert.False(filter.ShouldRoute("F4", isKeyDown: true, overlayHasFocus: false, modifierDown: true));
        Assert.False(filter.ShouldRoute("F4", isKeyDown: true, overlayHasFocus: false, modifierDown: false));
    }

    [Fact]
    public void None_RoutesNothing()
    {
        Assert.False(SnipOverlayKeyFilter.None.ShouldRoute("Escape", isKeyDown: true, overlayHasFocus: false, modifierDown: false));
        Assert.False(SnipOverlayKeyFilter.None.ShouldRoute("Escape", isKeyDown: true, overlayHasFocus: true, modifierDown: false));
    }

    [Fact]
    public void Hotkeys_CompareByValue_SoAnUnchangedReadSkipsTheRebuild()
    {
        Assert.Equal(Recording, Recording with { });
        Assert.NotEqual(Recording, Recording with { Stop = "F10" });
    }
}
