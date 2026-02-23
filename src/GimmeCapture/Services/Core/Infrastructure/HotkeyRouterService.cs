using System;
using GimmeCapture.ViewModels.Main;

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
        CopyAutoAction
    }

    public enum WindowHotkeyAction
    {
        None,
        ActiveAction,
        ToggleToolbar,
        Save,
        Copy
    }

    public bool TryMapGlobalHotkeyToCaptureMode(int hotkeyId, out MainWindowViewModel.CaptureMode mode)
    {
        switch (hotkeyId)
        {
            case HotkeyIds.Snip:
                mode = MainWindowViewModel.CaptureMode.Normal;
                return true;
            case HotkeyIds.Record:
                mode = MainWindowViewModel.CaptureMode.Record;
                return true;
            case HotkeyIds.Pin:
                mode = MainWindowViewModel.CaptureMode.Pin;
                return true;
            case HotkeyIds.Translate:
                mode = MainWindowViewModel.CaptureMode.Translate;
                return true;
            default:
                mode = default;
                return false;
        }
    }

    public string GetPressedHotkeyText(int hotkeyId, MainWindowViewModel vm)
    {
        return hotkeyId switch
        {
            HotkeyIds.Snip => vm.SnipHotkey,
            HotkeyIds.Record => vm.RecordHotkey,
            HotkeyIds.Translate => vm.TranslateHotkey,
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
}
