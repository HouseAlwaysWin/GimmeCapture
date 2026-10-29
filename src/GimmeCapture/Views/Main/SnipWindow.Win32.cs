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
using System.ComponentModel;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Input;
using Avalonia.Input.Raw;
using GimmeCapture.Services.Platforms.Avalonia;
#if WINDOWS
using GimmeCapture.Services.Platforms.Windows;
#endif

namespace GimmeCapture.Views.Main;

public partial class SnipWindow : Window
{
    // Click-through outside the hit-test regions (IsHitTestPassThrough), installed once the window has a handle.
    private Win32HitTestPassThrough? _hitTestPassThrough;
    private bool _translationClickThroughExStyleActive;
    private bool _overlayNoActivateExStyleActive;

    // Set once this overlay recorded or was excluded from screen capture. Hiding or closing such a window can leave a
    // DWM ghost of its last frame, the recording selection frame (docs/archive/WGC_HANDOFF.md, Fix F), so only such an
    // overlay clears its region before it closes (OnClosing) and repaints the desktop after (OnClosed).
    private bool _mayLeaveCaptureGhost;

    // Every change to the overlay's capture affinity goes through here, so no exclusion escapes the flag above.
    private void SetOverlayCaptureVisibility(IntPtr hwnd, bool visible)
    {
        if (!visible)
        {
            _mayLeaveCaptureGhost = true;
        }

        Win32Helpers.SetWindowCaptureVisibility(hwnd, visible);
    }

    /// <summary>
    /// While the translation toolbar is HIDDEN, add <c>WS_EX_TRANSPARENT</c> to the overlay so a Chromium
    /// (GPU/DWM-composited) browser underneath stops graying out on tab/page switch. Chromium's native-window
    /// occlusion tracker treats a <c>WS_EX_TRANSPARENT</c> top-most window as non-occluding and skips it, so it
    /// never marks the browser occluded. This is the ONLY lever that fixes the gray — the window's content,
    /// Win32 region, and capture affinity (the earlier attempts) do not affect Chromium's occlusion decision,
    /// only the extended style does. The style is removed when the toolbar is shown so the toolbar stays
    /// clickable; while hidden the overlay is fully click-through anyway ("read the page" mode — Esc / the
    /// toolbar-toggle hotkey still work through the global keyboard hook). Windows-only; no-op elsewhere.
    /// </summary>
    private void SyncTranslationClickThroughExStyle()
    {
        if (!OperatingSystem.IsWindows() || _viewModel == null) return;

        // While the selection modifier is held the overlay must be INTERACTIVE so a Ctrl-drag can start a
        // selection even with the toolbar hidden — so it can't be WS_EX_TRANSPARENT then. The browser dims like a
        // normal screenshot selection for that brief window and restores on release. Otherwise (reading the page)
        // keep it click-through so the browser doesn't gray.
        bool selecting = IsTranslationSelectionModifierDownForRegion() && !_translationSuppressFullHitUntilSelectionModifierUp;
        bool want = _viewModel.IsTranslationMode && !_viewModel.IsToolbarShownOnScreen && !selecting;
        if (want == _translationClickThroughExStyleActive) return;

        var hwnd = this.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (hwnd == IntPtr.Zero) return;

        try
        {
            User32.SetExStyleFlags(hwnd, User32.WS_EX_TRANSPARENT, want);
            _translationClickThroughExStyleActive = want;
        }
        catch (Exception e)
        {
            AppLog.Warning("SnipWindow.SyncTranslationClickThroughExStyle", e);
        }
    }

