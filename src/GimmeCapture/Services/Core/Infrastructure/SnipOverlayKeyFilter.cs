using System;
using System.Collections.Generic;

namespace GimmeCapture.Services.Core.Infrastructure;

/// <summary>
/// The snip overlay's hotkeys as its keyboard routing reads them, captured on the UI thread. Value equality lets the
/// overlay skip rebuilding <see cref="SnipOverlayKeyFilter"/> when none of them changed.
/// </summary>
internal sealed record SnipOverlayHotkeys(
    bool IsTranslationMode,
    string? SelectionHoldModifier,
    string? Close,
    string? Toolbar,
    string? Action,
    string? FullscreenSelect,
    string? SwitchToSnip,
    string? SwitchToRecord,
    string? SwitchToTranslate,
    string? Playback,
    string? Stop,
    string? Copy,
    string? Save,
    string? Undo,
    string? Redo,
    string? Clear,
    string? Rectangle,
    string? Ellipse,
    string? Arrow,
    string? Line,
    string? Mosaic,
    string? Blur,
    string? Pen,
    string? Text,
    string? TranslateAll,
    string? TranslatePin,
    string? ScanAll,
    string? ClearAll);

/// <summary>
/// Which keys the snip overlay's low-level keyboard hook hands to the UI thread. Windows holds every key typed
/// anywhere until that hook returns, so the hook decides here, on its own thread, and lets every key the overlay
/// cannot act on straight through. This mirrors <c>SnipWindow.HandleGlobalKeyboardEvent</c>: a key that routing can
/// act on but this leaves out stops working while the overlay is open, while a key listed in excess only waits for the
/// UI thread, as every key used to.
/// </summary>
internal sealed class SnipOverlayKeyFilter
{
    [Flags]
    private enum Use
    {
        Bare = 1,          // with no Shift, Ctrl or Alt held
        WithModifiers = 2, // with one of them held
        Any = Bare | WithModifiers,
    }

    /// <summary>Routes nothing: every key passes straight through.</summary>
    public static readonly SnipOverlayKeyFilter None = new(
        new Dictionary<string, Use>(StringComparer.OrdinalIgnoreCase),
        new Dictionary<string, Use>(StringComparer.OrdinalIgnoreCase));

    private readonly Dictionary<string, Use> _whenFocused;
    private readonly Dictionary<string, Use> _whenUnfocused;

    private SnipOverlayKeyFilter(Dictionary<string, Use> whenFocused, Dictionary<string, Use> whenUnfocused)
    {
        _whenFocused = whenFocused;
        _whenUnfocused = whenUnfocused;
    }

    /// <param name="key">The key as the overlay names it: "A", "5", "F4", "Escape", "Space", "Ctrl" and so on.</param>
    /// <param name="overlayHasFocus">The overlay, or a window inside it, is the foreground window.</param>
    /// <param name="modifierDown">Shift, Ctrl or Alt is held; for a modifier's own event, as it was before it.</param>
    public bool ShouldRoute(string key, bool isKeyDown, bool overlayHasFocus, bool modifierDown)
    {
        // The routing acts on a release only for Shift, Ctrl and Alt: the translation selection-hold modifier.
        if (!isKeyDown && !IsModifier(key))
        {
            return false;
        }

        var keys = overlayHasFocus ? _whenFocused : _whenUnfocused;
        return keys.TryGetValue(key, out Use use) && (use & (modifierDown ? Use.WithModifiers : Use.Bare)) != 0;
    }

