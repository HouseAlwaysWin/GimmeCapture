using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Platform;

namespace GimmeCapture.Services;

public class GlobalHotkeyService : IGlobalHotkeyService
{
    private const int WM_HOTKEY = 0x0312;
    private IntPtr _handle;
    private readonly HashSet<int> _registeredIds = new();
    
    // Action to fire when hotkey is pressed, passing the ID
    public Action<int>? OnHotkeyPressed { get; set; }
    
    private IntPtr _oldWndProc = IntPtr.Zero;
    private WndProc? _newWndProc; // Keep reference to prevent GC

    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private delegate IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    
    public void Initialize(Window window)
    {
        if (!OperatingSystem.IsWindows()) return;

        var platformHandle = window.TryGetPlatformHandle();
        if (platformHandle != null)
        {
            _handle = platformHandle.Handle;
        }
    }

    public void Register(int id, string hotkey)
    {
        if (!OperatingSystem.IsWindows() || _handle == IntPtr.Zero) return;
        
        Unregister(id);

        (uint modifiers, uint vkey) = ParseHotkey(hotkey);

        if (vkey != 0)
        {
             bool success = RegisterHotKey(_handle, id, modifiers, vkey);
             if (success)
             {
                 _registeredIds.Add(id);
                 
                 if (_oldWndProc == IntPtr.Zero)
                 {
                     InstallWndProcHook();
                 }
             }
        }
    }
    
    public void Unregister(int id)
    {
        if (_handle != IntPtr.Zero && _registeredIds.Contains(id))
        {
            UnregisterHotKey(_handle, id);
            _registeredIds.Remove(id);
        }
    }

    private void UnregisterAll()
    {
        if (_handle == IntPtr.Zero) return;
        foreach (var id in new List<int>(_registeredIds))
        {
            UnregisterHotKey(_handle, id);
        }
        _registeredIds.Clear();
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