    /// <summary>
    /// Applies (or removes) <c>WS_EX_NOACTIVATE</c> on the overlay so it can be shown on top and receive mouse
    /// clicks WITHOUT stealing foreground focus — a normal top-most window activates on click, and that focus
    /// transfer is exactly what closes a target app's dropdown / right-click menu before it can be captured.
    /// Enabled when <see cref="SnipWindowViewModel.ShouldAvoidStealingFocus"/> (the CaptureWithoutStealingFocus
    /// setting, non-translation), and temporarily removed while the annotation text editor is open so the
    /// textbox can take real keyboard focus. Keyboard shortcuts still work via the process-global LL hook.
    /// Windows-only; no-op elsewhere.
    /// </summary>
    private void SyncOverlayNoActivateExStyle()
    {
        if (!OperatingSystem.IsWindows() || _viewModel == null) return;

        bool want = _viewModel.ShouldAvoidStealingFocus && !_viewModel.IsEnteringText;
        if (want == _overlayNoActivateExStyleActive) return;

        var hwnd = this.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (hwnd == IntPtr.Zero) return;

        try
        {
            User32.SetExStyleFlags(hwnd, User32.WS_EX_NOACTIVATE, want);
            _overlayNoActivateExStyleActive = want;
        }
        catch (Exception e)
        {
            AppLog.Warning("SnipWindow.SyncOverlayNoActivateExStyle", e);
        }
    }

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
    // Windows holds every key typed anywhere until a low-level hook returns. This hook used to run on the UI thread,
    // so whenever the UI was busy all typing waited, in every app, for as long as the overlay was open (a whole
    // recording included). It now runs on a thread of its own: a key the overlay cannot act on (SnipOverlayKeyFilter)
    // passes straight through, and the rest are routed on the UI thread, which gets UiRouteTimeout to answer.
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_KEYUP = 0x0101;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int WM_SYSKEYUP = 0x0105;
    private static readonly TimeSpan UiRouteTimeout = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan UiRouteGrace = TimeSpan.FromMilliseconds(50);

    // The hook handle and the timeout-log throttle are touched on the hook thread only, and the delegate is kept
    // alive while the hook is installed. The filter is written on the UI thread and read on the hook thread.
    private IntPtr _llKeyboardHook = IntPtr.Zero;
    private User32.LowLevelKeyboardProc? _llKeyboardDelegate;
    private IntPtr _llKeyboardOverlayHandle;
    private SnipOverlayKeyFilter _llKeyboardFilter = SnipOverlayKeyFilter.None;
    private SnipOverlayHotkeys? _llKeyboardFilterInputs;
    private SnipWindowViewModel? _llKeyboardFilterSource;
    private long _lastUiRouteTimeoutLogTicks;
#if WINDOWS
    private Win32HotkeyThread? _llKeyboardThread;
#endif

    private void InstallLLKeyboardHook()
    {
        // The delegate lives exactly as long as the hook.
        if (_llKeyboardDelegate != null) return;

        _llKeyboardOverlayHandle = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        _llKeyboardFilterSource = _viewModel;
        if (_llKeyboardFilterSource != null)
        {
            _llKeyboardFilterSource.PropertyChanged += OnViewModelChangedForKeyFilter;
        }

        RefreshLLKeyboardFilter();

        _llKeyboardDelegate = LLKeyboardHookCallback;
#if WINDOWS
        var thread = new Win32HotkeyThread("GimmeCapture.SnipKeyboardHook", "SnipHook");
        bool started = thread.Start(() =>
        {
            _llKeyboardHook = User32.SetWindowsHookEx(User32.WH_KEYBOARD_LL, _llKeyboardDelegate, Kernel32.GetModuleHandle(null), 0);
            if (_llKeyboardHook == IntPtr.Zero)
            {
                _llKeyboardHook = User32.SetWindowsHookEx(User32.WH_KEYBOARD_LL, _llKeyboardDelegate, IntPtr.Zero, 0);
            }

            if (_llKeyboardHook == IntPtr.Zero)
            {
                AppLog.Warning("SnipHook.Install", $"SetWindowsHookEx failed (win32={Marshal.GetLastWin32Error()}).");
            }
        }, TimeSpan.FromSeconds(2));

        if (!started)
        {
            AppLog.Warning("SnipHook.Install", "The overlay keyboard hook thread did not start within 2 s.");
        }

        _llKeyboardThread = thread;
#endif
    }

    private void UninstallLLKeyboardHook()
    {
        if (_llKeyboardFilterSource != null)
        {
            _llKeyboardFilterSource.PropertyChanged -= OnViewModelChangedForKeyFilter;
            _llKeyboardFilterSource = null;
        }

        _llKeyboardFilterInputs = null;
        Volatile.Write(ref _llKeyboardFilter, SnipOverlayKeyFilter.None);

#if WINDOWS
        var thread = _llKeyboardThread;
        _llKeyboardThread = null;
        if (thread != null)
        {
            // Unhook and end the hook thread without the UI thread waiting for either: that wait cost every overlay
            // close 20-25 ms on a published build, and longer when the hook thread was itself waiting for this thread
            // to route a key (it gives up after UiRouteTimeout). The filter above already passes every key straight
            // through, so the hook does nothing in the meantime; the delegate stays referenced until it is unhooked.
            var hookProc = _llKeyboardDelegate;
            Task.Run(() =>
            {
                thread.TryInvoke(() =>
                {
                    if (_llKeyboardHook != IntPtr.Zero)
                    {
                        User32.UnhookWindowsHookEx(_llKeyboardHook);
                        _llKeyboardHook = IntPtr.Zero;
                    }
                }, TimeSpan.FromMilliseconds(500));
                thread.Dispose();
                GC.KeepAlive(hookProc);
            }).Forget("SnipHook.Uninstall");
        }
#endif
        _llKeyboardDelegate = null;
    }

