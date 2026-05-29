using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using GimmeCapture.Services.Abstractions;
using Avalonia.Controls;
using Avalonia.Platform;

namespace GimmeCapture.Services.Platforms.Windows;

public class WindowsGlobalHotkeyService : IGlobalHotkeyService
{
    private const int WM_HOTKEY = 0x0312;
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_KEYUP = 0x0101;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int WM_SYSKEYUP = 0x0105;
    private const int WM_INPUT = 0x00FF;
    private const uint RID_INPUT = 0x10000003;
    private const uint RIDEV_INPUTSINK = 0x00000100;
    private const uint RIM_TYPEKEYBOARD = 1;
    private IntPtr _handle;
    private readonly HashSet<int> _registeredIds = new();
    private readonly Dictionary<int, string> _pendingRegistrations = new();
    private readonly Dictionary<int, (uint Mods, uint Key)> _registeredCombos = new();
    private readonly HashSet<int> _pressedVirtualKeys = new();
    private readonly Dictionary<int, long> _lastHotkeyInvokeTicks = new();
    private bool _isSuspended;
    private bool _llShiftDown;
    private bool _llCtrlDown;
    private bool _llAltDown;
    private readonly string _messageWindowClassName = $"GimmeCaptureHotkeyMessageWindow_{Environment.ProcessId}_{Guid.NewGuid():N}";
    private readonly string _debugLogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "GimmeCapture",
        "hotkey-debug.log");
    private ushort _messageWindowClassAtom;
    private IntPtr _messageWindowInstance = IntPtr.Zero;
    
    // Action to fire when hotkey is pressed, passing the ID
    public Action<int>? OnHotkeyPressed { get; set; }
    public Action<int, string, int>? OnHotkeyRegistrationFailed { get; set; }
    public Action? OnElevatedWindowFocused { get; set; }
    
    private IntPtr _oldWndProc = IntPtr.Zero;
    private WndProc? _newWndProc; // Keep reference to prevent GC
    private WndProc? _messageWndProc;
    private IntPtr _llKeyboardHook = IntPtr.Zero;
    private LowLevelKeyboardProc? _llKeyboardDelegate;

    // Foreground monitoring used to warn when an elevated window blocks our hotkeys.
    private IntPtr _foregroundEventHook = IntPtr.Zero;
    private WinEventDelegate? _foregroundEventDelegate;
    private bool _selfIsElevated;
    private int _selfIntegrityLevel;
    private bool _selfIntegrityResolved;
    private bool _previousForegroundWasElevated;
    private long _lastElevationNoticeTicks;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public uint cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string? lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClassEx(ref WNDCLASSEX lpwcx);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowEx(
        uint dwExStyle,
        string lpClassName,
        string lpWindowName,
        uint dwStyle,
        int x,
        int y,
        int nWidth,
        int nHeight,
        IntPtr hWndParent,
        IntPtr hMenu,
        IntPtr hInstance,
        IntPtr lpParam);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterClass(string lpClassName, IntPtr hInstance);

    [DllImport("user32.dll", CallingConvention = CallingConvention.StdCall)]
    private static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    private static readonly IntPtr HwndMessage = new(-3);

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUTDEVICE
    {
        public ushort usUsagePage;
        public ushort usUsage;
        public uint dwFlags;
        public IntPtr hwndTarget;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUTHEADER
    {
        public uint dwType;
        public uint dwSize;
        public IntPtr hDevice;
        public IntPtr wParam;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWKEYBOARD
    {
        public ushort MakeCode;
        public ushort Flags;
        public ushort Reserved;
        public ushort VKey;
        public uint Message;
        public uint ExtraInformation;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUTKEYBOARD
    {
        public RAWINPUTHEADER Header;
        public RAWKEYBOARD Keyboard;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterRawInputDevices(
        [In] RAWINPUTDEVICE[] pRawInputDevices,
        uint uiNumDevices,
        uint cbSize);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetRawInputData(
        IntPtr hRawInput,
        uint uiCommand,
        IntPtr pData,
        ref uint pcbSize,
        uint cbSizeHeader);

    protected virtual bool NativeRegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vkey)
    {
        return RegisterHotKey(hWnd, id, fsModifiers, vkey);
    }

    protected virtual bool NativeUnregisterHotKey(IntPtr hWnd, int id)
    {
        return UnregisterHotKey(hWnd, id);
    }

    private delegate IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
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

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    private const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    private const uint WINEVENT_OUTOFCONTEXT = 0x0000;
    private const uint WINEVENT_SKIPOWNPROCESS = 0x0002;
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private const uint TOKEN_QUERY = 0x0008;
    private const int TokenIntegrityLevel = 25;
    private const int SECURITY_MANDATORY_MEDIUM_RID = 0x2000;
    private const int ERROR_ACCESS_DENIED = 5;

    private delegate void WinEventDelegate(IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmodWinEventProc, WinEventDelegate lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWinEvent(IntPtr hWinEventHook);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint dwDesiredAccess, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, uint dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(IntPtr ProcessHandle, uint DesiredAccess, out IntPtr TokenHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(IntPtr TokenHandle, int TokenInformationClass, IntPtr TokenInformation, uint TokenInformationLength, out uint ReturnLength);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern IntPtr GetSidSubAuthority(IntPtr pSid, uint nSubAuthority);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern IntPtr GetSidSubAuthorityCount(IntPtr pSid);

    public void Initialize(Window window)
    {
        if (!OperatingSystem.IsWindows()) return;

        EnsureMessageWindow();
        InstallLowLevelKeyboardHook();
        InstallForegroundMonitor();

        if (_handle != IntPtr.Zero)
        {
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
            _registeredCombos.Remove(id);
            System.Diagnostics.Debug.WriteLine($"[GlobalHotkey] Unsupported hotkey '{hotkey}' for ID {id}.");
            return;
        }

        _registeredCombos[id] = (modifiers, vkey);

        bool success = NativeRegisterHotKey(_handle, id, modifiers, vkey);
        if (success)
        {
            _registeredIds.Add(id);
            WriteDebugLog($"registered id={id} hotkey='{hotkey}' mods={modifiers} vk={vkey} handle=0x{_handle.ToInt64():X}");
        }
        else
        {
            int error = Marshal.GetLastWin32Error();
            System.Diagnostics.Debug.WriteLine($"[GlobalHotkey] Failed to register ID {id} as '{hotkey}' (mods={modifiers}, vk={vkey}, win32={error}).");
            WriteDebugLog($"register failed id={id} hotkey='{hotkey}' mods={modifiers} vk={vkey} win32={error} handle=0x{_handle.ToInt64():X}");
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
        _registeredCombos.Remove(id);
        _lastHotkeyInvokeTicks.Remove(id);
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
        _registeredCombos.Clear();
        _lastHotkeyInvokeTicks.Clear();
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
        else if (keyPart.Equals("PRINTSCREEN".AsSpan(), StringComparison.OrdinalIgnoreCase)
            || keyPart.Equals("PRTSC".AsSpan(), StringComparison.OrdinalIgnoreCase)
            || keyPart.Equals("PRINT".AsSpan(), StringComparison.OrdinalIgnoreCase))
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
                InvokeHotkey(id, "wm_hotkey");
            }
        }
        
        return CallWindowProc(_oldWndProc, hWnd, msg, wParam, lParam);
    }

    private void EnsureMessageWindow()
    {
        if (_handle != IntPtr.Zero)
        {
            return;
        }

        _messageWndProc = MessageWindowProc;
        using var curProcess = System.Diagnostics.Process.GetCurrentProcess();
        using var curModule = curProcess.MainModule;
        if (curModule?.ModuleName == null)
        {
            return;
        }

        _messageWindowInstance = GetModuleHandle(curModule.ModuleName);
        var wndClass = new WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_messageWndProc),
            hInstance = _messageWindowInstance,
            lpszClassName = _messageWindowClassName
        };

        _messageWindowClassAtom = RegisterClassEx(ref wndClass);
        if (_messageWindowClassAtom == 0)
        {
            System.Diagnostics.Debug.WriteLine($"[GlobalHotkey] RegisterClassEx failed: {Marshal.GetLastWin32Error()}");
            return;
        }

        _handle = CreateWindowEx(
            0,
            _messageWindowClassName,
            string.Empty,
            0,
            0,
            0,
            0,
            0,
            HwndMessage,
            IntPtr.Zero,
            _messageWindowInstance,
            IntPtr.Zero);

        if (_handle == IntPtr.Zero)
        {
            System.Diagnostics.Debug.WriteLine($"[GlobalHotkey] Create message window failed: {Marshal.GetLastWin32Error()}");
            WriteDebugLog($"message window create failed win32={Marshal.GetLastWin32Error()}");
        }
        else
        {
            System.Diagnostics.Debug.WriteLine($"[GlobalHotkey] Message window created: 0x{_handle.ToInt64():X}");
            WriteDebugLog($"message window created handle=0x{_handle.ToInt64():X}");
            RegisterRawKeyboardSink();
        }
    }

    private IntPtr MessageWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WM_HOTKEY)
        {
            int id = wParam.ToInt32();
            if (_registeredIds.Contains(id))
            {
                InvokeHotkey(id, "message_window");
            }
        }
        else if (msg == WM_INPUT)
        {
            HandleRawKeyboardInput(lParam);
        }

        return DefWindowProc(hWnd, msg, wParam, lParam);
    }

    private void RegisterRawKeyboardSink()
    {
        var devices = new[]
        {
            new RAWINPUTDEVICE
            {
                usUsagePage = 0x01,
                usUsage = 0x06,
                dwFlags = RIDEV_INPUTSINK,
                hwndTarget = _handle
            }
        };

        bool success = RegisterRawInputDevices(devices, (uint)devices.Length, (uint)Marshal.SizeOf<RAWINPUTDEVICE>());
        WriteDebugLog(success
            ? $"raw input keyboard sink registered handle=0x{_handle.ToInt64():X}"
            : $"raw input keyboard sink failed win32={Marshal.GetLastWin32Error()} handle=0x{_handle.ToInt64():X}");
    }

    private void HandleRawKeyboardInput(IntPtr rawInputHandle)
    {
        uint size = 0;
        uint headerSize = (uint)Marshal.SizeOf<RAWINPUTHEADER>();
        GetRawInputData(rawInputHandle, RID_INPUT, IntPtr.Zero, ref size, headerSize);
        if (size == 0 || size > 512)
        {
            return;
        }

        IntPtr buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            uint copied = GetRawInputData(rawInputHandle, RID_INPUT, buffer, ref size, headerSize);
            if (copied != size)
            {
                return;
            }

            var input = Marshal.PtrToStructure<RAWINPUTKEYBOARD>(buffer);
            if (input.Header.dwType != RIM_TYPEKEYBOARD)
            {
                return;
            }

            int vkCode = input.Keyboard.VKey;
            if (vkCode == 0 || vkCode == 0xFF)
            {
                return;
            }

            int msg = unchecked((int)input.Keyboard.Message);
            bool isKeyDown = msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN;
            bool isKeyUp = msg == WM_KEYUP || msg == WM_SYSKEYUP;
            ProcessFallbackKeyboardEvent(vkCode, isKeyDown, isKeyUp, "raw_input");
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private void InstallLowLevelKeyboardHook()
    {
        if (_llKeyboardHook != IntPtr.Zero)
        {
            return;
        }

        _llKeyboardDelegate = LowLevelKeyboardHookCallback;
        using var curProcess = System.Diagnostics.Process.GetCurrentProcess();
        using var curModule = curProcess.MainModule;
        if (curModule?.ModuleName == null)
        {
            return;
        }

        var moduleHandle = GetModuleHandle(curModule.ModuleName);
        _llKeyboardHook = SetWindowsHookEx(WH_KEYBOARD_LL, _llKeyboardDelegate, moduleHandle, 0);
        if (_llKeyboardHook == IntPtr.Zero)
        {
            int firstError = Marshal.GetLastWin32Error();
            _llKeyboardHook = SetWindowsHookEx(WH_KEYBOARD_LL, _llKeyboardDelegate, IntPtr.Zero, 0);
            WriteDebugLog($"ll hook install retry firstWin32={firstError} retryHandle=0x{_llKeyboardHook.ToInt64():X} retryWin32={Marshal.GetLastWin32Error()} module=0x{moduleHandle.ToInt64():X}");
        }
        else
        {
            WriteDebugLog($"ll hook installed handle=0x{_llKeyboardHook.ToInt64():X} module=0x{moduleHandle.ToInt64():X}");
        }
    }

    private void InstallForegroundMonitor()
    {
        if (_foregroundEventHook != IntPtr.Zero)
        {
            return;
        }

        ResolveSelfIntegrity();

        // If we are already elevated we can capture any window, so the hint is never needed.
        if (_selfIsElevated)
        {
            return;
        }

        _foregroundEventDelegate = ForegroundEventCallback;
        _foregroundEventHook = SetWinEventHook(
            EVENT_SYSTEM_FOREGROUND,
            EVENT_SYSTEM_FOREGROUND,
            IntPtr.Zero,
            _foregroundEventDelegate,
            0,
            0,
            WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);

        WriteDebugLog(_foregroundEventHook != IntPtr.Zero
            ? $"foreground monitor installed hook=0x{_foregroundEventHook.ToInt64():X} selfIntegrity=0x{_selfIntegrityLevel:X}"
            : $"foreground monitor install failed win32={Marshal.GetLastWin32Error()}");
    }

    private void RemoveForegroundMonitor()
    {
        if (_foregroundEventHook == IntPtr.Zero)
        {
            return;
        }

        UnhookWinEvent(_foregroundEventHook);
        _foregroundEventHook = IntPtr.Zero;
        _foregroundEventDelegate = null;
    }

    private void ForegroundEventCallback(IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
    {
        if (eventType != EVENT_SYSTEM_FOREGROUND || hwnd == IntPtr.Zero)
        {
            return;
        }

        bool isElevated = IsWindowHigherIntegrity(hwnd);

        // Only react on the transition into an elevated window so users are not nagged repeatedly
        // while the same window keeps focus.
        bool transitionedIntoElevated = isElevated && !_previousForegroundWasElevated;
        _previousForegroundWasElevated = isElevated;

        if (!transitionedIntoElevated)
        {
            return;
        }

        long now = Environment.TickCount64;
        if (_lastElevationNoticeTicks != 0 && now - _lastElevationNoticeTicks < 60_000)
        {
            return;
        }

        _lastElevationNoticeTicks = now;
        WriteDebugLog("elevated foreground window detected; raising hotkey-blocked notice");

        try
        {
            OnElevatedWindowFocused?.Invoke();
        }
        catch (Exception ex)
        {
            WriteDebugLog($"elevated notice callback error={ex}");
        }
    }

    private bool IsWindowHigherIntegrity(IntPtr hwnd)
    {
        uint pid;
        if (GetWindowThreadProcessId(hwnd, out pid) == 0 || pid == 0)
        {
            return false;
        }

        IntPtr hProcess = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (hProcess == IntPtr.Zero)
        {
            // A flat access-denied when merely opening for limited query is a strong signal that
            // the target is a higher-integrity / protected process we cannot capture.
            return Marshal.GetLastWin32Error() == ERROR_ACCESS_DENIED;
        }

        IntPtr hToken = IntPtr.Zero;
        try
        {
            if (!OpenProcessToken(hProcess, TOKEN_QUERY, out hToken))
            {
                return Marshal.GetLastWin32Error() == ERROR_ACCESS_DENIED;
            }

            int targetIntegrity = GetTokenIntegrityLevel(hToken);
            if (targetIntegrity < 0)
            {
                return false;
            }

            return targetIntegrity > _selfIntegrityLevel;
        }
        finally
        {
            if (hToken != IntPtr.Zero) CloseHandle(hToken);
            CloseHandle(hProcess);
        }
    }

    private void ResolveSelfIntegrity()
    {
        if (_selfIntegrityResolved)
        {
            return;
        }

        _selfIntegrityResolved = true;
        _selfIntegrityLevel = SECURITY_MANDATORY_MEDIUM_RID;

        if (OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, out IntPtr hToken))
        {
            try
            {
                int level = GetTokenIntegrityLevel(hToken);
                if (level >= 0)
                {
                    _selfIntegrityLevel = level;
                }
            }
            finally
            {
                CloseHandle(hToken);
            }
        }

        // High (0x3000) or System (0x4000) integrity means we are effectively elevated.
        _selfIsElevated = _selfIntegrityLevel > SECURITY_MANDATORY_MEDIUM_RID;
    }

    private static int GetTokenIntegrityLevel(IntPtr hToken)
    {
        uint size = 0;
        GetTokenInformation(hToken, TokenIntegrityLevel, IntPtr.Zero, 0, out size);
        if (size == 0)
        {
            return -1;
        }

        IntPtr buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            if (!GetTokenInformation(hToken, TokenIntegrityLevel, buffer, size, out size))
            {
                return -1;
            }

            // TOKEN_MANDATORY_LABEL { SID_AND_ATTRIBUTES Label }; Label.Sid is the first pointer.
            IntPtr pSid = Marshal.ReadIntPtr(buffer);
            if (pSid == IntPtr.Zero)
            {
                return -1;
            }

            IntPtr pCount = GetSidSubAuthorityCount(pSid);
            if (pCount == IntPtr.Zero)
            {
                return -1;
            }

            int subAuthorityCount = Marshal.ReadByte(pCount);
            if (subAuthorityCount == 0)
            {
                return -1;
            }

            IntPtr pLast = GetSidSubAuthority(pSid, (uint)(subAuthorityCount - 1));
            if (pLast == IntPtr.Zero)
            {
                return -1;
            }

            return Marshal.ReadInt32(pLast);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private void RemoveLowLevelKeyboardHook()
    {
        if (_llKeyboardHook == IntPtr.Zero)
        {
            return;
        }

        UnhookWindowsHookEx(_llKeyboardHook);
        _llKeyboardHook = IntPtr.Zero;
        _llKeyboardDelegate = null;
        _pressedVirtualKeys.Clear();
        _llShiftDown = false;
        _llCtrlDown = false;
        _llAltDown = false;
    }

    private IntPtr LowLevelKeyboardHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode < 0)
        {
            return CallNextHookEx(_llKeyboardHook, nCode, wParam, lParam);
        }

        int msg = wParam.ToInt32();
        int vkCode = Marshal.ReadInt32(lParam);
        bool isKeyDown = msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN;
        bool isKeyUp = msg == WM_KEYUP || msg == WM_SYSKEYUP;

        if (ProcessFallbackKeyboardEvent(vkCode, isKeyDown, isKeyUp, "ll_hook"))
        {
            return new IntPtr(1);
        }

        return CallNextHookEx(_llKeyboardHook, nCode, wParam, lParam);
    }

    private bool ProcessFallbackKeyboardEvent(int vkCode, bool isKeyDown, bool isKeyUp, string source)
    {
        if (UpdateModifierState(vkCode, isKeyDown, isKeyUp))
        {
            return false;
        }

        if (isKeyUp)
        {
            _pressedVirtualKeys.Remove(vkCode);
            return false;
        }

        if (!isKeyDown || IsModifierVirtualKey(vkCode))
        {
            return false;
        }

        if (!_pressedVirtualKeys.Add(vkCode))
        {
            return false;
        }

        uint modifiers = GetCurrentModifierMask();
        bool isInterestingKey = IsRegisteredVirtualKey((uint)vkCode);
        if (isInterestingKey)
        {
            WriteDebugLog($"{source} keydown vk={vkCode} mods={modifiers} registered={_registeredCombos.Count} shift={_llShiftDown} ctrl={_llCtrlDown} alt={_llAltDown}");
        }

        foreach (var kvp in _registeredCombos)
        {
            if (kvp.Value.Key != (uint)vkCode || kvp.Value.Mods != modifiers)
            {
                continue;
            }

            if (InvokeHotkey(kvp.Key, source))
            {
                return true;
            }
        }

        return false;
    }

    private bool IsRegisteredVirtualKey(uint vkCode)
    {
        foreach (var combo in _registeredCombos.Values)
        {
            if (combo.Key == vkCode)
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsModifierVirtualKey(int vkCode)
    {
        return vkCode is 0x10 or 0xA0 or 0xA1
            or 0x11 or 0xA2 or 0xA3
            or 0x12 or 0xA4 or 0xA5;
    }

    private bool UpdateModifierState(int vkCode, bool isKeyDown, bool isKeyUp)
    {
        if (!IsModifierVirtualKey(vkCode))
        {
            return false;
        }

        bool isDown = isKeyDown && !isKeyUp;
        switch (vkCode)
        {
            case 0x10:
            case 0xA0:
            case 0xA1:
                _llShiftDown = isDown;
                break;
            case 0x11:
            case 0xA2:
            case 0xA3:
                _llCtrlDown = isDown;
                break;
            case 0x12:
            case 0xA4:
            case 0xA5:
                _llAltDown = isDown;
                break;
        }

        return true;
    }

    private uint GetCurrentModifierMask()
    {
        uint modifiers = 0;
        if (_llShiftDown || (GetAsyncKeyState(0x10) & 0x8000) != 0) modifiers |= 0x0004;
        if (_llCtrlDown || (GetAsyncKeyState(0x11) & 0x8000) != 0) modifiers |= 0x0002;
        if (_llAltDown || (GetAsyncKeyState(0x12) & 0x8000) != 0) modifiers |= 0x0001;
        return modifiers;
    }

    private bool InvokeHotkey(int id, string source)
    {
        long now = Environment.TickCount64;
        if (_lastHotkeyInvokeTicks.TryGetValue(id, out long lastTick) && now - lastTick < 150)
        {
            return false;
        }

        _lastHotkeyInvokeTicks[id] = now;
        System.Diagnostics.Debug.WriteLine($"[GlobalHotkey] Triggered ID {id} via {source}");
        WriteDebugLog($"triggered id={id} source={source}");

        try
        {
            OnHotkeyPressed?.Invoke(id);
            return true;
        }
        catch (System.Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error in hotkey callback: {ex}");
            WriteDebugLog($"callback error id={id} source={source} error={ex}");
            return false;
        }
    }

    private void WriteDebugLog(string message)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_debugLogPath)!);
            File.AppendAllText(
                _debugLogPath,
                $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}");
        }
        catch
        {
            // Diagnostics must never interfere with global hotkey routing.
        }
    }

    public void Dispose()
    {
        UnregisterAll();
        RemoveWndProcHook();
        RemoveLowLevelKeyboardHook();
        RemoveForegroundMonitor();
        if (_handle != IntPtr.Zero)
        {
            DestroyWindow(_handle);
            _handle = IntPtr.Zero;
        }

        if (_messageWindowClassAtom != 0 && _messageWindowInstance != IntPtr.Zero)
        {
            UnregisterClass(_messageWindowClassName, _messageWindowInstance);
            _messageWindowClassAtom = 0;
            _messageWindowInstance = IntPtr.Zero;
        }
    }
}
