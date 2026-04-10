using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using GimmeCapture.Models;
using GimmeCapture.Services.Interop;
using GimmeCapture.ViewModels.Main;
using System;
using System.Collections.Generic;
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
    }

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKey(uint uCode, uint uMapType);
    private const uint MAPVK_VSC_TO_VK_EX = 3;

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
                // We route the parsed key directly to the translation mode logic.
                bool handled = HandleGlobalKeyboardEvent(keyStr, vkCode, isKeyDown);
                
                // If the key is a modifier (Shift/Ctrl/Alt), we NEVER swallow it globally to avoid breaking typing elsewhere.
                // If it is an action hotkey matching our translation mode, we SWALLOW it (return 1) so it doesn't trigger in apps below.
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

    private bool HandleGlobalKeyboardEvent(string keyStr, int vkCode, bool isKeyDown)
    {
        if (_viewModel == null) return false;

        bool shiftDown = (GetAsyncKeyState(0x10) & 0x8000) != 0;
        bool ctrlDown = (GetAsyncKeyState(0x11) & 0x8000) != 0;
        bool altDown = (GetAsyncKeyState(0x12) & 0x8000) != 0;

        // Translation mode: one configurable hold-modifier for selection (Shift/Ctrl/Alt; None = no key hook).
        if (IsPureModifierKey(keyStr))
        {
            var selMod = _viewModel?.TranslationSelectionHoldModifier ?? "Ctrl";
            if (!string.Equals(selMod, "None", StringComparison.OrdinalIgnoreCase) &&
                _viewModel?.IsTranslationMode == true &&
                string.Equals(keyStr, selMod, StringComparison.OrdinalIgnoreCase))
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    if (_viewModel == null) return;
                    if (isKeyDown)
                        _viewModel.CurrentTranslationTool = Models.TranslationTool.Single;
                    else
                    {
                        _translationSuppressFullHitUntilSelectionModifierUp = false;
                        _viewModel.CurrentTranslationTool = Models.TranslationTool.Cursor;
                    }
                    RequestTranslationWindowRegionRefresh();
                }, Avalonia.Threading.DispatcherPriority.Input);
                return true;
            }

            return false;
        }

        // Only process action hotkeys on KeyDown
        if (!isKeyDown) return false;

        // --- Handle Global Escape Key explicitly ---
        if (string.Equals(keyStr, "Escape", StringComparison.OrdinalIgnoreCase))
        {
            // Replicate OnKeyDown Escape logic so users can cancel screenshots/records when unfocused
            if (_viewModel.IsEnteringText)
            {
                _viewModel.CancelTextEntryCommand.Execute(System.Reactive.Unit.Default).Subscribe();
                return true;
            }

            if (_viewModel.RecState != RecordingState.Idle)
            {
                return true; // Ignore escape gracefully while recording
            }

            if (_viewModel.IsDrawingMode)
            {
                _viewModel.IsDrawingMode = false;
                return true;
            }
            else if (_viewModel.CurrentState == SnipState.Selecting || 
                     _viewModel.CurrentState == SnipState.Selected)
            {
                _viewModel.CurrentState = SnipState.Detecting;
                _viewModel.SelectionRect = new Rect(0,0,0,0);
                return true;
            }
            else
            {
                // We shouldn't directly close the window from a background thread / hook callback without dispatcher context usually,
                // but Avalonia mostly allows Dispatcher.UIThread.Post for safety.
                Avalonia.Threading.Dispatcher.UIThread.Post(() => this.Close());
                return true;
            }
        }

        // --- Do NOT process other hotkeys if the user is typing in the Text annotation tool ---
        if (_viewModel.IsEnteringText)
        {
            return false;
        }

        // Let native text selection copy handle Ctrl+C in translation text blocks.
        if (_viewModel.IsInputFocused && ctrlDown && string.Equals(keyStr, "C", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string activeKeysStr = "";
        if (ctrlDown) activeKeysStr += "Ctrl+";
        if (altDown) activeKeysStr += "Alt+";
        if (shiftDown) activeKeysStr += "Shift+";
        activeKeysStr += keyStr;

        bool IsMatch(string hotkey)
        {
            if (string.IsNullOrEmpty(hotkey)) return false;
            
            string mappedExpected = hotkey;
            if (hotkey.StartsWith("D") && hotkey.Length == 2 && char.IsDigit(hotkey[1]))
            {
                mappedExpected = hotkey.Substring(1);
            }
            if (string.Equals(mappedExpected, "Return", StringComparison.OrdinalIgnoreCase))
            {
                mappedExpected = "Enter";
            }
            
            if (string.Equals(activeKeysStr, mappedExpected, StringComparison.OrdinalIgnoreCase)) return true;
            
            if (hotkey.Contains("+"))
            {
                bool hkCtrl = hotkey.IndexOf("Ctrl", StringComparison.OrdinalIgnoreCase) >= 0;
                bool hkAlt = hotkey.IndexOf("Alt", StringComparison.OrdinalIgnoreCase) >= 0;
                bool hkShift = hotkey.IndexOf("Shift", StringComparison.OrdinalIgnoreCase) >= 0;
                
                var parts = hotkey.Split('+');
                string baseKey = parts[parts.Length - 1].Trim();
                if (baseKey.StartsWith("D") && baseKey.Length == 2 && char.IsDigit(baseKey[1])) baseKey = baseKey.Substring(1);
                if (string.Equals(baseKey, "Return", StringComparison.OrdinalIgnoreCase)) baseKey = "Enter";
                
                if (hkCtrl == ctrlDown && hkAlt == altDown && hkShift == shiftDown && string.Equals(baseKey, keyStr, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            else
            {
                if (!ctrlDown && !altDown && !shiftDown && string.Equals(mappedExpected, keyStr, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

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

            if (IsMatch(_viewModel.ScanAllHotkey))
            {
                _viewModel.ScanAllTextCommand?.Execute().Subscribe();
                return true;
            }

            if (IsMatch(_viewModel.AutoDetectHotkey))
            {
                _viewModel.ToggleAutoDetectCommand?.Execute().Subscribe();
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

    public void InitializeWin32Hook()
    {
        if (!OperatingSystem.IsWindows()) return;
        var hwnd = this.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (hwnd == IntPtr.Zero || _wndProcDelegate != null) return;

        _wndProcDelegate = new WndProcDelegate(WndProcHook);
        IntPtr ptr = Marshal.GetFunctionPointerForDelegate(_wndProcDelegate);

        if (IntPtr.Size == 8)
            _oldWndProc = SetWindowLongPtr64(hwnd, GWLP_WNDPROC, ptr);
        else
            _oldWndProc = SetWindowLongPtr32(hwnd, GWLP_WNDPROC, ptr);

        // Also install the LL Keyboard hook for translation global hotkeys
        InstallLLKeyboardHook();
    }

    private IntPtr WndProcHook(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        bool allowHitTestTransparent = (_viewModel?.IsTranslationMode ?? false) || !(_viewModel?.IsDrawingMode ?? false);
        if (msg == WM_NCHITTEST && _useHitTestRegions && allowHitTestTransparent)
        {
            // Signed screen coords (multi-monitor). Must match SetWindowRgn rects (physical client pixels).
            int lp = lParam.ToInt32();
            var pt = new POINT { X = (short)(lp & 0xFFFF), Y = (short)((lp >> 16) & 0xFFFF) };
            ScreenToClient(hWnd, ref pt);
            var point = new Point(pt.X, pt.Y);

            bool hit = false;
            foreach (var r in _hitTestRegions)
            {
                if (r.Contains(point))
                {
                    hit = true;
                    break;
                }
            }

            if (!hit)
            {
                return new IntPtr(HTTRANSPARENT);
            }
        }

        return CallWindowProc(_oldWndProc, hWnd, msg, wParam, lParam);
    }

    /// <summary>
    /// Same as screenshot/recording when not using a full selection region: punch a 1×1 px hole so
    /// Chromium/DWM does not treat SnipWindow as a full-screen occluder (YouTube/hardware video).
    /// Optionally merges the toolbar so it stays hit-testable.
    /// </summary>
    /// <remarks>
    /// When the toolbar is hidden: if Ctrl is <b>not</b> held, use disjoint islands only (video-friendly).
    /// If Ctrl <b>is</b> held, we must use the nearly full-client region — otherwise <c>SetWindowRgn</c> leaves
    /// almost no hit-testable area and Ctrl-drag selection is delivered to windows below. While Ctrl is held,
    /// hardware video may be affected; release Ctrl to restore disjoint mode.
    /// </remarks>
    private void ApplyTranslationDwmMinimalOccluderFix(IntPtr hwnd, double scaling, int windowWidth, int windowHeight)
    {
        if (_viewModel != null && !_viewModel.IsToolbarShownOnScreen)
        {
            bool selModHeld = IsTranslationSelectionModifierDownForRegion();
            if (selModHeld && !_translationSuppressFullHitUntilSelectionModifierUp)
            {
                Win32Helpers.SetMultiWindowHoleRegion(hwnd, windowWidth, windowHeight, new[] { new Rect(0, 0, 1, 1) }, 0, null, null);
                _hitTestRegions.Clear();
                _useHitTestRegions = false;
                return;
            }

            ApplyTranslationPassThroughExceptToolbarAndLoadingBar(hwnd, scaling);
            return;
        }

        Rect? toolbarRect = null;
        if (_viewModel != null && _viewModel.IsToolbarVisible && _viewModel.ToolbarWidth > 0 && _viewModel.ToolbarHeight > 0)
        {
            double tw = TranslationToolbarOpaqueWidthDip(_viewModel.ToolbarWidth + 40);
            double th = _viewModel.ToolbarHeight + 40;
            double tx = _viewModel.ToolbarLeft - 20;
            double ty = _viewModel.ToolbarTop - 20;
            toolbarRect = new Rect(tx * scaling, ty * scaling, tw * scaling, th * scaling);
        }

        Win32Helpers.SetMultiWindowHoleRegion(hwnd, windowWidth, windowHeight, new[] { new Rect(0, 0, 1, 1) }, 0, toolbarRect, null);
    }

    /// <summary>
    /// Translation idle (no untranslated rings yet): only toolbar + top loading strip own hit-testing;
    /// the rest of the overlay passes clicks to windows below so the user can interact with the desktop.
    /// When <see cref="_viewModel"/> has no toolbar, uses a 1×1 px region as a minimal DWM-safe stub.
    /// </summary>
    private void ApplyTranslationPassThroughExceptToolbarAndLoadingBar(IntPtr hwnd, double scaling)
    {
        var opaque = new System.Collections.Generic.List<Rect>();
        if (_viewModel != null && _viewModel.IsToolbarVisible)
        {
            // Keep the toolbar hit area tight to avoid blocking interaction on its right side.
            if (_viewModel.ToolbarWidth > 1 && _viewModel.ToolbarHeight > 1)
            {
                const double pad = 8;
                double tw = TranslationToolbarOpaqueWidthDip(_viewModel.ToolbarWidth + (pad * 2));
                double th = _viewModel.ToolbarHeight + (pad * 2);
                double tx = _viewModel.ToolbarLeft - pad;
                double ty = _viewModel.ToolbarTop - pad;
                opaque.Add(new Rect(tx * scaling, ty * scaling, tw * scaling, th * scaling));
            }
        }

        if (_viewModel != null && _viewModel.ShowTopLoadingBar)
        {
            foreach (var screen in _viewModel.AllScreenBounds)
            {
                opaque.Add(new Rect(screen.X * scaling, screen.Y * scaling, screen.W * scaling, 8 * scaling));
            }
        }

        // Keep a 1x1 region to avoid full-screen occluder heuristics while still allowing pass-through.
        opaque.Add(new Rect(0, 0, 1, 1));
        Win32Helpers.SetDisjointOpaqueRegions(hwnd, opaque, null);
        _hitTestRegions.Clear();
        _useHitTestRegions = false;
    }

    /// <summary>
    /// Opaque UI islands for translation general (cursor) mode — same geometry as former WM_NCHITTEST list, for SetWindowRgn.
    /// </summary>
    private void CollectTranslationGeneralModeOpaqueRects(double scaling, System.Collections.Generic.List<Rect> dest)
    {
        if (_viewModel == null) return;

        if (_viewModel.IsToolbarVisible && _viewModel.ToolbarWidth > 0 && _viewModel.ToolbarHeight > 0)
        {
            double tw = TranslationToolbarOpaqueWidthDip(_viewModel.ToolbarWidth + 40);
            double th = _viewModel.ToolbarHeight + 40;
            double tx = _viewModel.ToolbarLeft - 20;
            double ty = _viewModel.ToolbarTop - 20;
            dest.Add(new Rect(tx * scaling, ty * scaling, tw * scaling, th * scaling));
        }

        foreach (var sel in _viewModel.UserSelections)
        {
            if (sel.Bounds.Width <= 10 || sel.Bounds.Height <= 10) continue;
            var rect = sel.Bounds;

            if (sel.IsTranslated)
            {
                if (sel.IsAudioPanel)
                {
                    dest.Add(new Rect(
                        rect.X * scaling,
                        rect.Y * scaling,
                        rect.Width * scaling,
                        rect.Height * scaling));
                }
                else
                {
                    dest.Add(new Rect(
                        rect.X * scaling,
                        rect.Y * scaling,
                        rect.Width * scaling,
                        (rect.Height + sel.EstimatedTextHeight + 20) * scaling));
                }
            }
            else
            {
                dest.Add(new Rect(
                    rect.X * scaling,
                    rect.Y * scaling,
                    rect.Width * scaling,
                    rect.Height * scaling));
            }
        }

        if (_viewModel.ShowTopLoadingBar)
        {
            foreach (var screen in _viewModel.AllScreenBounds)
            {
                dest.Add(new Rect(
                    screen.X * scaling,
                    screen.Y * scaling,
                    screen.W * scaling,
                    8 * scaling));
            }
        }
    }

    /// <summary>
    /// Same outer ring + inner pass-through hole as <see cref="SnipState.Selected"/> / screenshot (physical client pixels).
    /// </summary>
    private (Rect Outer, Rect InnerHole) ComputeScreenshotStyleRingPhysicalRects(Rect selectionBoundsLogical, double scaling)
    {
        var scaledRect = new Rect(
            selectionBoundsLogical.X * scaling,
            selectionBoundsLogical.Y * scaling,
            selectionBoundsLogical.Width * scaling,
            selectionBoundsLogical.Height * scaling);

        double maxMargin = 0;
        if (_viewModel != null)
        {
            double hSize = 40 * scaling;
            double sThick = 15 * scaling;
            double wW = _viewModel.WingWidth * scaling;
            double wH = _viewModel.WingHeight * scaling;
            double iSize = (_viewModel.SelectionIconSize + 8) * scaling;

            maxMargin = Math.Max(hSize / 2, Math.Max(sThick / 2, Math.Max(wW, iSize)));

            double verticalOverflow = (wH / 2) - (scaledRect.Height / 2);
            if (verticalOverflow > maxMargin)
            {
                maxMargin = verticalOverflow + (10 * scaling);
            }
        }
        else
        {
            maxMargin = 20 * scaling;
        }

        maxMargin += 20 * scaling;

        var outerBox = new Rect(
            scaledRect.X - maxMargin,
            scaledRect.Y - maxMargin,
            scaledRect.Width + maxMargin * 2,
            scaledRect.Height + maxMargin * 2);

        double innerShrink = 0;
        if (_viewModel != null)
        {
            double innerIconWidth = (_viewModel.SelectionIconSize + 4) * scaling;
            double borderThick = _viewModel.SelectionBorderThickness * scaling;
            innerShrink = borderThick + innerIconWidth + (12 * scaling);
            innerShrink = Math.Max(15 * scaling, innerShrink);
        }

        double maxAllowedShrink = Math.Min(scaledRect.Width, scaledRect.Height) / 2.0 - 1;
        if (maxAllowedShrink < 0) maxAllowedShrink = 0;
        innerShrink = Math.Min(innerShrink, maxAllowedShrink);

        var innerHole = new Rect(
            scaledRect.X + innerShrink,
            scaledRect.Y + innerShrink,
            Math.Max(0, scaledRect.Width - innerShrink * 2),
            Math.Max(0, scaledRect.Height - innerShrink * 2));

        return (outerBox, innerHole);
    }

    private void RequestTranslationWindowRegionRefresh()
    {
        if (_viewModel == null || !OperatingSystem.IsWindows()) return;
        UpdateWindowRegion(_viewModel.SelectionRect, _viewModel.CurrentState, _viewModel.IsDrawingMode);
    }

    /// <summary>
    /// Updates the window region to create a "hole" in the selection area for mouse pass-through.
    /// This allows clicking on underlying windows (like YouTube) while keeping the border UI interactive.
    /// The hole is disabled when in drawing mode to allow annotations.
    /// </summary>
    private void UpdateWindowRegion(Rect selectionRect, SnipState state, bool isDrawingMode)
    {
        if (!OperatingSystem.IsWindows()) return;
        
        var hwnd = this.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (hwnd == IntPtr.Zero) return;

        // Reset WM_NCHITTEST pass-through islands every refresh to avoid leaking translation-only
        // hit-test state into screenshot/recording region logic.
        _useHitTestRegions = false;
        _hitTestRegions.Clear();

        bool isTranslation = _viewModel?.IsTranslationMode ?? false;

        if (!isDrawingMode && (isTranslation || (state == SnipState.Selected && selectionRect.Width > 10 && selectionRect.Height > 10)))
        {
            double scaling = this.RenderScaling;
            int windowWidth = (int)(this.Bounds.Width * scaling);
            int windowHeight = (int)(this.Bounds.Height * scaling);
            
            var holeRects = new System.Collections.Generic.List<Rect>();
            var extraRegions = new System.Collections.Generic.List<Rect>();
            var translationRings = new System.Collections.Generic.List<(Rect Outer, Rect InnerHole)>();

            if (isTranslation && _viewModel != null)
            {
                foreach (var sel in _viewModel.UserSelections)
                {
                    if (sel.Bounds.Width > 10 && sel.Bounds.Height > 10)
                    {
                        var rect = sel.Bounds;

                        if (sel.IsTranslated)
                        {
                            if (sel.IsAudioPanel)
                            {
                                // Audio panel keeps text inside fixed box (no extra text island below).
                                extraRegions.Add(new Rect(
                                    rect.X * scaling,
                                    rect.Y * scaling,
                                    rect.Width * scaling,
                                    rect.Height * scaling));
                            }
                            else
                            {
                                // 已翻譯：選取框及下方文字島嶼保持不透明
                                extraRegions.Add(new Rect(
                                    rect.X * scaling,
                                    rect.Y * scaling,
                                    rect.Width * scaling,
                                    (rect.Height + sel.EstimatedTextHeight + 20) * scaling));
                            }
                        }
                        else
                        {
                            // Translation behaves like screenshot/recording selection flow.
                            translationRings.Add(ComputeScreenshotStyleRingPhysicalRects(rect, scaling));
                        }
                    }
                }
            }

            // Translation single/multi (untranslated selection): same ring + inner hole as screenshot — not full-window-minus-holes.
            if (isTranslation && translationRings.Count > 0)
            {
                // Hold Ctrl: full client hit-test (same as no-selection) so the user can start a new rect outside the ring; release or finish drag restores rings.
                bool selModHeld = IsTranslationSelectionModifierDownForRegion();
                if (selModHeld && !_translationSuppressFullHitUntilSelectionModifierUp)
                {
                    ApplyTranslationDwmMinimalOccluderFix(hwnd, scaling, windowWidth, windowHeight);
                    return;
                }

                Rect? toolbarRectRing = null;
                if (_viewModel != null && _viewModel.IsToolbarVisible && _viewModel.ToolbarWidth > 0 && _viewModel.ToolbarHeight > 0)
                {
                    double tw = TranslationToolbarOpaqueWidthDip(_viewModel.ToolbarWidth + 40);
                    double th = _viewModel.ToolbarHeight + 40;
                    double tx = _viewModel.ToolbarLeft - 20;
                    double ty = _viewModel.ToolbarTop - 20;
                    toolbarRectRing = new Rect(tx * scaling, ty * scaling, tw * scaling, th * scaling);
                }

                var ringsExtras = new System.Collections.Generic.List<Rect>(extraRegions);
                if (_viewModel != null && _viewModel.ShowTopLoadingBar)
                {
                    foreach (var screen in _viewModel.AllScreenBounds)
                    {
                        ringsExtras.Add(new Rect(
                            screen.X * scaling,
                            screen.Y * scaling,
                            screen.W * scaling,
                            8 * scaling));
                    }
                }

                Win32Helpers.SetMultipleBoundingBoxHoleRegions(hwnd, translationRings, toolbarRectRing, ringsExtras);
                return;
            }
            else if (state == SnipState.Selected)
            {
                // V8 Fix: Use a single contiguous Bounding Box region with an inner hole!
                // This allows full-screen pass-through outside the box AND avoids the DWM shadow 
                // glitches caused by combining multiple disjoint `extraRegions` for wings/skulls.
                var scaledRect = new Rect(selectionRect.X * scaling, selectionRect.Y * scaling, selectionRect.Width * scaling, selectionRect.Height * scaling);
                
                // Calculate outer bounding box that firmly wraps the selection + all wings/borders
                double maxMargin = 0;
                if (_viewModel != null)
                {
                    double hSize = 40 * scaling;      // Handles
                    double sThick = 15 * scaling;     // Frame edges
                    double wW = _viewModel.WingWidth * scaling;
                    double wH = _viewModel.WingHeight * scaling;
                    double iSize = (_viewModel.SelectionIconSize + 8) * scaling; 
                    
                    // Base margin handles basic widths
                    maxMargin = Math.Max(hSize / 2, Math.Max(sThick / 2, Math.Max(wW, iSize)));
                    
                    // Account for vertical overflowing wings on short selections
                    double verticalOverflow = (wH / 2) - (scaledRect.Height / 2);
                    if (verticalOverflow > maxMargin)
                    {
                        maxMargin = verticalOverflow + (10 * scaling); // +10px safety buffer
                    }
                }
                else
                {
                    maxMargin = 20 * scaling;
                }
                
                // Add an extra safety buffer for high-DPI or slight rounding variations
                maxMargin += 20 * scaling;
                
                // The single contiguous outer region containing our graphics (avoids slicing complex PNG anti-aliasing)
                var outerBox = new Rect(
                    scaledRect.X - maxMargin,
                    scaledRect.Y - maxMargin,
                    scaledRect.Width + maxMargin * 2,
                    scaledRect.Height + maxMargin * 2
                );

                Rect? selectedToolbarRect = null;
                if (_viewModel != null && _viewModel.IsToolbarVisible && _viewModel.ToolbarWidth > 0 && _viewModel.ToolbarHeight > 0)
                {
                    double tw = _viewModel.ToolbarWidth + 20; 
                    double th = _viewModel.ToolbarHeight + 20;
                    selectedToolbarRect = new Rect((_viewModel.ToolbarLeft - 2) * scaling, (_viewModel.ToolbarTop - 2) * scaling, tw * scaling, th * scaling);
                }

                // Avalonia draws borders and corners INSIDE the selectionRect Canvas.
                // Punching a hole exactly at scaledRect deletes them! 
                // We must shrink (deflate) the inner hole by the max inner penetration of our UI objects (~30px)
                double innerShrink = 0;
                if (_viewModel != null)
                {
                    // Corners penetrate by IconSize AND they are pushed inwards by a Margin proportional to BorderThickness.
                    double innerIconWidth = (_viewModel.SelectionIconSize + 4) * scaling; 
                    double borderThick = _viewModel.SelectionBorderThickness * scaling;
                    
                    // The inner clipping point is Additive (BorderThickness + IconSize + Safety Buffer)
                    innerShrink = borderThick + innerIconWidth + (12 * scaling);
                    innerShrink = Math.Max(15 * scaling, innerShrink); // At least 15px for basic UI handles
                }
                
                // Safe guard: Do not shrink the hole so much that it inverts the geometry natively
                double maxAllowedShrink = Math.Min(scaledRect.Width, scaledRect.Height) / 2.0 - 1;
                if (maxAllowedShrink < 0) maxAllowedShrink = 0;
                innerShrink = Math.Min(innerShrink, maxAllowedShrink);

                var innerHole = new Rect(
                    scaledRect.X + innerShrink,
                    scaledRect.Y + innerShrink,
                    Math.Max(0, scaledRect.Width - innerShrink * 2),
                    Math.Max(0, scaledRect.Height - innerShrink * 2)
                );

                Win32Helpers.SetBoundingBoxHoleRegion(hwnd, outerBox, innerHole, selectedToolbarRect);
                return;
            }

            // Normal logic for Translation Mode
            // V8 修正：翻譯模式下即便 holeRects 為空（全部已翻譯），
            // 也要正確設定 region（只有 extraRegions 的情況）
            if (holeRects.Count == 0 && extraRegions.Count == 0)
            {
                if (isTranslation)
                {
                    // No selection yet: pass-through everywhere except toolbar/loading so the user can operate
                    // underlying apps; hold Ctrl to switch to full-window hit (see RequestTranslationWindowRegionRefresh).
                    bool selModHeld = IsTranslationSelectionModifierDownForRegion();
                    if (selModHeld && !_translationSuppressFullHitUntilSelectionModifierUp)
                    {
                        ApplyTranslationDwmMinimalOccluderFix(hwnd, scaling, windowWidth, windowHeight);
                        _useHitTestRegions = false;
                    }
                    else
                    {
                        ApplyTranslationPassThroughExceptToolbarAndLoadingBar(hwnd, scaling);
                    }
                }
                else
                {
                    Win32Helpers.ClearWindowRegion(hwnd);
                }
                return;
            }

            Rect? toolbarRect = null;
            if (_viewModel != null && _viewModel.IsToolbarVisible && _viewModel.ToolbarWidth > 0 && _viewModel.ToolbarHeight > 0)
            {
                // V13: Robust toolbar region calculation
                double tw = TranslationToolbarOpaqueWidthDip(_viewModel.ToolbarWidth + 40); // More padding
                double th = _viewModel.ToolbarHeight + 40;
                double tx = _viewModel.ToolbarLeft - 20;
                double ty = _viewModel.ToolbarTop - 20;

                toolbarRect = new Rect(tx * scaling, ty * scaling, tw * scaling, th * scaling);
                
                // Add to extraRegions to be absolutely sure it's opaque in SetMultiWindowHoleRegion
                extraRegions.Add(toolbarRect.Value);
            }

            // V13: Ensure TopLoadingBar is visible in Translation Mode (including General Mode)
            if (_viewModel != null && _viewModel.ShowTopLoadingBar)
            {
                foreach (var screen in _viewModel.AllScreenBounds)
                {
                    extraRegions.Add(new Rect(
                        screen.X * scaling, 
                        screen.Y * scaling, 
                        screen.W * scaling, 
                        8 * scaling)); // Increased height slightly for visibility safety
                }
            }

            int borderWidth = (int)((_viewModel?.SelectionBorderThickness ?? 6) * scaling);

            // Translation remainder: translated-only islands (+ toolbar/loading already in extraRegions).
            // Must NOT use ApplyTranslationDwmMinimalOccluderFix here — that path uses almost the full client
            // as SetWindowRgn opaque area (full window minus a 1×1 hole), which occludes YouTube/hardware video
            // under the overlay. Disjoint islands + 1×1 DWM stub matches idle translation behavior.
            if (isTranslation && holeRects.Count == 0)
            {
                var opaque = new System.Collections.Generic.List<Rect>(extraRegions);
                opaque.Add(new Rect(0, 0, 1, 1));
                Win32Helpers.SetDisjointOpaqueRegions(hwnd, opaque, null);
                _hitTestRegions.Clear();
                _hitTestRegions.AddRange(extraRegions);
                _useHitTestRegions = _hitTestRegions.Count > 0;
                return;
            }

            Win32Helpers.SetMultiWindowHoleRegion(hwnd, windowWidth, windowHeight, holeRects, borderWidth, toolbarRect, extraRegions);
        }
        else
        {
            double scaling = this.RenderScaling;
            int windowWidth = (int)(this.Bounds.Width * scaling);
            int windowHeight = (int)(this.Bounds.Height * scaling);

            if (!isTranslation)
            {
                // To prevent Chromium-based browsers (Edge/Chrome) from aggressively 
                // occluding YouTube/hardware-accelerated videos behind the SnipWindow initially,
                // we punch a microscopic 1x1 pixel hole at the top left.
                // This breaks the "full screen occluder" heuristic in the Desktop Window Manager (DWM).
                Win32Helpers.SetMultiWindowHoleRegion(hwnd, windowWidth, windowHeight, new[] { new Rect(0, 0, 1, 1) }, 0);
            }
            else
            {
                // Translation + drawing: pointer must hit the whole overlay; disjoint-only would break ink outside tiny regions.
                // (When toolbar is hidden, ApplyTranslationDwmMinimalOccluderFix would route to pass-through — do not use it here.)
                Rect? drawToolbarRect = null;
                if (_viewModel != null && _viewModel.IsToolbarVisible && _viewModel.ToolbarWidth > 0 && _viewModel.ToolbarHeight > 0)
                {
                    double dtw = TranslationToolbarOpaqueWidthDip(_viewModel.ToolbarWidth + 40);
                    double dth = _viewModel.ToolbarHeight + 40;
                    double dtx = _viewModel.ToolbarLeft - 20;
                    double dty = _viewModel.ToolbarTop - 20;
                    drawToolbarRect = new Rect(dtx * scaling, dty * scaling, dtw * scaling, dth * scaling);
                }

                Win32Helpers.SetMultiWindowHoleRegion(hwnd, windowWidth, windowHeight, new[] { new Rect(0, 0, 1, 1) }, 0, drawToolbarRect, null);
            }
        }
    }

}
