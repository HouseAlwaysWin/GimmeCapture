using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using GimmeCapture.Services.Abstractions;
using Avalonia.Controls;
using Avalonia.Platform;

namespace GimmeCapture.Services.Platforms.Windows;

public class WindowsGlobalHotkeyService : IGlobalHotkeyService
{
    private const int WM_HOTKEY = 0x0312;
    private IntPtr _handle;
    private readonly HashSet<int> _registeredIds = new();
    private readonly Dictionary<int, string> _pendingRegistrations = new();
    private bool _isSuspended;
    
    // Action to fire when hotkey is pressed, passing the ID
    public Action<int>? OnHotkeyPressed { get; set; }
    public Action<int, string, int>? OnHotkeyRegistrationFailed { get; set; }
    
    private IntPtr _oldWndProc = IntPtr.Zero;
    private WndProc? _newWndProc; // Keep reference to prevent GC

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    protected virtual bool NativeRegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vkey)
    {
        return RegisterHotKey(hWnd, id, fsModifiers, vkey);
    }

    protected virtual bool NativeUnregisterHotKey(IntPtr hWnd, int id)
    {
        return UnregisterHotKey(hWnd, id);
    }

    private delegate IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    
    public void Initialize(Window window)
    {
        if (!OperatingSystem.IsWindows()) return;

        var platformHandle = window.TryGetPlatformHandle();
        if (platformHandle != null)
        {
            _handle = platformHandle.Handle;

            if (_pendingRegistrations.Count == 0)
            {
                return;
            }

            // Register from a snapshot because Register/Unregister mutates the pending dictionary.
            var pending = new List<KeyValuePair<int, string>>(_pendingRegistrations);
            _pendingRegistrations.Clear();
            foreach (var kvp in pending)
            {
                Register(kvp.Key, kvp.Value);
            }
        }
    }

    public void Register(int id, string hotkey)
    {
        if (!OperatingSystem.IsWindows()) return;
        
        _hotkeyStrings[id] = hotkey;

        if (_isSuspended)
        {
            _suspendedHotkeys[id] = hotkey;
            System.Diagnostics.Debug.WriteLine($"[GlobalHotkey] Queueing registration for ID {id} while suspended: {hotkey}");
            return;
        }

        if (_handle == IntPtr.Zero)
        {
            _pendingRegistrations[id] = hotkey;
            return;
        }
        
        Unregister(id);

        (uint modifiers, uint vkey) = ParseHotkey(hotkey);

        if (vkey == 0)
        {
            System.Diagnostics.Debug.WriteLine($"[GlobalHotkey] Unsupported hotkey '{hotkey}' for ID {id}.");
            return;
        }

        bool success = NativeRegisterHotKey(_handle, id, modifiers, vkey);
        if (success)
        {
            _registeredIds.Add(id);
            
            if (_oldWndProc == IntPtr.Zero)
            {
                InstallWndProcHook();
            }
        }
        else
        {
            int error = Marshal.GetLastWin32Error();
            System.Diagnostics.Debug.WriteLine($"[GlobalHotkey] Failed to register ID {id} as '{hotkey}' (mods={modifiers}, vk={vkey}, win32={error}).");
            OnHotkeyRegistrationFailed?.Invoke(id, hotkey, error);
        }
    }
    
    public void Unregister(int id)
    {
        if (_handle != IntPtr.Zero && _registeredIds.Contains(id))
        {
            NativeUnregisterHotKey(_handle, id);
            _registeredIds.Remove(id);
        }
        _pendingRegistrations.Remove(id);
        if (_isSuspended)
        {
            _suspendedHotkeys.Remove(id);
        }
    }

    private readonly Dictionary<int, string> _hotkeyStrings = new();
    private readonly Dictionary<int, string> _suspendedHotkeys = new();

    public void SuspendAll()
    {
        if (_handle == IntPtr.Zero) return;
        _isSuspended = true;
        _suspendedHotkeys.Clear();
        foreach (var id in new List<int>(_registeredIds))
        {
            if (_hotkeyStrings.TryGetValue(id, out var hk))
                _suspendedHotkeys[id] = hk;
            
            NativeUnregisterHotKey(_handle, id);
        }
        _registeredIds.Clear();
        System.Diagnostics.Debug.WriteLine($"[GlobalHotkey] Suspended {_suspendedHotkeys.Count} hotkeys");
    }

    public void ResumeAll()
    {
        if (_handle == IntPtr.Zero) return;
        
        _isSuspended = false;
        var toResume = new Dictionary<int, string>(_suspendedHotkeys);
        _suspendedHotkeys.Clear();

        foreach (var kvp in toResume)
        {
            // Always use the latest value from _hotkeyStrings if it exists
            string valueToRegister = _hotkeyStrings.TryGetValue(kvp.Key, out var current) ? current : kvp.Value;
            Register(kvp.Key, valueToRegister);
        }
        
        System.Diagnostics.Debug.WriteLine($"[GlobalHotkey] Resumed {toResume.Count} hotkeys");
    }

    private void UnregisterAll()
    {
        if (_handle == IntPtr.Zero) return;
        foreach (var id in new List<int>(_registeredIds))
        {
            UnregisterHotKey(_handle, id);
        }
        _registeredIds.Clear();
        _pendingRegistrations.Clear();
    }

    private (uint mods, uint vkey) ParseHotkey(string hk)
    {
        ReadOnlySpan<char> hotkey = hk.AsSpan().Trim();
        uint mods = 0;

        if (hotkey.Contains("Ctrl".AsSpan(), StringComparison.OrdinalIgnoreCase)) mods |= 0x0002;
        if (hotkey.Contains("Alt".AsSpan(), StringComparison.OrdinalIgnoreCase)) mods |= 0x0001;
        if (hotkey.Contains("Shift".AsSpan(), StringComparison.OrdinalIgnoreCase)) mods |= 0x0004;

        int plusIndex = hotkey.LastIndexOf('+');
        ReadOnlySpan<char> keyPart = plusIndex >= 0 ? hotkey[(plusIndex + 1)..].Trim() : hotkey;

        uint key = 0;
        if (keyPart.Length > 1 && (keyPart[0] is 'F' or 'f') && int.TryParse(keyPart[1..], out int fNum))
        {
            if (fNum >= 1 && fNum <= 24)
                key = (uint)(0x70 + fNum - 1);
        }
        else if (keyPart.Equals("PRINTSCREEN".AsSpan(), StringComparison.OrdinalIgnoreCase) || keyPart.Equals("PRTSC".AsSpan(), StringComparison.OrdinalIgnoreCase))
        {
            key = 0x2C;
        }
        else if (keyPart.Equals("ENTER".AsSpan(), StringComparison.OrdinalIgnoreCase) || keyPart.Equals("RETURN".AsSpan(), StringComparison.OrdinalIgnoreCase))
        {
            key = 0x0D;
        }
        else if (keyPart.Equals("ESC".AsSpan(), StringComparison.OrdinalIgnoreCase) || keyPart.Equals("ESCAPE".AsSpan(), StringComparison.OrdinalIgnoreCase))
        {
            key = 0x1B;
        }
        else if (keyPart.Equals("TAB".AsSpan(), StringComparison.OrdinalIgnoreCase))
        {
            key = 0x09;
        }
        else if (keyPart.Equals("SPACE".AsSpan(), StringComparison.OrdinalIgnoreCase))
        {
            key = 0x20;
        }
        else if (keyPart.Equals("DELETE".AsSpan(), StringComparison.OrdinalIgnoreCase) || keyPart.Equals("DEL".AsSpan(), StringComparison.OrdinalIgnoreCase))
        {
            key = 0x2E;
        }
        else if (keyPart.Equals("INSERT".AsSpan(), StringComparison.OrdinalIgnoreCase) || keyPart.Equals("INS".AsSpan(), StringComparison.OrdinalIgnoreCase))
        {
            key = 0x2D;
        }
        else if (keyPart.Equals("BACKSPACE".AsSpan(), StringComparison.OrdinalIgnoreCase) || keyPart.Equals("BKSP".AsSpan(), StringComparison.OrdinalIgnoreCase))
        {
            key = 0x08;
        }
        else if (keyPart.Equals("HOME".AsSpan(), StringComparison.OrdinalIgnoreCase))
        {
            key = 0x24;
        }
        else if (keyPart.Equals("END".AsSpan(), StringComparison.OrdinalIgnoreCase))
        {
            key = 0x23;
        }
        else if (keyPart.Equals("PAGEUP".AsSpan(), StringComparison.OrdinalIgnoreCase) || keyPart.Equals("PGUP".AsSpan(), StringComparison.OrdinalIgnoreCase))
        {
            key = 0x21;
        }
        else if (keyPart.Equals("PAGEDOWN".AsSpan(), StringComparison.OrdinalIgnoreCase) || keyPart.Equals("PGDN".AsSpan(), StringComparison.OrdinalIgnoreCase))
        {
            key = 0x22;
        }
        else if (keyPart.Equals("LEFT".AsSpan(), StringComparison.OrdinalIgnoreCase))
        {
            key = 0x25;
        }
        else if (keyPart.Equals("UP".AsSpan(), StringComparison.OrdinalIgnoreCase))
        {
            key = 0x26;
        }
        else if (keyPart.Equals("RIGHT".AsSpan(), StringComparison.OrdinalIgnoreCase))
        {
            key = 0x27;
        }
        else if (keyPart.Equals("DOWN".AsSpan(), StringComparison.OrdinalIgnoreCase))
        {
            key = 0x28;
        }
        else if (keyPart.Length == 1 && char.IsLetterOrDigit(keyPart[0]))
        {
             key = char.ToUpperInvariant(keyPart[0]);
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
            if (_registeredIds.Contains(id))
            {
                try
                {
                    OnHotkeyPressed?.Invoke(id);
                }
                catch (System.Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Error in hotkey callback: {ex}");
                }
            }
        }
        
        return CallWindowProc(_oldWndProc, hWnd, msg, wParam, lParam);
    }

    public void Dispose()
    {
        UnregisterAll();
        RemoveWndProcHook();
    }
}
