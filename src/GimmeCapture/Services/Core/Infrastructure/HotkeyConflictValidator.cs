using System;
using System.Collections.Generic;
using System.Linq;

namespace GimmeCapture.Services.Core.Infrastructure;

/// <summary>
/// Pure hotkey conflict detection extracted verbatim from MainWindowViewModel so it can be
/// unit-tested in isolation. Given the tag being edited, the proposed hotkey, and a snapshot of
/// all current hotkey values (keyed by tag), returns the localization key of the first
/// conflicting binding, or null when there is no conflict.
///
/// Semantics are preserved exactly from the original inline implementation:
/// - In-group comparisons use ordinal (case-sensitive) equality.
/// - Cross-action comparisons use OrdinalIgnoreCase.
/// - The global group gate lists 6 tags but only 4 are checked as conflict sources.
/// - Unified-pin tags (Snip_Pin / Record_Action / Translate_Pin) skip each other.
/// - The first matching branch wins (ordering matters).
/// </summary>
public static class HotkeyConflictValidator
{
    public static bool IsUnifiedPinHotkeyTag(string tag)
    {
        return tag == "Snip_Pin"
            || tag == "Record_Action"
            || tag == "Translate_Pin";
    }

    public static string? FindConflict(string targetTag, string hotkey, IReadOnlyDictionary<string, string> current)
    {
        string Cur(string tag) => current != null && current.TryGetValue(tag, out var v) ? (v ?? string.Empty) : string.Empty;

        static string? MatchCrossActionConflict(
            string targetTag,
            string hotkey,
            params (string Tag, string CurrentHotkey, string ConflictKey)[] candidates)
        {
            foreach (var candidate in candidates)
            {
                if (targetTag != candidate.Tag && string.Equals(candidate.CurrentHotkey, hotkey, StringComparison.OrdinalIgnoreCase))
                {
                    return candidate.ConflictKey;
                }
            }

            return null;
        }

        // 1. Global Group (Idle state triggers)
        var globalGroup = new[] { "SnipHotkey", "RecordHotkey", "TranslateHotkey", "TextCopyHotkey", "ScrollingCaptureHotkey", "PinHotkey", "CopyHotkey" };

        // 2. Snip Group (Local to Screenshot mode)
        var snipGroup = new[] {
            "Snip_Rectangle", "Snip_Ellipse", "Snip_Arrow", "Snip_Line", "Snip_Pen",
            "Snip_Text", "Snip_Mosaic", "Snip_Blur", "Snip_Undo", "Snip_Redo",
            "Snip_Clear", "Snip_Save", "Snip_Copy", "Snip_Close",
            "Snip_Toolbar", "Snip_SelectionMode", "Snip_CropMode", "Snip_Pin", "Snip_FullscreenSelect",
            "Snip_SwitchToTranslate", "Snip_SwitchToRecord"
        };

        // 3. Record Group (Local to Video Recording mode)
        var recordGroup = new[] {
            "Record_Rectangle", "Record_Ellipse", "Record_Arrow", "Record_Line", "Record_Pen",
            "Record_Text", "Record_Mosaic", "Record_Blur", "Record_Undo", "Record_Redo",
            "Record_Clear", "Record_Save", "Record_Copy", "Record_Close", "Record_Toolbar",
            "Record_Action", "Record_Playback", "Record_FullscreenSelect",
            "Record_SwitchToSnip", "Record_SwitchToTranslate"
        };

        // 4. Translate Group (Local to Translation mode)
        var translateGroup = new[] {
            "Translate_Action", "Translate_Pin", "Translate_Toolbar", "Translate_Close",
            "Translate_TranslateAll", "Translate_ScanAll", "Translate_ClearAll",
            "Translate_ToggleSelect", "Translate_AutoDetect",
            "Translate_SelectionHoldModifier",
            "Translate_ModeCursor", "Translate_ModeSingle", "Translate_ModeMulti",
            "Translate_SwitchToSnip", "Translate_SwitchToRecord"
        };

        if (globalGroup.Contains(targetTag))
        {
            if (targetTag != "SnipHotkey" && Cur("SnipHotkey") == hotkey) return "SnipHotkey";
            if (targetTag != "RecordHotkey" && Cur("RecordHotkey") == hotkey) return "RecordHotkey";
            if (targetTag != "TranslateHotkey" && Cur("TranslateHotkey") == hotkey) return "TranslateHotkey";
            if (targetTag != "TextCopyHotkey" && Cur("TextCopyHotkey") == hotkey) return "TextCopyHotkey";
            if (targetTag != "ScrollingCaptureHotkey" && Cur("ScrollingCaptureHotkey") == hotkey) return "ScrollingCaptureHotkey";
        }
        else if (snipGroup.Contains(targetTag))
        {
            if (targetTag != "Snip_Rectangle" && Cur("Snip_Rectangle") == hotkey) return "TipRectangle";
            if (targetTag != "Snip_Ellipse" && Cur("Snip_Ellipse") == hotkey) return "TipEllipse";
            if (targetTag != "Snip_Arrow" && Cur("Snip_Arrow") == hotkey) return "TipArrow";
            if (targetTag != "Snip_Line" && Cur("Snip_Line") == hotkey) return "TipLine";
            if (targetTag != "Snip_Pen" && Cur("Snip_Pen") == hotkey) return "TipPen";
            if (targetTag != "Snip_Text" && Cur("Snip_Text") == hotkey) return "TipText";
            if (targetTag != "Snip_Mosaic" && Cur("Snip_Mosaic") == hotkey) return "TipMosaic";
            if (targetTag != "Snip_Blur" && Cur("Snip_Blur") == hotkey) return "TipBlur";
            if (targetTag != "Snip_Undo" && Cur("Snip_Undo") == hotkey) return "Undo";
            if (targetTag != "Snip_Redo" && Cur("Snip_Redo") == hotkey) return "Redo";
            if (targetTag != "Snip_Clear" && Cur("Snip_Clear") == hotkey) return "Clear";
            if (targetTag != "Snip_Save" && Cur("Snip_Save") == hotkey) return "Save";
            if (targetTag != "Snip_Copy" && Cur("Snip_Copy") == hotkey) return "TipCopy";
            if (targetTag != "Snip_Close" && Cur("Snip_Close") == hotkey) return "ActionClose";
            if (targetTag != "Snip_Toolbar" && Cur("Snip_Toolbar") == hotkey) return "ActionToolbar";
            if (targetTag != "Snip_SelectionMode" && Cur("Snip_SelectionMode") == hotkey) return "ActionSelectionMode";
            if (targetTag != "Snip_CropMode" && Cur("Snip_CropMode") == hotkey) return "ActionCropMode";
            if (targetTag != "Snip_RemoveBackground" && Cur("Snip_RemoveBackground") == hotkey) return "RemoveBackground";
            if (targetTag != "Snip_MagicWand" && Cur("Snip_MagicWand") == hotkey) return "MagicWand";
            if (targetTag != "Snip_Pin" && !IsUnifiedPinHotkeyTag(targetTag) && Cur("Snip_Pin") == hotkey) return "TipPin";
            if (targetTag != "Snip_FullscreenSelect" && Cur("Snip_FullscreenSelect") == hotkey) return "ActionSelectFullscreen";
            if (targetTag != "Snip_SwitchToTranslate" && Cur("Snip_SwitchToTranslate") == hotkey) return "SwitchToTranslate";
            if (targetTag != "Snip_SwitchToRecord" && Cur("Snip_SwitchToRecord") == hotkey) return "SwitchToRecord";
        }
        else if (recordGroup.Contains(targetTag))
        {
            if (targetTag != "Record_Rectangle" && Cur("Record_Rectangle") == hotkey) return "TipRectangle";
            if (targetTag != "Record_Ellipse" && Cur("Record_Ellipse") == hotkey) return "TipEllipse";
            if (targetTag != "Record_Arrow" && Cur("Record_Arrow") == hotkey) return "TipArrow";
            if (targetTag != "Record_Line" && Cur("Record_Line") == hotkey) return "TipLine";
            if (targetTag != "Record_Pen" && Cur("Record_Pen") == hotkey) return "TipPen";
            if (targetTag != "Record_Text" && Cur("Record_Text") == hotkey) return "TipText";
            if (targetTag != "Record_Mosaic" && Cur("Record_Mosaic") == hotkey) return "TipMosaic";
            if (targetTag != "Record_Blur" && Cur("Record_Blur") == hotkey) return "TipBlur";
            if (targetTag != "Record_Undo" && Cur("Record_Undo") == hotkey) return "Undo";
            if (targetTag != "Record_Redo" && Cur("Record_Redo") == hotkey) return "Redo";
            if (targetTag != "Record_Clear" && Cur("Record_Clear") == hotkey) return "Clear";
            if (targetTag != "Record_Save" && Cur("Record_Save") == hotkey) return "Save";
            if (targetTag != "Record_Copy" && Cur("Record_Copy") == hotkey) return "TipCopy";
            if (targetTag != "Record_Close" && Cur("Record_Close") == hotkey) return "ActionClose";
            if (targetTag != "Record_Toolbar" && Cur("Record_Toolbar") == hotkey) return "ActionToolbar";
            if (targetTag != "Record_Action" && !IsUnifiedPinHotkeyTag(targetTag) && Cur("Record_Action") == hotkey) return "ActionStartPin";
            if (targetTag != "Record_Playback" && Cur("Record_Playback") == hotkey) return "ActionPlayback";
            if (targetTag != "Record_FullscreenSelect" && Cur("Record_FullscreenSelect") == hotkey) return "ActionSelectFullscreen";
            if (targetTag != "Record_SwitchToSnip" && Cur("Record_SwitchToSnip") == hotkey) return "SwitchToSnip";
            if (targetTag != "Record_SwitchToTranslate" && Cur("Record_SwitchToTranslate") == hotkey) return "SwitchToTranslate";
        }
        else if (translateGroup.Contains(targetTag))
        {
            if (targetTag != "Translate_Action" && Cur("Translate_Action") == hotkey) return "ActionHideTranslate";
            if (targetTag != "Translate_Pin" && !IsUnifiedPinHotkeyTag(targetTag) && Cur("Translate_Pin") == hotkey) return "MenuPinTranslation";
            if (targetTag != "Translate_Toolbar" && Cur("Translate_Toolbar") == hotkey) return "ActionToolbar";
            if (targetTag != "Translate_Close" && Cur("Translate_Close") == hotkey) return "ActionClose";
            if (targetTag != "Translate_TranslateAll" && Cur("Translate_TranslateAll") == hotkey) return "ActionTranslateAll";
            if (targetTag != "Translate_ScanAll" && Cur("Translate_ScanAll") == hotkey) return "ActionScanAll";
            if (targetTag != "Translate_ClearAll" && Cur("Translate_ClearAll") == hotkey) return "ActionClearAll";
            if (targetTag != "Translate_ToggleSelect" && Cur("Translate_ToggleSelect") == hotkey) return "ActionToggleSelect";
            if (targetTag != "Translate_AutoDetect" && Cur("Translate_AutoDetect") == hotkey) return "ActionAutoDetect";
            if (targetTag != "Translate_SelectionHoldModifier" && Cur("Translate_SelectionHoldModifier") == hotkey) return "ActionSelectionHoldModifier";
            if (targetTag != "Translate_ModeCursor" && Cur("Translate_ModeCursor") == hotkey) return "TranslateModeCursor";
            if (targetTag != "Translate_ModeSingle" && Cur("Translate_ModeSingle") == hotkey) return "TranslateModeSingle";
            if (targetTag != "Translate_ModeMulti" && Cur("Translate_ModeMulti") == hotkey) return "TranslateModeMulti";
            if (targetTag != "Translate_SwitchToSnip" && Cur("Translate_SwitchToSnip") == hotkey) return "SwitchToSnip";
            if (targetTag != "Translate_SwitchToRecord" && Cur("Translate_SwitchToRecord") == hotkey) return "SwitchToRecord";
        }

        var crossActionCandidates = new List<(string Tag, string CurrentHotkey, string ConflictKey)>
        {
            ("Translate_Action", Cur("Translate_Action"), "ActionHideTranslate")
        };

        if (!IsUnifiedPinHotkeyTag(targetTag))
        {
            crossActionCandidates.Add(("Snip_Pin", Cur("Snip_Pin"), "TipPin"));
            crossActionCandidates.Add(("Record_Action", Cur("Record_Action"), "ActionStartPin"));
            crossActionCandidates.Add(("Translate_Pin", Cur("Translate_Pin"), "MenuPinTranslation"));
        }

        return MatchCrossActionConflict(targetTag, hotkey, crossActionCandidates.ToArray());
    }
}
