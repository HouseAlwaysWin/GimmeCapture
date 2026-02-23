using GimmeCapture.ViewModels.Main;

namespace GimmeCapture.Services.Core.Infrastructure;

public class HotkeyRouterService
{
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
}