    private void OnViewModelChangedForKeyFilter(object? sender, PropertyChangedEventArgs e) => RefreshLLKeyboardFilter();

    // The overlay's hotkeys follow its mode and the app settings without change notifications of their own, so they
    // are read again on every view model change; the filter is rebuilt only when one of them differs.
    private void RefreshLLKeyboardFilter()
    {
        var vm = _llKeyboardFilterSource;
        if (vm == null) return;

        var inputs = new SnipOverlayHotkeys(
            IsTranslationMode: vm.IsTranslationMode,
            SelectionHoldModifier: vm.TranslationSelectionHoldModifier,
            Close: vm.CloseHotkey,
            Toolbar: vm.ActiveToolbarHotkey,
            Action: vm.ActiveActionHotkey,
            FullscreenSelect: vm.FullscreenSelectHotkey,
            SwitchToSnip: vm.SwitchToSnipHotkey,
            SwitchToRecord: vm.SwitchToRecordHotkey,
            SwitchToTranslate: vm.SwitchToTranslateHotkey,
            Playback: vm.ActivePlaybackHotkey,
            Stop: vm.ActiveStopHotkey,
            Copy: vm.CopyHotkey,
            Save: vm.SaveHotkey,
            Undo: vm.UndoHotkey,
            Redo: vm.RedoHotkey,
            Clear: vm.ClearHotkey,
            Rectangle: vm.RectangleHotkey,
            Ellipse: vm.EllipseHotkey,
            Arrow: vm.ArrowHotkey,
            Line: vm.LineHotkey,
            Mosaic: vm.MosaicHotkey,
            Blur: vm.BlurHotkey,
            Pen: vm.PenHotkey,
            Text: vm.TextHotkey,
            TranslateAll: vm.TranslateAllHotkey,
            TranslatePin: vm.TranslatePinHotkey,
            ScanAll: vm.ScanAllHotkey,
            ClearAll: vm.ClearAllHotkey);
        if (inputs == _llKeyboardFilterInputs) return;

        _llKeyboardFilterInputs = inputs;
        Volatile.Write(ref _llKeyboardFilter, SnipOverlayKeyFilter.Build(inputs));
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        if (IsWarmUp)
        {
            return;
        }

        UninstallLLKeyboardHook();
        StopLinuxSnipKeyGrab();
        _viewModel?.TranslationResultLayer?.RefreshWindowState();

        // The snip/record/translate overlay held large capture/preview buffers (a full-screen grab is tens of
        // MB), now released. On Linux the C allocator keeps freed pages until asked to hand them back, which this
        // does once the app is idle, like the main-window→tray path; Windows returns them as they are freed, and
        // nothing is scheduled there. The scheduler skips this if the user is immediately active again.
        _ = GimmeCapture.Services.Core.Infrastructure.ProcessMemoryTrimService
            .RequestIdleWorkingSetTrimAsync("snip-overlay-closed");

        // Safety net: force the whole desktop to repaint so any lingering DWM ghost of this (capture-excluded,
        // regioned) overlay — the yellow recording selection frame — is cleared even if the hide-time cleanup
        // didn't catch it. See docs/archive/WGC_HANDOFF.md. Only after an overlay that can leave one: this used to
        // repaint every window on screen after every capture, our own pins synchronously among them. The overlay
        // spans every monitor, so its own rectangle would be the whole desktop all the same.
        if (_mayLeaveCaptureGhost && OperatingSystem.IsWindows())
        {
            try
            {
                RedrawWindow(IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                    RDW_INVALIDATE | RDW_ERASE | RDW_ALLCHILDREN | RDW_UPDATENOW);
            }
            catch
            {
                // best effort
            }
        }
    }

