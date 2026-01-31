using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Platform;

namespace GimmeCapture.Services;

public class GlobalHotkeyService : IDisposable
{
    private const int WM_HOTKEY = 0x0312;
    private const int HOTKEY_ID = 9000; // Unique ID for our snip hotkey

    private IntPtr _handle;
    private bool _isRegistered;
    
    // Action to fire when hotkey is pressed
    public Action? OnHotkeyPressed { get; set; }

    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private delegate IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    
    // We don't necessarily need to subclass if we just use the Avalonia Window's parsing, 
    // but Avalonia doesn't expose a raw WndProc easily in a cross-platform way without some lifting.
    // However, typical Avalonia way allows us to hook into the wndproc via IWindowImpl (internal) 
    // or just use a message loop hook if possible.
    // The safest "modern Avalonia" way without digging too deep into internals is usually 
    // creating a dummy invisible window or just subclassing the main window handle if we have it.
    
    // Actually, Avalonia on Windows exposes `Win32PlatformOptions` but not direct WndProc hook easily in 11.0 
    // without `ICoCreateTopLevelImpl`. 
    // BUT! We can just use a simple message loop filter or `UnamangedMethods` if we had a dedicated message window.
    // Let's try to just use the Main Window handle and assume we can catch the message or if Avalonia eats it.
    // Spoiler: Avalonia might eat it.
    // 
    // Safer bet: Create a dedicated "MessageOnly" window? No, that's heavy.
    // Let's try to use the standard Win32 approach: Subclassing or checking if Avalonia has a hook.
    // Avalonia 0.10 had `Win32Properties.WndProc`, but 11.x?
    // 
    // Let's rely on standard HWND and `SetWindowLongPtr` (Subclassing) which is robust.
    
    public void Initialize(Window window)
    {
        if (!OperatingSystem.IsWindows()) return;

        var platformHandle = window.TryGetPlatformHandle();
        if (platformHandle != null)
        {
            _handle = platformHandle.Handle;
            
            // Note: In a real robust app, we should subclass the window procedure (WndProc) 
            // to listen for WM_HOTKEY.
            // For this implementation, let's keep it simple. If we just RegisterHotKey, the WM_HOTKEY message
            // arrives in the message queue. Avalonia's loop will dispatch it. 
            // Does Avalonia expose it? Not directly.
            // SO we MUST subclass functionality to intercept the message.
            
            // Adding a simple hook managed by ourself.
            // Since Subclassing in C# can be tricky with delegates being GC'd, we need to be careful.
            // For now, let's assume we can reference a simpler library or just implement the delegate.
            
            // To avoid complex Subclassing code which might crash if not careful (32 vs 64 bit),
            // We can look for a library-free approach or use a background thread polling (GetAsyncKeyState) 
            // but Polling is bad for battery/performance.
            
            // Let's try the cleanest "Native" way: 
            // We actually don't NEED to subclass if we use a global hook (SetWindowsHookEx), 
            // but RegisterHotKey is cleaner. It requires a loop message.
            // 
            // CRITICAL: Avalonia's `Win32Platform` exposes `WndProc` hook via reflection or internal? No.
            // 
            // Let's go with a hidden dummy form? No forms allowed.
            // 
            // Let's implement full proper Subclassing.
        }
    }
    
    // To keep this Initial Proof of Concept robust, I will use a P/Invoke subclass wrapper.
    // HOWEVER, actually modifying the WndProc of the Main Window can be dangerous if Avalonia fights back.
    // 
    // SIMPLIFICATION: I will use `GetAsyncKeyState` in a timer loop for "Global" hotkeys as a FAILSAFE first step?
    // NO! The user wants "Global Hotkey". Polling is "Global" but ugly.
    // 
    // Correct way:
    // Pass the handle. Subclass it.
    
    // --- Subclassing Infrastructure ---
    private IntPtr _oldWndProc = IntPtr.Zero;
    private WndProc _newWndProc; // Keep reference to prevent GC

