using System;
using System.Collections.Generic;
using GimmeCapture.ViewModels.Main;

namespace GimmeCapture.Services.Core.Infrastructure;

public class HotkeyMappingService
{
    private readonly Dictionary<string, Action<MainWindowViewModel, string>> _mappings;

    public HotkeyMappingService()
    {
        _mappings = new Dictionary<string, Action<MainWindowViewModel, string>>
        {
            { "SnipHotkey", (vm, val) => vm.SnipHotkey = val },
            { "RecordHotkey", (vm, val) => vm.RecordHotkey = val },
            { "CopyHotkey", (vm, val) => vm.CopyHotkey = val },
            { "PinHotkey", (vm, val) => vm.PinHotkey = val },
            
            // Drawing Tools
            { "RectangleHotkey", (vm, val) => vm.RectangleHotkey = val },
            { "EllipseHotkey", (vm, val) => vm.EllipseHotkey = val },
            { "ArrowHotkey", (vm, val) => vm.ArrowHotkey = val },
            { "LineHotkey", (vm, val) => vm.LineHotkey = val },
            { "PenHotkey", (vm, val) => vm.PenHotkey = val },
            { "TextHotkey", (vm, val) => vm.TextHotkey = val },
            { "MosaicHotkey", (vm, val) => vm.MosaicHotkey = val },
            { "BlurHotkey", (vm, val) => vm.BlurHotkey = val },
            
            // Actions
            { "UndoHotkey", (vm, val) => vm.UndoHotkey = val },
            { "RedoHotkey", (vm, val) => vm.RedoHotkey = val },
            { "ClearHotkey", (vm, val) => vm.ClearHotkey = val },
            { "SaveHotkey", (vm, val) => vm.SaveHotkey = val },
            { "CloseHotkey", (vm, val) => vm.CloseHotkey = val },
            { "TogglePlaybackHotkey", (vm, val) => vm.TogglePlaybackHotkey = val },
            { "ToggleToolbarHotkey", (vm, val) => vm.ToggleToolbarHotkey = val },
            { "SelectionModeHotkey", (vm, val) => vm.SelectionModeHotkey = val },
            { "CropModeHotkey", (vm, val) => vm.CropModeHotkey = val }
        };
    }

    public void UpdateViewModelHotkey(MainWindowViewModel vm, string tag, string hotkeyStr)
    {
        if (vm == null || string.IsNullOrEmpty(tag)) return;

        if (_mappings.TryGetValue(tag, out var updateAction))
        {
            updateAction(vm, hotkeyStr);
        }
        else
        {
            System.Diagnostics.Debug.WriteLine($"[HotkeyMappingService] Unknown hotkey tag: {tag}");
        }
    }
}
