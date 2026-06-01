using System;

namespace GimmeCapture.Views.Main;

/// <summary>
/// Translation mode: configurable modifier (Shift/Ctrl/Alt/None) for box selection + Win32 hit-test sync.
/// </summary>
public partial class SnipWindow
{
    /// <summary>
    /// After finishing a drag while the selection modifier is still held, keep ring hit-test until key-up.
    /// </summary>
    private bool _translationSuppressFullHitUntilSelectionModifierUp;

    private void ApplyTranslationSelectionModifierState(bool isKeyDown)
    {
        if (_viewModel == null || !_viewModel.IsTranslationMode)
        {
            return;
        }

        if (isKeyDown)
        {
            bool hasAnyBox = false;
            foreach (var selection in _viewModel.UserSelections)
            {
                if (!selection.IsAudioPanel && selection.Bounds.Width > 5 && selection.Bounds.Height > 5)
                {
                    hasAnyBox = true;
                    break;
                }
            }
            _viewModel.CurrentTranslationTool = hasAnyBox
                ? Models.TranslationTool.Multi
                : Models.TranslationTool.Single;
        }
        else
        {
            _translationSuppressFullHitUntilSelectionModifierUp = false;
            _viewModel.CurrentTranslationTool = Models.TranslationTool.Cursor;
        }

        RequestTranslationWindowRegionRefresh();
    }

    private static bool IsPhysicalModifierLabelDown(string? label)
    {
        if (string.IsNullOrEmpty(label)) return false;
        if (string.Equals(label, "Ctrl", StringComparison.OrdinalIgnoreCase))
            return (GetAsyncKeyState(0x11) & 0x8000) != 0;
        if (string.Equals(label, "Shift", StringComparison.OrdinalIgnoreCase))
            return (GetAsyncKeyState(0x10) & 0x8000) != 0;
        if (string.Equals(label, "Alt", StringComparison.OrdinalIgnoreCase))
            return (GetAsyncKeyState(0x12) & 0x8000) != 0;
        return false;
    }

    /// <summary> Left-drag to select: <c>None</c> = no modifier required. </summary>
    private bool IsTranslationSelectionModifierDownForPointer()
    {
        var m = _viewModel?.TranslationSelectionHoldModifier ?? "Ctrl";
        if (string.Equals(m, "None", StringComparison.OrdinalIgnoreCase))
            return true;
        return IsPhysicalModifierLabelDown(m);
    }

    /// <summary> Win32 region: <c>None</c> behaves like modifier always active (full-hit for selection). </summary>
    private bool IsTranslationSelectionModifierDownForRegion()
    {
        var m = _viewModel?.TranslationSelectionHoldModifier ?? "Ctrl";
        if (string.Equals(m, "None", StringComparison.OrdinalIgnoreCase))
            return true;
        return IsPhysicalModifierLabelDown(m);
    }
}
