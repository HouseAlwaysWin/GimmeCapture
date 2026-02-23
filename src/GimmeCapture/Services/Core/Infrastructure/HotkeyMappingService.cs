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
            { "TranslateHotkey", (vm, val) => vm.TranslateHotkey = val },
            
            // Snip Mode Actions
            { "SnipRectangleHotkey", (vm, val) => vm.SnipRectangleHotkey = val },
            { "SnipEllipseHotkey", (vm, val) => vm.SnipEllipseHotkey = val },
            { "SnipArrowHotkey", (vm, val) => vm.SnipArrowHotkey = val },
            { "SnipLineHotkey", (vm, val) => vm.SnipLineHotkey = val },
            { "SnipPenHotkey", (vm, val) => vm.SnipPenHotkey = val },
            { "SnipTextHotkey", (vm, val) => vm.SnipTextHotkey = val },
            { "SnipMosaicHotkey", (vm, val) => vm.SnipMosaicHotkey = val },
            { "SnipBlurHotkey", (vm, val) => vm.SnipBlurHotkey = val },
            { "SnipUndoHotkey", (vm, val) => vm.SnipUndoHotkey = val },
            { "SnipRedoHotkey", (vm, val) => vm.SnipRedoHotkey = val },
            { "SnipClearHotkey", (vm, val) => vm.SnipClearHotkey = val },
            { "SnipSaveHotkey", (vm, val) => vm.SnipSaveHotkey = val },
            { "SnipCopyHotkey", (vm, val) => vm.SnipCopyHotkey = val },
            { "SnipPinHotkey", (vm, val) => vm.SnipPinHotkey = val },
            { "SnipCloseHotkey", (vm, val) => vm.SnipCloseHotkey = val },
            { "SnipToolbarHotkey", (vm, val) => vm.SnipToolbarHotkey = val },
            { "SnipSelectionModeHotkey", (vm, val) => vm.SnipSelectionModeHotkey = val },
            { "SnipCropModeHotkey", (vm, val) => vm.SnipCropModeHotkey = val },

            // Record Mode Actions
            { "RecordRectangleHotkey", (vm, val) => vm.RecordRectangleHotkey = val },
            { "RecordEllipseHotkey", (vm, val) => vm.RecordEllipseHotkey = val },
            { "RecordArrowHotkey", (vm, val) => vm.RecordArrowHotkey = val },
            { "RecordLineHotkey", (vm, val) => vm.RecordLineHotkey = val },
            { "RecordPenHotkey", (vm, val) => vm.RecordPenHotkey = val },
            { "RecordTextHotkey", (vm, val) => vm.RecordTextHotkey = val },
            { "RecordMosaicHotkey", (vm, val) => vm.RecordMosaicHotkey = val },
            { "RecordBlurHotkey", (vm, val) => vm.RecordBlurHotkey = val },
            { "RecordUndoHotkey", (vm, val) => vm.RecordUndoHotkey = val },
            { "RecordRedoHotkey", (vm, val) => vm.RecordRedoHotkey = val },
            { "RecordClearHotkey", (vm, val) => vm.RecordClearHotkey = val },
            { "RecordSaveHotkey", (vm, val) => vm.RecordSaveHotkey = val },
            { "RecordCopyHotkey", (vm, val) => vm.RecordCopyHotkey = val },
            { "RecordCloseHotkey", (vm, val) => vm.RecordCloseHotkey = val },
            { "RecordActionHotkey", (vm, val) => vm.RecordActionHotkey = val },
            { "RecordToolbarHotkey", (vm, val) => vm.RecordToolbarHotkey = val },
            { "RecordPlaybackHotkey", (vm, val) => vm.RecordPlaybackHotkey = val },

            // Translate Mode Actions
            { "TranslateActionHotkey", (vm, val) => vm.TranslateActionHotkey = val },
            { "TranslateToolbarHotkey", (vm, val) => vm.TranslateToolbarHotkey = val },
            { "TranslateCloseHotkey", (vm, val) => vm.TranslateCloseHotkey = val }
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
