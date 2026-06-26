using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.VisualTree;
using GimmeCapture.Models;
using GimmeCapture.Services.Core.Infrastructure;
using GimmeCapture.Services.Interop;
using GimmeCapture.ViewModels.Main;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Avalonia.Input;
using Avalonia.Input.Raw;

namespace GimmeCapture.Views.Main;

public partial class SnipWindow : Window
{
    // Win32 Interop for click-through
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr")]
    private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", EntryPoint = "SetWindowLong")]
    private static extern IntPtr SetWindowLongPtr32(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll")]
    private static extern IntPtr CallWindowProc(IntPtr lpPrevWndFunc, IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ScreenToClient(IntPtr hWnd, ref POINT lpPoint);

    private const int GWLP_WNDPROC = -4;
    private const uint WM_NCHITTEST = 0x0084;
    private const int HTTRANSPARENT = -1;

    private WndProcDelegate? _wndProcDelegate;
    private IntPtr _oldWndProc;

    private readonly List<Rect> _hitTestRegions = new();
    private bool _useHitTestRegions;

    /// <summary>
    /// Translation toolbar (language row + actions) is wide; the first layout pass can report a
    /// <see cref="SnipWindowViewModel.ToolbarWidth"/> that is still too small. Win32 region code uses
    /// that width for <c>SetWindowRgn</c>; underestimating clips painting on the right.
    /// </summary>
    private static double TranslationToolbarOpaqueWidthDip(double widthIncludingPadding)
    {
        const double minWidth = 1080.0;
        return Math.Max(widthIncludingPadding, minWidth);
    }

    // --- Low-Level Keyboard Hook ---
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_KEYUP = 0x0101;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int WM_SYSKEYUP = 0x0105;

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string lpModuleName);

    private IntPtr _llKeyboardHook = IntPtr.Zero;
    private LowLevelKeyboardProc? _llKeyboardDelegate;

    private void InstallLLKeyboardHook()
    {
        if (_llKeyboardHook != IntPtr.Zero) return;
        _llKeyboardDelegate = LLKeyboardHookCallback;
        using (var curProcess = System.Diagnostics.Process.GetCurrentProcess())
        using (var curModule = curProcess.MainModule)
        {
            if (curModule?.ModuleName != null)
            {
                _llKeyboardHook = SetWindowsHookEx(WH_KEYBOARD_LL, _llKeyboardDelegate, GetModuleHandle(curModule.ModuleName), 0);
            }
        }
    }

    private void UninstallLLKeyboardHook()
    {
        if (_llKeyboardHook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_llKeyboardHook);
            _llKeyboardHook = IntPtr.Zero;
            _llKeyboardDelegate = null;
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        UninstallLLKeyboardHook();
        TranslationResultLayerManager.RefreshWindowState();
    }

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKey(uint uCode, uint uMapType);
    private const uint MAPVK_VSC_TO_VK_EX = 3;

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsChild(IntPtr hWndParent, IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hwnd, uint gaFlags);

    private const uint GA_ROOT = 2;

    /// <summary>
    /// Low-level keyboard hook is process-global. Foreground ownership is still the default gate.
    /// A separate, narrower unfocused path is used for capture-flow shortcuts while the overlay is active.
    /// </summary>
    private bool ShouldRouteLowLevelHotkeysForForegroundWindow()
    {
        IntPtr mine = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (mine == IntPtr.Zero) return false;
        IntPtr fg = GetForegroundWindow();
        if (fg == IntPtr.Zero) return false;
        if (fg == mine) return true;
        if (IsChild(mine, fg)) return true;
        return GetAncestor(fg, GA_ROOT) == mine;
    }

    private IntPtr LLKeyboardHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && _viewModel != null)
        {
            int msg = wParam.ToInt32();
            int vkCode = Marshal.ReadInt32(lParam);
            
            // If IME is active, vkCode is VK_PROCESSKEY (0xE5 = 229).
            // We must read the scanCode to get the physical key.
            if (vkCode == 229)
            {
                int scanCode = Marshal.ReadInt32(lParam, 4);
                uint mappedVk = MapVirtualKey((uint)scanCode, MAPVK_VSC_TO_VK_EX);
                if (mappedVk != 0)
                {
                    vkCode = (int)mappedVk;
                }
            }
            
            string keyStr = GetKeyStringFromVirtualKey(vkCode);

            bool isKeyDown = (msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN);
            bool isKeyUp = (msg == WM_KEYUP || msg == WM_SYSKEYUP);

            if ((isKeyDown || isKeyUp) && keyStr != "Unknown")
            {
                // Route the parsed key through the low-level hotkey dispatcher.
                bool handled = HandleGlobalKeyboardEvent(keyStr, vkCode, isKeyDown);
                
                // If the key is a modifier (Shift/Ctrl/Alt), we NEVER swallow it globally to avoid breaking typing elsewhere.
                // If it matched one of our global capture handlers, we swallow it so it does not trigger apps underneath.
                if (handled && !IsPureModifierKey(keyStr))
                {
                    return new IntPtr(1); // Consume the key
                }
            }
        }
        return CallNextHookEx(_llKeyboardHook, nCode, wParam, lParam);
    }
    
    private bool IsPureModifierKey(string keyStr)
    {
        return keyStr == "Shift" || keyStr == "Ctrl" || keyStr == "Alt";
    }

    private string GetKeyStringFromVirtualKey(int vkCode)
    {
        if (vkCode >= 0x41 && vkCode <= 0x5A) return ((char)vkCode).ToString().ToUpper(); // A-Z
        if (vkCode >= 0x30 && vkCode <= 0x39) return ((char)vkCode).ToString(); // 0-9
        if (vkCode >= 0x70 && vkCode <= 0x87) return "F" + (vkCode - 0x70 + 1); // F1-F24
        
        switch (vkCode)
        {
            case 0x0D: return "Enter";
            case 0x1B: return "Escape";
            case 0x09: return "Tab";
            case 0x2E: return "Delete";
            case 0x20: return "Space";
            case 0x10: 
            case 0xA0: 
            case 0xA1: return "Shift";
            case 0x11:
            case 0xA2: 
            case 0xA3: return "Ctrl";
            case 0x12:
            case 0xA4: 
            case 0xA5: return "Alt";
        }
        return "Unknown";
    }

    private bool CanHandleUnfocusedCaptureHotkeys()
    {
        if (_viewModel == null) return false;
        if (_viewModel.IsTranslationMode || _viewModel.IsEnteringText || _viewModel.IsInputFocused || _viewModel.IsRecordingFinalizing)
            return false;

        if (_pointerState != PointerInteractionState.None)
            return false;

        return _viewModel.CurrentState == SnipState.Detecting
            || _viewModel.CurrentState == SnipState.Selecting
            || _viewModel.CurrentState == SnipState.Selected
            || _viewModel.RecState != RecordingState.Idle;
    }

    private static bool IsSafeUnfocusedCaptureHotkey(string hotkey)
    {
        return !string.IsNullOrWhiteSpace(hotkey)
            && HotkeyParsingHelper.IsSafeSingleKeyHotkey(hotkey.AsSpan());
    }

    // A modifier combo (e.g. "Ctrl+C") carries a '+'. Used to allow Copy/Save globally on the unfocused
    // capture path without risking a single-letter binding swallowing the user's plain typing.
    private static bool IsModifierCombo(string? hotkey)
    {
        return !string.IsNullOrWhiteSpace(hotkey) && hotkey.IndexOf('+') >= 0;
    }

    private static bool MatchesUnfocusedCaptureHotkey(string hotkey, Func<string, bool> isMatch)
    {
        return !string.IsNullOrWhiteSpace(hotkey)
            && IsSafeUnfocusedCaptureHotkey(hotkey)
            && isMatch(hotkey);
    }

    private void PostHandleDismissOrCloseHotkey()
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            var vm = _viewModel;
            if (vm == null) return;

            // During manual scrolling capture, Esc must cancel the session — not reset the
            // (hidden) selection — whichever path delivered the key.
            if (vm.IsManualScrollActive)
            {
                vm.FinishManualScrollCapture(cancelled: true);
                return;
            }

            if (vm.IsEnteringText)
            {
                vm.CancelTextEntryCommand.Execute(System.Reactive.Unit.Default).Subscribe();
                return;
            }

            if (vm.RecState != RecordingState.Idle)
            {
                return;
            }

            if (vm.IsTranslationMode)
            {
                Close();
                return;
            }

            if (vm.IsDrawingMode)
            {
                vm.IsDrawingMode = false;
                return;
            }

            if (vm.CurrentState == SnipState.Selecting || vm.CurrentState == SnipState.Selected)
            {
                vm.CurrentState = SnipState.Detecting;
                vm.SelectionRect = new Rect(0, 0, 0, 0);
                return;
            }

            Close();
        }, Avalonia.Threading.DispatcherPriority.Input);
    }

    private bool TryHandleUnfocusedCaptureHotkey(Func<string, bool> isMatch)
    {
        if (_viewModel == null) return false;

        if (MatchesUnfocusedCaptureHotkey(_viewModel.CloseHotkey, isMatch))
        {
            PostHandleDismissOrCloseHotkey();
            return true;
        }

        if (MatchesUnfocusedCaptureHotkey(_viewModel.ActiveToolbarHotkey, isMatch))
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(
                () => _viewModel?.ToggleToolbarCommand?.Execute().Subscribe(),
                Avalonia.Threading.DispatcherPriority.Input);
            return true;
        }

        if (MatchesUnfocusedCaptureHotkey(_viewModel.ActiveActionHotkey, isMatch))
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(
                () => _viewModel?.HandleActiveActionHotkeyCommand?.Execute().Subscribe(),
                Avalonia.Threading.DispatcherPriority.Input);
            return true;
        }

        if (MatchesUnfocusedCaptureHotkey(_viewModel.FullscreenSelectHotkey, isMatch))
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(
                () => _viewModel?.SelectFullscreenCommand?.Execute().Subscribe(),
                Avalonia.Threading.DispatcherPriority.Input);
            return true;
        }

        if (MatchesUnfocusedCaptureHotkey(_viewModel.SwitchToSnipHotkey, isMatch))
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(
                () => _viewModel?.SwitchToSnipCommand?.Execute().Subscribe(),
                Avalonia.Threading.DispatcherPriority.Input);
            return true;
        }

        if (MatchesUnfocusedCaptureHotkey(_viewModel.SwitchToRecordHotkey, isMatch))
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(
                () => _viewModel?.SwitchToRecordCommand?.Execute().Subscribe(),
                Avalonia.Threading.DispatcherPriority.Input);
            return true;
        }

        if (MatchesUnfocusedCaptureHotkey(_viewModel.SwitchToTranslateHotkey, isMatch))
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(
                () => _viewModel?.SwitchToTranslateCommand?.Execute().Subscribe(),
                Avalonia.Threading.DispatcherPriority.Input);
            return true;
        }

        return false;
    }

    private bool HandleGlobalKeyboardEvent(string keyStr, int vkCode, bool isKeyDown)
    {
        if (_viewModel == null) return false;

        bool ownsForeground = ShouldRouteLowLevelHotkeysForForegroundWindow();
        bool isCtrlModifierEvent = IsPureModifierKey(keyStr) && string.Equals(keyStr, "Ctrl", StringComparison.OrdinalIgnoreCase);
        bool allowGlobalCtrlSelectionModifier =
            _viewModel.IsTranslationMode &&
            isCtrlModifierEvent &&
            string.Equals(_viewModel.TranslationSelectionHoldModifier, "Ctrl", StringComparison.OrdinalIgnoreCase);
        bool allowUnfocusedCaptureHotkeys =
            !_viewModel.IsTranslationMode &&
            !ownsForeground &&
            CanHandleUnfocusedCaptureHotkeys();

        if (!allowGlobalCtrlSelectionModifier && !ownsForeground && !allowUnfocusedCaptureHotkeys)
        {
            return false;
        }

        // Prevent duplicate handling when the Snip window already has foreground focus.
        // Screenshot/recording mode only uses the LL hook for the narrow unfocused capture-flow path.
        if (!_viewModel.IsTranslationMode && !allowUnfocusedCaptureHotkeys)
        {
            return false;
        }

        bool shiftDown = (GetAsyncKeyState(0x10) & 0x8000) != 0;
        bool ctrlDown = (GetAsyncKeyState(0x11) & 0x8000) != 0;
        bool altDown = (GetAsyncKeyState(0x12) & 0x8000) != 0;

        // Manual scrolling capture owns the keyboard and must be handled BEFORE the generic
        // Escape/dismiss handler below — otherwise Esc would dismiss the (hidden) overlay
        // instead of cancelling the scroll session. The Pin key (F6) finishes, Esc cancels,
        // and every other key passes through so the user can keep scrolling.
        if (_viewModel.IsManualScrollActive)
        {
            if (isKeyDown && IsMatch(_viewModel.ActiveActionHotkey))
            {
                _viewModel.FinishManualScrollCapture(cancelled: false);
                return true;
            }

            if (isKeyDown && (IsMatch(_viewModel.CloseHotkey)
                || string.Equals(keyStr, "Escape", StringComparison.OrdinalIgnoreCase)))
            {
                _viewModel.FinishManualScrollCapture(cancelled: true);
                return true;
            }

            return false;
        }

        // Escape is handled even when a text control is focused (cancel entry / dismiss selection / close).
        if (isKeyDown && string.Equals(keyStr, "Escape", StringComparison.OrdinalIgnoreCase))
        {
            PostHandleDismissOrCloseHotkey();
            return true;
        }

        // Translation mode modifier hook:
        // keep this BEFORE input-focus gating so Ctrl-hold can still toggle selection behavior globally.
        if (IsPureModifierKey(keyStr))
        {
            var selMod = _viewModel?.TranslationSelectionHoldModifier ?? "Ctrl";
            bool allowWithoutFocus = _viewModel?.IsTranslationMode == true
                && string.Equals(keyStr, "Ctrl", StringComparison.OrdinalIgnoreCase);

            // When another control (textbox/combobox/IME) is focused, only the Ctrl-hold exception is allowed.
            if (_viewModel?.IsInputFocused == true && !allowWithoutFocus)
            {
                return false;
            }

            if (!string.Equals(selMod, "None", StringComparison.OrdinalIgnoreCase) &&
                _viewModel?.IsTranslationMode == true &&
                string.Equals(keyStr, selMod, StringComparison.OrdinalIgnoreCase))
            {
                void ApplyModifierState()
                {
                    ApplyTranslationSelectionModifierState(isKeyDown);
                }

                if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
                {
                    ApplyModifierState();
                }
                else
                {
                    Avalonia.Threading.Dispatcher.UIThread.Post(
                        ApplyModifierState,
                        Avalonia.Threading.DispatcherPriority.Send);
                }
                return true;
            }

            return false;
        }

        // Let other keys through to SelectableTextBlock/TextBox/ComboBox (translation results, toolbars, IME, etc.).
        if (_viewModel.IsInputFocused)
        {
            return false;
        }

        // Only process action hotkeys on KeyDown
        if (!isKeyDown) return false;

        // --- Do NOT process other hotkeys if the user is typing in the Text annotation tool ---
        if (_viewModel.IsEnteringText)
        {
            return false;
        }

        bool IsMatch(string hotkey)
        {
            if (string.IsNullOrEmpty(hotkey)) return false;

            ReadOnlySpan<char> hotkeySpan = hotkey.AsSpan().Trim();
            ReadOnlySpan<char> keyPart = HotkeyParsingHelper.GetKeyPart(hotkeySpan);

            if (hotkeySpan.IndexOf('+') < 0)
            {
                return !ctrlDown
                    && !altDown
                    && !shiftDown
                    && keyPart.Equals(keyStr.AsSpan(), StringComparison.OrdinalIgnoreCase);
            }

            if (HotkeyParsingHelper.ModifiersMatch(hotkeySpan, ctrlDown, altDown, shiftDown)
                && keyPart.Equals(keyStr.AsSpan(), StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return false;
        }

        if (!_viewModel.IsTranslationMode)
        {
            if (TryHandleUnfocusedCaptureHotkey(IsMatch))
            {
                return true;
            }

            // Pause/stop must still work globally while recording (e.g. F9/Space from another app).
            if (_viewModel.CurrentMode == SnipMode.Recording && _viewModel.RecState != RecordingState.Idle)
            {
                if (IsMatch(_viewModel.ActivePlaybackHotkey))
                {
                    _viewModel.PauseRecordingCommand?.Execute().Subscribe();
                    return true;
                }

                if (IsMatch(_viewModel.ActiveStopHotkey))
                {
                    _viewModel.StopRecordingCommand?.Execute().Subscribe();
                    return true;
                }
            }

            // Copy/Save stay available globally (even when the overlay isn't focused) while there is a
            // capture to act on, so the user can grab/save it without clicking into the overlay first. These
            // are restricted to modifier combos (e.g. Ctrl+C / Ctrl+S) so — unlike the single-letter drawing
            // hotkeys — they can never swallow plain typing in another app.
            bool hasCaptureContent =
                _viewModel.CurrentState == SnipState.Selected
                || _viewModel.IsDrawingMode
                || _viewModel.RecState != RecordingState.Idle;

            if (hasCaptureContent)
            {
                if (IsModifierCombo(_viewModel.CopyHotkey) && IsMatch(_viewModel.CopyHotkey))
                {
                    _viewModel.CopyCommand?.Execute().Subscribe();
                    return true;
                }

                if (IsModifierCombo(_viewModel.SaveHotkey) && IsMatch(_viewModel.SaveHotkey))
                {
                    _viewModel.SaveCommand?.Execute().Subscribe();
                    return true;
                }
            }

            // Past here the LL hook is only ever reached on the UNFOCUSED path in Snip/Record mode (when the
            // overlay owns focus, HandleGlobalKeyboardEvent has already returned above and Avalonia's window
            // KeyBindings handle keys). So beyond the narrow capture-flow + Copy/Save hotkeys above, the overlay
            // must stay completely hands-off and must NOT swallow the user's typing in another app — in
            // particular the single-letter drawing-tool hotkeys (R/E/A/L/P/T/M/B) and Clear, which used to be
            // eaten here while recording. Undo/Redo stay available while the overlay is focused (window
            // KeyBindings); annotation/editing lives in the pin windows, which have their own hotkeys.
            return false;
        }

        // 1. General Window Hotkeys
        var winAction = _hotkeyRouter.ResolveWindowHotkeyAction(
            _viewModel.ActiveActionHotkey,
            _viewModel.ActiveToolbarHotkey,
            _viewModel.SaveHotkey,
            _viewModel.CopyHotkey,
            IsMatch);

        switch (winAction)
        {
            case HotkeyRouterService.WindowHotkeyAction.ActiveAction:
                _viewModel.HandleActiveActionHotkeyCommand?.Execute().Subscribe();
                return true;
            case HotkeyRouterService.WindowHotkeyAction.ToggleToolbar:
                _viewModel.ToggleToolbarCommand?.Execute().Subscribe();
                return true;
            case HotkeyRouterService.WindowHotkeyAction.Save:
                _viewModel.SaveCommand?.Execute().Subscribe();
                return true;
            case HotkeyRouterService.WindowHotkeyAction.Copy:
                _viewModel.CopyCommand?.Execute().Subscribe();
                return true;
        }

        // 2. Common Action Hotkeys (Snip/Record/Translate)
        if (IsMatch(_viewModel.UndoHotkey)) { _viewModel.UndoCommand?.Execute().Subscribe(); return true; }
        if (IsMatch(_viewModel.RedoHotkey)) { _viewModel.RedoCommand?.Execute().Subscribe(); return true; }
        if (IsMatch(_viewModel.ClearHotkey)) { _viewModel.ClearAnnotationsCommand?.Execute().Subscribe(); return true; }

        // 3. Drawing Tools
        if (IsMatch(_viewModel.RectangleHotkey)) { _viewModel.SelectToolCommand?.Execute(Models.AnnotationType.Rectangle).Subscribe(); return true; }
        if (IsMatch(_viewModel.EllipseHotkey)) { _viewModel.SelectToolCommand?.Execute(Models.AnnotationType.Ellipse).Subscribe(); return true; }
        if (IsMatch(_viewModel.ArrowHotkey)) { _viewModel.SelectToolCommand?.Execute(Models.AnnotationType.Arrow).Subscribe(); return true; }
        if (IsMatch(_viewModel.LineHotkey)) { _viewModel.SelectToolCommand?.Execute(Models.AnnotationType.Line).Subscribe(); return true; }
        if (IsMatch(_viewModel.MosaicHotkey)) { _viewModel.SelectToolCommand?.Execute(Models.AnnotationType.Mosaic).Subscribe(); return true; }
        if (IsMatch(_viewModel.BlurHotkey)) { _viewModel.SelectToolCommand?.Execute(Models.AnnotationType.Blur).Subscribe(); return true; }
        if (IsMatch(_viewModel.PenHotkey)) { _viewModel.ToggleToolGroupCommand?.Execute("Pen").Subscribe(); return true; }
        if (IsMatch(_viewModel.TextHotkey)) { _viewModel.ToggleToolGroupCommand?.Execute("Text").Subscribe(); return true; }

        // 4. Mode Switching
        if (IsMatch(_viewModel.SwitchToSnipHotkey)) { _viewModel.SwitchToSnipCommand?.Execute().Subscribe(); return true; }
        if (IsMatch(_viewModel.SwitchToRecordHotkey)) { _viewModel.SwitchToRecordCommand?.Execute().Subscribe(); return true; }
        if (IsMatch(_viewModel.SwitchToTranslateHotkey)) { _viewModel.SwitchToTranslateCommand?.Execute().Subscribe(); return true; }

        // 5. Snip/Recording Specific Hotkeys
        if (_viewModel.CurrentMode == SnipMode.Recording && IsMatch(_viewModel.ActivePlaybackHotkey))
        {
            _viewModel.PauseRecordingCommand?.Execute().Subscribe();
            return true;
        }

        // Stop hotkey ends the active recording from anywhere (global LL-hook path, like pause above).
        if (_viewModel.CurrentMode == SnipMode.Recording && _viewModel.RecState != RecordingState.Idle
            && IsMatch(_viewModel.ActiveStopHotkey))
        {
            _viewModel.StopRecordingCommand?.Execute().Subscribe();
            return true;
        }

        if ((_viewModel.CurrentMode == SnipMode.Screenshot || _viewModel.CurrentMode == SnipMode.Recording) &&
            IsMatch(_viewModel.FullscreenSelectHotkey))
        {
            _viewModel.SelectFullscreenCommand?.Execute().Subscribe();
            return true;
        }

        if (_viewModel.CurrentMode == SnipMode.Screenshot)
        {
            // Note: SnipSelectionModeHotkey and SnipCropModeHotkey do not actually exist in the ViewModel.
            // If they are added in the future, they should be mapped here.
        }

        // 2. Translation Mode Specific Hotkeys
        if (_viewModel.IsTranslationMode)
        {
            var specificAction = _hotkeyRouter.ResolveSpecificTranslationAction(
                _viewModel.TranslateAllHotkey,
                IsMatch);

            if (specificAction == HotkeyRouterService.WindowHotkeyAction.TranslateAll)
            {
                _viewModel.TranslateAllSelectionsCommand?.Execute().Subscribe();
                return true;
            }

            if (IsMatch(_viewModel.TranslatePinHotkey))
            {
                _viewModel.PinTranslationResultsCommand?.Execute().Subscribe();
                return true;
            }

            if (IsMatch(_viewModel.ScanAllHotkey))
            {
                _viewModel.ScanAllTextCommand?.Execute().Subscribe();
                return true;
            }

            if (IsMatch(_viewModel.ClearAllHotkey))
            {
                _viewModel.ClearAllSelectionsCommand?.Execute().Subscribe();
                return true;
            }
        }

        return false;
    }


}
