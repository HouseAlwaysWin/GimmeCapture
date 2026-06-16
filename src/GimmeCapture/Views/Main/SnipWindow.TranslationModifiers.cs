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
    private bool _translationSelectionModifierHeld;

    private void ApplyTranslationSelectionModifierState(bool isKeyDown)
    {
        if (_viewModel == null || !_viewModel.IsTranslationMode)
        {
            ResetTranslationSelectionModifierState();
            return;
        }

        // Avalonia and the low-level keyboard hook can both report the same transition.
        // Key-repeat also produces additional key-down events on some systems.
        if (_translationSelectionModifierHeld == isKeyDown)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[TranslationOCR] Ignored duplicate selection modifier state: down={isKeyDown}");
            return;
        }

        _translationSelectionModifierHeld = isKeyDown;
        System.Diagnostics.Debug.WriteLine(
            $"[TranslationOCR] Selection modifier transition: down={isKeyDown}");

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
            _viewModel.EnterTranslationOcrSearchAsync().Forget("Translation.EnterOcrSearch");
        }
        else
        {
            _translationSuppressFullHitUntilSelectionModifierUp = false;
            _viewModel.CurrentTranslationTool = Models.TranslationTool.Cursor;
            _viewModel.ExitTranslationOcrSearch();
        }

        RequestTranslationWindowRegionRefresh();
    }

    private void ResetTranslationSelectionModifierState()
    {
        _translationSelectionModifierHeld = false;
        _translationSuppressFullHitUntilSelectionModifierUp = false;
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

    internal bool IsTranslationSelectionModifierKeyEvent(Avalonia.Input.KeyEventArgs e)
    {
        var m = _viewModel?.TranslationSelectionHoldModifier ?? "Ctrl";
        if (string.Equals(m, "None", StringComparison.OrdinalIgnoreCase))
            return false;

        return string.Equals(m, "Ctrl", StringComparison.OrdinalIgnoreCase)
            ? e.Key is Avalonia.Input.Key.LeftCtrl or Avalonia.Input.Key.RightCtrl
                || string.Equals(e.PhysicalKey.ToString(), "ControlLeft", StringComparison.OrdinalIgnoreCase)
                || string.Equals(e.PhysicalKey.ToString(), "ControlRight", StringComparison.OrdinalIgnoreCase)
            : string.Equals(m, "Shift", StringComparison.OrdinalIgnoreCase)
                ? e.Key is Avalonia.Input.Key.LeftShift or Avalonia.Input.Key.RightShift
                    || string.Equals(e.PhysicalKey.ToString(), "ShiftLeft", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(e.PhysicalKey.ToString(), "ShiftRight", StringComparison.OrdinalIgnoreCase)
                : string.Equals(m, "Alt", StringComparison.OrdinalIgnoreCase)
                    && (e.Key is Avalonia.Input.Key.LeftAlt or Avalonia.Input.Key.RightAlt
                        || string.Equals(e.PhysicalKey.ToString(), "AltLeft", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(e.PhysicalKey.ToString(), "AltRight", StringComparison.OrdinalIgnoreCase));
    }
}
