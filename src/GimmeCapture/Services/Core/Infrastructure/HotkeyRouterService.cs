using System;
using GimmeCapture.Models;

namespace GimmeCapture.Services.Core.Infrastructure;

public class HotkeyRouterService
{
    public enum SnipGlobalHotkeyAction
    {
        None,
        ActiveAction,
        ToggleToolbar,
        ScreenshotMode,
        RecordingMode,
        TranslateMode,
        CopyAutoAction,
        TextCopyAutoAction,
        ScrollingCaptureAutoAction
    }

    public enum WindowHotkeyAction
    {
        None,
        ActiveAction,
        ToggleToolbar,
        Save,
        Copy,
        ModeCursor,
        ModeSingle,
        ModeMulti,
        TranslateAll
    }

    public bool TryMapGlobalHotkeyToCaptureMode(int hotkeyId, out CaptureMode mode)
    {
        switch (hotkeyId)
        {
            case HotkeyIds.Snip:
                mode = CaptureMode.Normal;
                return true;
            case HotkeyIds.Record:
                mode = CaptureMode.Record;
                return true;
            case HotkeyIds.Pin:
                mode = CaptureMode.Pin;
                return true;
            case HotkeyIds.Translate:
                mode = CaptureMode.Translate;
                return true;
            case HotkeyIds.TextCopy:
                mode = CaptureMode.TextCopy;
                return true;
            case HotkeyIds.ScrollingCapture:
                mode = CaptureMode.ScrollingCapture;
                return true;
            default:
                mode = default;
                return false;
        }
    }

    public string GetPressedHotkeyText(
        int hotkeyId,
        string snipHotkey,
        string recordHotkey,
        string translateHotkey,
        string textCopyHotkey = "")
    {
        return hotkeyId switch
        {
            HotkeyIds.Snip => snipHotkey,
            HotkeyIds.Record => recordHotkey,
            HotkeyIds.Translate => translateHotkey,
            HotkeyIds.TextCopy => textCopyHotkey,
            _ => string.Empty
        };
    }

    public SnipGlobalHotkeyAction ResolveSnipGlobalHotkeyAction(
        int hotkeyId,
        string pressedHotkey,
        string activeActionHotkey,
        string activeToolbarHotkey)
    {
        if (!string.IsNullOrWhiteSpace(pressedHotkey))
        {
            if (pressedHotkey == activeActionHotkey) return SnipGlobalHotkeyAction.ActiveAction;
            if (pressedHotkey == activeToolbarHotkey) return SnipGlobalHotkeyAction.ToggleToolbar;
        }

        return hotkeyId switch
        {
            HotkeyIds.Snip => SnipGlobalHotkeyAction.ScreenshotMode,
            HotkeyIds.Record => SnipGlobalHotkeyAction.RecordingMode,
            HotkeyIds.Pin => SnipGlobalHotkeyAction.ActiveAction,
            HotkeyIds.Translate => SnipGlobalHotkeyAction.TranslateMode,
            HotkeyIds.Copy => SnipGlobalHotkeyAction.CopyAutoAction,
            HotkeyIds.TextCopy => SnipGlobalHotkeyAction.TextCopyAutoAction,
            HotkeyIds.ScrollingCapture => SnipGlobalHotkeyAction.ScrollingCaptureAutoAction,
            _ => SnipGlobalHotkeyAction.None
        };
    }

    public WindowHotkeyAction ResolveWindowHotkeyAction(
        string activeActionHotkey,
        string activeToolbarHotkey,
        string saveHotkey,
        string copyHotkey,
        Func<string, bool> isMatch)
    {
        if (!string.IsNullOrWhiteSpace(activeActionHotkey) && isMatch(activeActionHotkey))
            return WindowHotkeyAction.ActiveAction;

        if (!string.IsNullOrWhiteSpace(activeToolbarHotkey) && isMatch(activeToolbarHotkey))
            return WindowHotkeyAction.ToggleToolbar;

        if (!string.IsNullOrWhiteSpace(saveHotkey) && isMatch(saveHotkey))
            return WindowHotkeyAction.Save;

        if (!string.IsNullOrWhiteSpace(copyHotkey) && isMatch(copyHotkey))
            return WindowHotkeyAction.Copy;

        return WindowHotkeyAction.None;
    }

    public WindowHotkeyAction ResolveTranslationModeHotkeyAction(
        string modeCursorHotkey,
        string modeSingleHotkey,
        string modeMultiHotkey,
        Func<string, bool> isMatch)
    {
        if (!string.IsNullOrWhiteSpace(modeCursorHotkey) && isMatch(modeCursorHotkey))
            return WindowHotkeyAction.ModeCursor;

        if (!string.IsNullOrWhiteSpace(modeSingleHotkey) && isMatch(modeSingleHotkey))
            return WindowHotkeyAction.ModeSingle;

        if (!string.IsNullOrWhiteSpace(modeMultiHotkey) && isMatch(modeMultiHotkey))
            return WindowHotkeyAction.ModeMulti;

        return WindowHotkeyAction.None;
    }

    public WindowHotkeyAction ResolveSpecificTranslationAction(
        string translateAllHotkey,
        Func<string, bool> isMatch)
    {
        if (!string.IsNullOrWhiteSpace(translateAllHotkey) && isMatch(translateAllHotkey))
            return WindowHotkeyAction.TranslateAll;

        return WindowHotkeyAction.None;
    }
}
