using System;
using System.Collections.Generic;
using System.Reactive.Linq;
using GimmeCapture.Models;
using GimmeCapture.Services.Core.Infrastructure;

namespace GimmeCapture.ViewModels.Main;

/// <summary>
/// Capture-flow keys for an overlay that deliberately does NOT hold focus (CaptureWithoutStealingFocus, which
/// applies while the overlay is live rather than frozen — recording, or screenshots with freeze-frame off).
///
/// Those keys are normally matched in the process-wide low-level keyboard hook (SnipWindow.Win32.cs), and
/// Windows stops feeding that hook the moment a window running at a higher integrity level has focus (UIPI).
/// Recording an app that runs as administrator therefore left Esc / the action key / Ctrl+C / Ctrl+S quietly
/// doing nothing. RegisterHotKey is not subject to UIPI — WM_HOTKEY reaches our message window whatever is in
/// front — so for as long as the overlay stays unfocused the same keys are ALSO registered as temporary global
/// hotkeys, exactly like a manual scrolling session does for finish/cancel (SnipWindowViewModel.ScrollingManual.cs).
///
/// Only the core capture-flow keys are mirrored: dismiss, the active action, Copy/Save, and recording
/// pause/stop. The rest of the unfocused set (mode switches, fullscreen select, toolbar toggle) stays hook-only
/// rather than taking those keys away from every other app for as long as an overlay is open. A registration
/// that fails because another app owns the combo is ignored — the hook still serves it wherever Windows allows.
/// </summary>
public partial class SnipWindowViewModel
{
    private readonly HashSet<int> _unfocusedOverlayHotkeyIds = new();

    /// <summary>What the current registrations were built from; re-registering is skipped while it is unchanged.</summary>
    private string? _unfocusedOverlayHotkeySignature;

    /// <summary>
    /// Brings the temporary registrations in line with <see cref="ShouldAvoidStealingFocus"/> and the hotkeys of
    /// the current mode. Called on every input that can flip either (mode, translation, text entry, freeze).
    /// </summary>
    private void SyncUnfocusedOverlayHotkeys()
    {
        // A manual scrolling session registers the same action/close keys for finish/cancel; leaving ours in
        // place would just lose the race for the combo and report a conflict.
        string signature = ShouldAvoidStealingFocus && !_manualScrollActive
            ? $"{CurrentMode}|{CloseHotkey}|{ActiveActionHotkey}|{CopyHotkey}|{SaveHotkey}|{ActivePlaybackHotkey}|{ActiveStopHotkey}"
            : string.Empty;

        if (signature == _unfocusedOverlayHotkeySignature)
        {
            return;
        }

        _unfocusedOverlayHotkeySignature = signature;
        UnregisterUnfocusedOverlayHotkeys();

        if (signature.Length == 0)
        {
            return;
        }

        RegisterUnfocusedOverlayHotkey(HotkeyIds.OverlayDismiss, CloseHotkey);
        RegisterUnfocusedOverlayHotkey(HotkeyIds.OverlayAction, ActiveActionHotkey);

        // Copy/Save only as modifier combos, for the same reason the hook restricts them: a bare letter would be
        // taken away from whatever the user is typing in the window behind the overlay.
        RegisterUnfocusedOverlayHotkey(HotkeyIds.OverlayCopy, CopyHotkey, modifierCombosOnly: true);
        RegisterUnfocusedOverlayHotkey(HotkeyIds.OverlaySave, SaveHotkey, modifierCombosOnly: true);

        if (CurrentMode == SnipMode.Recording)
        {
            RegisterUnfocusedOverlayHotkey(HotkeyIds.OverlayRecordPause, ActivePlaybackHotkey);
            RegisterUnfocusedOverlayHotkey(HotkeyIds.OverlayRecordStop, ActiveStopHotkey);
        }
    }

    private void RegisterUnfocusedOverlayHotkey(int id, string? hotkey, bool modifierCombosOnly = false)
    {
        if (string.IsNullOrWhiteSpace(hotkey)) return;
        if (modifierCombosOnly && !hotkey.Contains('+')) return;
        if (_mainVm?.HotkeyService == null) return;

        _mainVm.HotkeyService.Register(id, hotkey);
        _unfocusedOverlayHotkeyIds.Add(id);
    }

    private void UnregisterUnfocusedOverlayHotkeys()
    {
        if (_unfocusedOverlayHotkeyIds.Count == 0) return;

        foreach (var id in _unfocusedOverlayHotkeyIds)
        {
            _mainVm?.HotkeyService?.Unregister(id);
        }

        _unfocusedOverlayHotkeyIds.Clear();
        _unfocusedOverlayHotkeySignature = null;
    }

    /// <summary>
    /// Routes one of the temporary registrations to the command the hook path uses for the same key. An id we
    /// registered is always reported as handled — including when the gate below says "not right now" — so it can
    /// never fall through and be re-interpreted as a main-window hotkey.
    /// </summary>
    private bool TryHandleUnfocusedOverlayHotkey(int id)
    {
        if (!_unfocusedOverlayHotkeyIds.Contains(id))
        {
            return false;
        }

        if (!CanRouteUnfocusedOverlayHotkey)
        {
            return true;
        }

        switch (id)
        {
            case HotkeyIds.OverlayDismiss:
                DismissOrClose();
                return true;
            case HotkeyIds.OverlayAction:
                HandleActiveActionHotkeyCommand?.Execute().SubscribeLoggingErrors();
                return true;
            case HotkeyIds.OverlayCopy when HasContentForCopyOrSave:
                CopyCommand?.Execute().SubscribeLoggingErrors();
                return true;
            case HotkeyIds.OverlaySave when HasContentForCopyOrSave:
                SaveCommand?.Execute().SubscribeLoggingErrors();
                return true;
            case HotkeyIds.OverlayRecordPause when RecState != RecordingState.Idle:
                PauseRecordingCommand?.Execute().SubscribeLoggingErrors();
                return true;
            case HotkeyIds.OverlayRecordStop when RecState != RecordingState.Idle:
                StopRecordingCommand?.Execute().SubscribeLoggingErrors();
                return true;
        }

        return true;
    }

    /// <summary>Mirrors the hook's unfocused gate: only while the overlay is mid-capture, and never while a text
    /// control owns the keyboard.</summary>
    private bool CanRouteUnfocusedOverlayHotkey =>
        !IsTranslationMode
        && !IsEnteringText
        && !IsInputFocused
        && (CurrentState is SnipState.Detecting or SnipState.Selecting or SnipState.Selected
            || RecState != RecordingState.Idle);

    private bool HasContentForCopyOrSave =>
        CurrentState == SnipState.Selected || IsDrawingMode || RecState != RecordingState.Idle;
}