    [DllImport("user32.dll")]
    private static extern bool RedrawWindow(IntPtr hWnd, IntPtr lprcUpdate, IntPtr hrgnUpdate, uint flags);
    private const uint RDW_INVALIDATE = 0x0001;
    private const uint RDW_ERASE = 0x0004;
    private const uint RDW_ALLCHILDREN = 0x0080;
    private const uint RDW_UPDATENOW = 0x0100;

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
    private bool ShouldRouteLowLevelHotkeysForForegroundWindow() =>
        IsForegroundWithin(TryGetPlatformHandle()?.Handle ?? IntPtr.Zero);

    // Callable from any thread, so the hook thread asks it too, with the handle read once on the UI thread.
    private static bool IsForegroundWithin(IntPtr mine)
    {
        if (mine == IntPtr.Zero) return false;
        IntPtr fg = GetForegroundWindow();
        if (fg == IntPtr.Zero) return false;
        if (fg == mine) return true;
        if (IsChild(mine, fg)) return true;
        return GetAncestor(fg, GA_ROOT) == mine;
    }

    private IntPtr LLKeyboardHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        // Windows calls this from native code: an exception that escapes does not reach any .NET handler, it
        // terminates the process on the spot. Whatever went wrong with one key, log it and let the key through.
        try
        {
            return HandleLLKeyboardHook(nCode, wParam, lParam);
        }
        catch (Exception ex)
        {
            AppLog.Error("SnipWindow.KeyboardHook", ex);
            return User32.CallNextHookEx(_llKeyboardHook, nCode, wParam, lParam);
        }
    }

    // Runs on the hook thread.
    private IntPtr HandleLLKeyboardHook(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
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
                bool modifierDown = (User32.GetAsyncKeyState(0x10) & 0x8000) != 0 // Shift
                    || (User32.GetAsyncKeyState(0x11) & 0x8000) != 0             // Ctrl
                    || (User32.GetAsyncKeyState(0x12) & 0x8000) != 0;            // Alt
                var filter = Volatile.Read(ref _llKeyboardFilter);

                // Route the parsed key through the low-level hotkey dispatcher, unless it is one the dispatcher
                // cannot act on. If the key is a modifier (Shift/Ctrl/Alt), we NEVER swallow it globally to avoid
                // breaking typing elsewhere. If it matched one of our global capture handlers, we swallow it so it
                // does not trigger apps underneath.
                if (filter.ShouldRoute(keyStr, isKeyDown, IsForegroundWithin(_llKeyboardOverlayHandle), modifierDown)
                    && RouteKeyOnUiThread(keyStr, vkCode, isKeyDown)
                    && !IsPureModifierKey(keyStr))
                {
                    return new IntPtr(1); // Consume the key
                }
            }
        }
        return User32.CallNextHookEx(_llKeyboardHook, nCode, wParam, lParam);
    }

    // On the hook thread. The routing runs on the UI thread, which owns the view model; a key it does not get to
    // within UiRouteTimeout goes to the focused app, and the routing will not act on it later.
    private bool RouteKeyOnUiThread(string keyStr, int vkCode, bool isKeyDown)
    {
        bool handled = TimedUiRoute.Ask(
            PostToUiThread,
            () => HandleGlobalKeyboardEvent(keyStr, vkCode, isKeyDown),
            UiRouteTimeout,
            UiRouteGrace,
            out bool timedOut);

        if (timedOut)
        {
            ReportUiRouteTimeout();
        }

        return handled;
    }

    private static Task<bool> PostToUiThread(Func<bool> call)
    {
        var answer = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            try
            {
                answer.SetResult(call());
            }
            catch (Exception ex)
            {
                answer.SetException(ex);
            }
        }, Avalonia.Threading.DispatcherPriority.Send);
        return answer.Task;
    }

    private void ReportUiRouteTimeout()
    {
        long now = Environment.TickCount64;
        if (now - _lastUiRouteTimeoutLogTicks < 10_000)
        {
            return;
        }

        _lastUiRouteTimeoutLogTicks = now;
        AppLog.Warning("SnipHook.UiTimeout",
            $"The UI thread did not route an overlay hotkey within {UiRouteTimeout.TotalMilliseconds:F0} ms; the key went to the focused app.");
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

    // The toolbar toggle is the one unfocused shortcut allowed in EVERY overlay mode — including translation,
    // which the general unfocused path excludes (so target-app Esc can't close the overlay). This lets the
    // user re-show the toolbar after hiding it while the target app (e.g. an editor) has focus. Gated to a
    // toolbar-bearing mode and to not-typing.
    private bool CanToggleToolbarWhileUnfocused()
    {
        return _viewModel != null
            && _viewModel.IsToolbarVisible
            && !_viewModel.IsEnteringText
            && !_viewModel.IsInputFocused;
    }

    private bool MatchesToolbarToggleKeyNow(string keyStr)
    {
        string hotkey = _viewModel?.ActiveToolbarHotkey ?? string.Empty;
        if (!IsSafeUnfocusedCaptureHotkey(hotkey))
        {
            return false; // only a safe single key (e.g. F4) toggles globally; combos would risk swallowing typing
        }

        bool noModifiers = (User32.GetAsyncKeyState(0x10) & 0x8000) == 0 // Shift
            && (User32.GetAsyncKeyState(0x11) & 0x8000) == 0             // Ctrl
            && (User32.GetAsyncKeyState(0x12) & 0x8000) == 0;            // Alt
        ReadOnlySpan<char> keyPart = HotkeyParsingHelper.GetKeyPart(hotkey.AsSpan().Trim());
        return noModifiers && keyPart.Equals(keyStr.AsSpan(), StringComparison.OrdinalIgnoreCase);
    }

    // Esc/close needs the unfocused path ONLY in translation mode. Screenshot/recording already dismiss on an
    // unfocused Esc via the general capture-hotkey gate below — but that gate excludes translation (so a
    // target-app Esc couldn't close the overlay), which is exactly what left the translation overlay
    // un-closable by keyboard once the target app took focus. Gated to not-typing so it won't hijack Esc while
    // the user edits a translation input field.
    private bool CanDismissOrCloseWhileUnfocused()
    {
        return _viewModel != null
            && _viewModel.IsTranslationMode
            && !_viewModel.IsEnteringText
            && !_viewModel.IsInputFocused;
    }

    private bool MatchesDismissOrCloseKeyNow(string keyStr)
    {
        // Never hijack a physical modifier combo (e.g. Ctrl+W in the target app) — only a bare press dismisses.
        bool noModifiers = (User32.GetAsyncKeyState(0x10) & 0x8000) == 0 // Shift
            && (User32.GetAsyncKeyState(0x11) & 0x8000) == 0             // Ctrl
            && (User32.GetAsyncKeyState(0x12) & 0x8000) == 0;            // Alt
        if (!noModifiers)
        {
            return false;
        }

        // Bare Escape always dismisses (single key, universally expected).
        if (string.Equals(keyStr, "Escape", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Also honor a configured single-key close hotkey; reject combos so a bound letter can't swallow typing.
        string hotkey = _viewModel?.CloseHotkey ?? string.Empty;
        if (!IsSafeUnfocusedCaptureHotkey(hotkey))
        {
            return false;
        }
        ReadOnlySpan<char> keyPart = HotkeyParsingHelper.GetKeyPart(hotkey.AsSpan().Trim());
        return keyPart.Equals(keyStr.AsSpan(), StringComparison.OrdinalIgnoreCase);
    }

    private void PostHandleDismissOrCloseHotkey()
    {
        // Route to the same VM decision the Esc key-binding uses, so the two paths can't diverge.
        Avalonia.Threading.Dispatcher.UIThread.Post(
            () => _viewModel?.DismissOrClose(),
            Avalonia.Threading.DispatcherPriority.Input);
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
                () => _viewModel?.ToggleToolbarCommand?.Execute().SubscribeLoggingErrors(),
                Avalonia.Threading.DispatcherPriority.Input);
            return true;
        }

        if (MatchesUnfocusedCaptureHotkey(_viewModel.ActiveActionHotkey, isMatch))
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(
                () => _viewModel?.HandleActiveActionHotkeyCommand?.Execute().SubscribeLoggingErrors(),
                Avalonia.Threading.DispatcherPriority.Input);
            return true;
        }

        if (MatchesUnfocusedCaptureHotkey(_viewModel.FullscreenSelectHotkey, isMatch))
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(
                () => _viewModel?.SelectFullscreenCommand?.Execute().SubscribeLoggingErrors(),
                Avalonia.Threading.DispatcherPriority.Input);
            return true;
        }

        if (MatchesUnfocusedCaptureHotkey(_viewModel.SwitchToSnipHotkey, isMatch))
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(
                () => _viewModel?.SwitchToSnipCommand?.Execute().SubscribeLoggingErrors(),
                Avalonia.Threading.DispatcherPriority.Input);
            return true;
        }

        if (MatchesUnfocusedCaptureHotkey(_viewModel.SwitchToRecordHotkey, isMatch))
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(
                () => _viewModel?.SwitchToRecordCommand?.Execute().SubscribeLoggingErrors(),
                Avalonia.Threading.DispatcherPriority.Input);
            return true;
        }

        if (MatchesUnfocusedCaptureHotkey(_viewModel.SwitchToTranslateHotkey, isMatch))
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(
                () => _viewModel?.SwitchToTranslateCommand?.Execute().SubscribeLoggingErrors(),
                Avalonia.Threading.DispatcherPriority.Input);
            return true;
        }

        return false;
    }

    // On the UI thread. Every key this can act on must be one SnipOverlayKeyFilter lets through to it.
    private bool HandleGlobalKeyboardEvent(string keyStr, int vkCode, bool isKeyDown)
    {
        if (_viewModel == null) return false;

        bool ownsForeground = ShouldRouteLowLevelHotkeysForForegroundWindow();

        // Re-show/hide the toolbar from anywhere while the overlay is active — including translation mode and
        // while the target app has focus. Handled before the general unfocused gate below (which excludes
        // translation for Esc/close etc.); only the safe single-key toolbar hotkey with no modifiers matches,
        // so it won't disturb the target app. Without this, hiding the toolbar while focused on e.g. VS Code
        // left no way to bring it back until the overlay regained focus.
        if (isKeyDown && !ownsForeground && CanToggleToolbarWhileUnfocused() && MatchesToolbarToggleKeyNow(keyStr))
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(
                () => _viewModel?.ToggleToolbarCommand?.Execute().SubscribeLoggingErrors(),
                Avalonia.Threading.DispatcherPriority.Input);
            return true;
        }

        // Dismiss/close (Esc, or the configured close hotkey) while in translation mode with the target app
        // (browser/editor) focused. Mirrors the toolbar-toggle case above and runs before the general unfocused
        // gate below, which deliberately excludes translation for Esc/close — an exclusion that left the overlay
        // impossible to dismiss by keyboard once focus moved to the target app (the reported bug: "esc 會關不掉").
        // Only a bare Esc / single-key close hotkey with no modifiers matches, so it won't swallow target-app
        // shortcuts. Screenshot/recording keep dismissing via the existing gate — this only closes the gap for
        // translation.
        if (isKeyDown && !ownsForeground && CanDismissOrCloseWhileUnfocused() && MatchesDismissOrCloseKeyNow(keyStr))
        {
            PostHandleDismissOrCloseHotkey();
            return true;
        }

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

        bool shiftDown = (User32.GetAsyncKeyState(0x10) & 0x8000) != 0;
        bool ctrlDown = (User32.GetAsyncKeyState(0x11) & 0x8000) != 0;
        bool altDown = (User32.GetAsyncKeyState(0x12) & 0x8000) != 0;

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
                    _viewModel.PauseRecordingCommand?.Execute().SubscribeLoggingErrors();
                    return true;
                }

                if (IsMatch(_viewModel.ActiveStopHotkey))
                {
                    _viewModel.StopRecordingCommand?.Execute().SubscribeLoggingErrors();
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
                    _viewModel.CopyCommand?.Execute().SubscribeLoggingErrors();
                    return true;
                }

                if (IsModifierCombo(_viewModel.SaveHotkey) && IsMatch(_viewModel.SaveHotkey))
                {
                    _viewModel.SaveCommand?.Execute().SubscribeLoggingErrors();
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
                _viewModel.HandleActiveActionHotkeyCommand?.Execute().SubscribeLoggingErrors();
                return true;
            case HotkeyRouterService.WindowHotkeyAction.ToggleToolbar:
                _viewModel.ToggleToolbarCommand?.Execute().SubscribeLoggingErrors();
                return true;
            case HotkeyRouterService.WindowHotkeyAction.Save:
                _viewModel.SaveCommand?.Execute().SubscribeLoggingErrors();
                return true;
            case HotkeyRouterService.WindowHotkeyAction.Copy:
                _viewModel.CopyCommand?.Execute().SubscribeLoggingErrors();
                return true;
        }

        // 2. Common Action Hotkeys (Snip/Record/Translate)
        if (IsMatch(_viewModel.UndoHotkey)) { _viewModel.UndoCommand?.Execute().SubscribeLoggingErrors(); return true; }
        if (IsMatch(_viewModel.RedoHotkey)) { _viewModel.RedoCommand?.Execute().SubscribeLoggingErrors(); return true; }
        if (IsMatch(_viewModel.ClearHotkey)) { _viewModel.ClearAnnotationsCommand?.Execute().SubscribeLoggingErrors(); return true; }

        // 3. Drawing Tools
        if (IsMatch(_viewModel.RectangleHotkey)) { _viewModel.SelectToolCommand?.Execute(Models.AnnotationType.Rectangle).SubscribeLoggingErrors(); return true; }
        if (IsMatch(_viewModel.EllipseHotkey)) { _viewModel.SelectToolCommand?.Execute(Models.AnnotationType.Ellipse).SubscribeLoggingErrors(); return true; }
        if (IsMatch(_viewModel.ArrowHotkey)) { _viewModel.SelectToolCommand?.Execute(Models.AnnotationType.Arrow).SubscribeLoggingErrors(); return true; }
        if (IsMatch(_viewModel.LineHotkey)) { _viewModel.SelectToolCommand?.Execute(Models.AnnotationType.Line).SubscribeLoggingErrors(); return true; }
        if (IsMatch(_viewModel.MosaicHotkey)) { _viewModel.SelectToolCommand?.Execute(Models.AnnotationType.Mosaic).SubscribeLoggingErrors(); return true; }
        if (IsMatch(_viewModel.BlurHotkey)) { _viewModel.SelectToolCommand?.Execute(Models.AnnotationType.Blur).SubscribeLoggingErrors(); return true; }
        if (IsMatch(_viewModel.PenHotkey)) { _viewModel.ToggleToolGroupCommand?.Execute("Pen").SubscribeLoggingErrors(); return true; }
        if (IsMatch(_viewModel.TextHotkey)) { _viewModel.ToggleToolGroupCommand?.Execute("Text").SubscribeLoggingErrors(); return true; }

        // 4. Mode Switching
        if (IsMatch(_viewModel.SwitchToSnipHotkey)) { _viewModel.SwitchToSnipCommand?.Execute().SubscribeLoggingErrors(); return true; }
        if (IsMatch(_viewModel.SwitchToRecordHotkey)) { _viewModel.SwitchToRecordCommand?.Execute().SubscribeLoggingErrors(); return true; }
        if (IsMatch(_viewModel.SwitchToTranslateHotkey)) { _viewModel.SwitchToTranslateCommand?.Execute().SubscribeLoggingErrors(); return true; }

        // 5. Snip/Recording Specific Hotkeys
        if (_viewModel.CurrentMode == SnipMode.Recording && IsMatch(_viewModel.ActivePlaybackHotkey))
        {
            _viewModel.PauseRecordingCommand?.Execute().SubscribeLoggingErrors();
            return true;
        }

        // Stop hotkey ends the active recording from anywhere (global LL-hook path, like pause above).
        if (_viewModel.CurrentMode == SnipMode.Recording && _viewModel.RecState != RecordingState.Idle
            && IsMatch(_viewModel.ActiveStopHotkey))
        {
            _viewModel.StopRecordingCommand?.Execute().SubscribeLoggingErrors();
            return true;
        }

        if ((_viewModel.CurrentMode == SnipMode.Screenshot || _viewModel.CurrentMode == SnipMode.Recording) &&
            IsMatch(_viewModel.FullscreenSelectHotkey))
        {
            _viewModel.SelectFullscreenCommand?.Execute().SubscribeLoggingErrors();
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
                _viewModel.TranslateAllSelectionsCommand?.Execute().SubscribeLoggingErrors();
                return true;
            }

            if (IsMatch(_viewModel.TranslatePinHotkey))
            {
                _viewModel.PinTranslationResultsCommand?.Execute().SubscribeLoggingErrors();
                return true;
            }

            if (IsMatch(_viewModel.ScanAllHotkey))
            {
                _viewModel.ScanAllTextCommand?.Execute().SubscribeLoggingErrors();
                return true;
            }

            if (IsMatch(_viewModel.ClearAllHotkey))
            {
                _viewModel.ClearAllSelectionsCommand?.Execute().SubscribeLoggingErrors();
                return true;
            }
        }

        return false;
    }


}