    public static SnipOverlayKeyFilter Build(SnipOverlayHotkeys hotkeys)
    {
        var focused = new Dictionary<string, Use>(StringComparer.OrdinalIgnoreCase);
        var unfocused = new Dictionary<string, Use>(StringComparer.OrdinalIgnoreCase);

        // Any mode, another window focused: the toolbar toggle, when it is a single safe key pressed bare.
        AddSafeSingleKey(unfocused, hotkeys.Toolbar);

        if (hotkeys.IsTranslationMode)
        {
            // Another window focused: Esc or a single-key close hotkey dismisses the overlay, and a Ctrl
            // selection-hold modifier is tracked.
            Add(unfocused, "Escape", Use.Any);
            AddSafeSingleKey(unfocused, hotkeys.Close);
            if (string.Equals(hotkeys.SelectionHoldModifier, "Ctrl", StringComparison.OrdinalIgnoreCase))
            {
                Add(unfocused, "Ctrl", Use.Any);
            }

            // The overlay focused: Esc, the selection-hold modifier and every hotkey the translation overlay acts on.
            Add(focused, "Escape", Use.Any);
            if (hotkeys.SelectionHoldModifier is { } modifier && IsModifier(modifier))
            {
                Add(focused, modifier, Use.Any);
            }

            foreach (string? hotkey in new[]
            {
                hotkeys.Action, hotkeys.Toolbar, hotkeys.Close, hotkeys.Save, hotkeys.Copy,
                hotkeys.Undo, hotkeys.Redo, hotkeys.Clear,
                hotkeys.Rectangle, hotkeys.Ellipse, hotkeys.Arrow, hotkeys.Line,
                hotkeys.Mosaic, hotkeys.Blur, hotkeys.Pen, hotkeys.Text,
                hotkeys.SwitchToSnip, hotkeys.SwitchToRecord, hotkeys.SwitchToTranslate,
                hotkeys.TranslateAll, hotkeys.TranslatePin, hotkeys.ScanAll, hotkeys.ClearAll,
            })
            {
                AddHotkey(focused, hotkey);
            }
        }
        else
        {
            // Screenshot and recording: with the overlay focused its own key bindings take every key, so nothing is
            // routed. With another window focused during a capture: Esc, finishing or cancelling a manual scroll,
            // the single-key capture-flow hotkeys, pause and stop, and Copy and Save when they are modifier combos.
            Add(unfocused, "Escape", Use.Any);
            AddHotkey(unfocused, hotkeys.Action);
            AddHotkey(unfocused, hotkeys.Close);
            foreach (string? hotkey in new[]
            {
                hotkeys.Close, hotkeys.Toolbar, hotkeys.Action, hotkeys.FullscreenSelect,
                hotkeys.SwitchToSnip, hotkeys.SwitchToRecord, hotkeys.SwitchToTranslate,
            })
            {
                AddSafeSingleKey(unfocused, hotkey);
            }

            AddHotkey(unfocused, hotkeys.Playback);
            AddHotkey(unfocused, hotkeys.Stop);
            if (IsModifierCombo(hotkeys.Copy))
            {
                AddHotkey(unfocused, hotkeys.Copy);
            }

            if (IsModifierCombo(hotkeys.Save))
            {
                AddHotkey(unfocused, hotkeys.Save);
            }
        }

        return new SnipOverlayKeyFilter(focused, unfocused);
    }

    // As the routing matches a hotkey: one without '+' only on a bare press, a combo only with its modifiers held.
    private static void AddHotkey(Dictionary<string, Use> keys, string? hotkey)
    {
        if (string.IsNullOrWhiteSpace(hotkey))
        {
            return;
        }

        ReadOnlySpan<char> trimmed = hotkey.AsSpan().Trim();
        bool needsModifier = trimmed.IndexOf('+') >= 0
            && (trimmed.Contains("Ctrl".AsSpan(), StringComparison.OrdinalIgnoreCase)
                || trimmed.Contains("Alt".AsSpan(), StringComparison.OrdinalIgnoreCase)
                || trimmed.Contains("Shift".AsSpan(), StringComparison.OrdinalIgnoreCase));
        Add(keys, HotkeyParsingHelper.GetKeyPart(trimmed).ToString(), needsModifier ? Use.WithModifiers : Use.Bare);
    }

    // The unfocused paths take only a single safe key (F1-F24, Esc, PrintScreen), pressed bare.
    private static void AddSafeSingleKey(Dictionary<string, Use> keys, string? hotkey)
    {
        if (string.IsNullOrWhiteSpace(hotkey) || !HotkeyParsingHelper.IsSafeSingleKeyHotkey(hotkey.AsSpan()))
        {
            return;
        }

        Add(keys, HotkeyParsingHelper.GetKeyPart(hotkey.AsSpan().Trim()).ToString(), Use.Bare);
    }

    private static void Add(Dictionary<string, Use> keys, string key, Use use)
    {
        if (key.Length > 0)
        {
            keys[key] = keys.TryGetValue(key, out Use existing) ? existing | use : use;
        }
    }

    private static bool IsModifierCombo(string? hotkey) =>
        !string.IsNullOrWhiteSpace(hotkey) && hotkey.IndexOf('+') >= 0;

    private static bool IsModifier(string key) =>
        key.Equals("Shift", StringComparison.OrdinalIgnoreCase)
        || key.Equals("Ctrl", StringComparison.OrdinalIgnoreCase)
        || key.Equals("Alt", StringComparison.OrdinalIgnoreCase);
}