    public void Register(string hotkey)
    {
        if (!OperatingSystem.IsWindows() || _handle == IntPtr.Zero) return;
        
        // 1. Unregister old
        Unregister();

        // 2. Parse hotkey string (Simple implementation: "F1", "Ctrl+F1")
        // Mapping: F1 = 0x70
        // Mods: Ctrl = 2, Alt = 1, Shift = 4, Win = 8
        
        (uint modifiers, uint vkey) = ParseHotkey(hotkey);

        if (vkey != 0)
        {
            // 3. Register
             bool success = RegisterHotKey(_handle, HOTKEY_ID, modifiers, vkey);
             if (success)
             {
                 _isRegistered = true;
                 
                 // Install Hook if not already
                 if (_oldWndProc == IntPtr.Zero)
                 {
                     InstallWndProcHook();
                 }
             }
        }
    }
    
    private void Unregister()
    {
        if (_isRegistered && _handle != IntPtr.Zero)
        {
            UnregisterHotKey(_handle, HOTKEY_ID);
            _isRegistered = false;
        }
    }

    private (uint mods, uint vkey) ParseHotkey(string hk)
    {
        // Very basic parser for "F1", "F12", "Ctrl+S" etc.
        // For Phase 1, let's support F1-F12 and simple modifiers.
        
        hk = hk.ToUpper().Trim();
        uint mods = 0;
        
        if (hk.Contains("CTRL")) mods |= 0x0002;
        if (hk.Contains("ALT")) mods |= 0x0001;
        if (hk.Contains("SHIFT")) mods |= 0x0004;
        
        // Extract key
        string keyPart = hk;
        if (hk.Contains('+'))
        {
            var parts = hk.Split('+');
            keyPart = parts[parts.Length - 1].Trim();
        }

        uint key = 0;
        // Function keys
        if (keyPart.StartsWith("F") && keyPart.Length > 1 && int.TryParse(keyPart.Substring(1), out int fNum))
        {
            if (fNum >= 1 && fNum <= 24)
                key = (uint)(0x70 + fNum - 1);
        }
        else if (keyPart == "PRINTSCREEN" || keyPart == "PRTSC")
        {
            key = 0x2C;
        }
        else if (keyPart.Length == 1 && char.IsLetterOrDigit(keyPart[0]))
        {
             // ASCII (A-Z 0-9) maps directly to VK for these
             key = (uint)keyPart[0];
        }

        return (mods, key);
    }
    
    // --- Window Subclassing ---
    
    [DllImport("user32.dll", SetLastError = true, EntryPoint = "SetWindowLongPtr")]
    private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", SetLastError = true, EntryPoint = "SetWindowLong")]
    private static extern IntPtr SetWindowLong32(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", CallingConvention = CallingConvention.StdCall)]
    private static extern IntPtr CallWindowProc(IntPtr lpPrevWndFunc, IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    private const int GWLP_WNDPROC = -4;

    private static IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong)
    {
        if (IntPtr.Size == 8)
            return SetWindowLongPtr64(hWnd, nIndex, dwNewLong);
        else
            return SetWindowLong32(hWnd, nIndex, dwNewLong);
    }

    private void InstallWndProcHook()
    {
        _newWndProc = new WndProc(CustomWndProc);
        IntPtr newWndProcPtr = Marshal.GetFunctionPointerForDelegate(_newWndProc);
        _oldWndProc = SetWindowLongPtr(_handle, GWLP_WNDPROC, newWndProcPtr);
    }
    
    private void RemoveWndProcHook()
    {
        if (_oldWndProc != IntPtr.Zero && _handle != IntPtr.Zero)
        {
            SetWindowLongPtr(_handle, GWLP_WNDPROC, _oldWndProc);
            _oldWndProc = IntPtr.Zero;
        }
    }

    private IntPtr CustomWndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WM_HOTKEY)
        {
            int id = wParam.ToInt32();
            if (id == HOTKEY_ID)
            {
                OnHotkeyPressed?.Invoke();
            }
        }
        
        return CallWindowProc(_oldWndProc, hWnd, msg, wParam, lParam);
    }

    public void Dispose()
    {
        Unregister();
        RemoveWndProcHook();
    }
}
