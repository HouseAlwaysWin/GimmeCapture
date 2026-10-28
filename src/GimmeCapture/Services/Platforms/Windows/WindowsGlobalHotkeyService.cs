using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using GimmeCapture.Services.Abstractions;
using GimmeCapture.Services.Core.Infrastructure;
using GimmeCapture.Services.Interop;
using Avalonia.Controls;
using Avalonia.Threading;

namespace GimmeCapture.Services.Platforms.Windows;

/// <summary>
/// Global hotkeys on Windows, from three sources: RegisterHotKey (WM_HOTKEY), a low-level keyboard hook that also
/// covers combos another app has registered, and a raw-input sink. All three, and the foreground monitor behind the
/// elevated-window notice, live on a <see cref="Win32HotkeyThread"/> and so does every field they touch: public
/// calls hand their work to that thread, and presses go to the UI thread by posting. Before
/// <see cref="Initialize"/> and after <see cref="Dispose"/> there is no thread, and calls run where they are made.
/// </summary>
public class WindowsGlobalHotkeyService : IGlobalHotkeyService
{
    // A public call waits this long for the hotkey thread, which only ever runs short native calls.
    private static readonly TimeSpan HotkeyThreadCallTimeout = TimeSpan.FromMilliseconds(500);
    private const uint HookLateThresholdMs = 100;

    private const int WM_HOTKEY = 0x0312;
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
    private readonly LowLevelHotkeyMatcher _matcher = new();
    private bool _isSuspended;
    private readonly string _messageWindowClassName = $"GimmeCaptureHotkeyMessageWindow_{Environment.ProcessId}_{Guid.NewGuid():N}";
    private ushort _messageWindowClassAtom;
    private IntPtr _messageWindowInstance = IntPtr.Zero;

    private readonly Action<Action> _postToUi;
    private Win32HotkeyThread? _hotkeyThread;
    private long _lowLevelHookCalls;
    private long _lastHookLateLogTicks;

    public Action<HotkeyPress>? OnHotkeyPressed { get; set; }
    public Action<int, string, int>? OnHotkeyRegistrationFailed { get; set; }
    public Action<string>? OnElevatedWindowFocused { get; set; }

    // The delegates native code calls back are kept in fields so they live as long as the service.
    private User32.WndProc? _messageWndProc;
    private IntPtr _llKeyboardHook = IntPtr.Zero;
    private User32.LowLevelKeyboardProc? _llKeyboardDelegate;

    // Foreground monitoring used to warn when an elevated window blocks our hotkeys.
    private IntPtr _foregroundEventHook = IntPtr.Zero;
    private WinEventDelegate? _foregroundEventDelegate;
    private bool _selfIsElevated;
    private int _selfIntegrityLevel;
    private bool _selfIntegrityResolved;
    private readonly HashSet<uint> _notifiedElevatedPids = new();
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

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint dwDesiredAccess, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, uint dwProcessId);

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

    public WindowsGlobalHotkeyService()
        : this(action => Dispatcher.UIThread.Post(action))
    {
    }

    /// <param name="postToUi">How a press reaches the UI thread; tests pass their own.</param>
    internal WindowsGlobalHotkeyService(Action<Action> postToUi)
    {
        _postToUi = postToUi;
    }

    public void Initialize(Window window)
    {
        if (!OperatingSystem.IsWindows()) return;

        if (_hotkeyThread == null)
        {
            var thread = new Win32HotkeyThread("GimmeCapture.Hotkeys");
            if (!thread.Start(InstallNativeHooks, TimeSpan.FromSeconds(5)))
            {
                AppLog.Warning("GlobalHotkey.ThreadStart", "The hotkey thread did not finish starting within 5 s.");
            }

            _hotkeyThread = thread;
        }

        foreach (var kvp in RunOnHotkeyThread(TakePendingRegistrations, new List<KeyValuePair<int, string>>()))
        {
            Register(kvp.Key, kvp.Value);
        }
    }

    // Runs on the hotkey thread as it starts.
    private void InstallNativeHooks()
    {
        EnsureMessageWindow();
        InstallLowLevelKeyboardHook();
        InstallForegroundMonitor();
    }

    private List<KeyValuePair<int, string>> TakePendingRegistrations()
    {
        // Taken as a snapshot because Register mutates the pending dictionary.
        var pending = new List<KeyValuePair<int, string>>();
        if (_handle != IntPtr.Zero)
        {
            pending.AddRange(_pendingRegistrations);
            _pendingRegistrations.Clear();
        }

        return pending;
    }

    public void Register(int id, string hotkey)
    {
        if (!OperatingSystem.IsWindows()) return;

        int? failure = RunOnHotkeyThread(() => RegisterCore(id, hotkey), (int?)null);
        if (failure is int win32Error)
        {
            OnHotkeyRegistrationFailed?.Invoke(id, hotkey, win32Error);
        }
    }

    // The Win32 error when RegisterHotKey refused the combo (the hook still serves it), otherwise null.
    private int? RegisterCore(int id, string hotkey)
    {
        _hotkeyStrings[id] = hotkey;

        if (_isSuspended)
        {
            _suspendedHotkeys[id] = hotkey;
            AppLog.Debug("WindowsGlobalHotkeyService", $"[GlobalHotkey] Queueing registration for ID {id} while suspended: {hotkey}");
            return null;
        }

        if (_handle == IntPtr.Zero)
        {
            _pendingRegistrations[id] = hotkey;
            return null;
        }

        UnregisterCore(id);

        (uint modifiers, uint vkey) = ParseHotkey(hotkey);

        if (vkey == 0)
        {
            _registeredCombos.Remove(id);
            AppLog.Debug("WindowsGlobalHotkeyService", $"[GlobalHotkey] Unsupported hotkey '{hotkey}' for ID {id}.");
            return null;
        }

        _registeredCombos[id] = (modifiers, vkey);

        if (NativeRegisterHotKey(_handle, id, modifiers, vkey))
        {
            _registeredIds.Add(id);
            WriteDebugLog($"registered id={id} hotkey='{hotkey}' mods={modifiers} vk={vkey} handle=0x{_handle.ToInt64():X}");
            return null;
        }

        int error = Marshal.GetLastWin32Error();
        AppLog.Debug("WindowsGlobalHotkeyService", $"[GlobalHotkey] Failed to register ID {id} as '{hotkey}' (mods={modifiers}, vk={vkey}, win32={error}).");
        WriteDebugLog($"register failed id={id} hotkey='{hotkey}' mods={modifiers} vk={vkey} win32={error} handle=0x{_handle.ToInt64():X}");
        return error;
    }

    public void Unregister(int id) => RunOnHotkeyThread(() => UnregisterCore(id));

    private void UnregisterCore(int id)
    {
        if (_handle != IntPtr.Zero && _registeredIds.Contains(id))
        {
            NativeUnregisterHotKey(_handle, id);
            _registeredIds.Remove(id);
        }
        _registeredCombos.Remove(id);
        _matcher.Forget(id);
        _pendingRegistrations.Remove(id);
        if (_isSuspended)
        {
            _suspendedHotkeys.Remove(id);
        }
    }

    private readonly Dictionary<int, string> _hotkeyStrings = new();
    private readonly Dictionary<int, string> _suspendedHotkeys = new();

    public void SuspendAll() => RunOnHotkeyThread(SuspendCore);

    private void SuspendCore()
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
        AppLog.Debug("WindowsGlobalHotkeyService", $"[GlobalHotkey] Suspended {_suspendedHotkeys.Count} hotkeys");
    }

    public void ResumeAll()
    {
        // Re-registered through Register, so a combo refused now still raises OnHotkeyRegistrationFailed.
        foreach (var kvp in RunOnHotkeyThread(TakeSuspendedHotkeys, new List<KeyValuePair<int, string>>()))
        {
            Register(kvp.Key, kvp.Value);
        }
    }

    private List<KeyValuePair<int, string>> TakeSuspendedHotkeys()
    {
        var toResume = new List<KeyValuePair<int, string>>();
        if (_handle == IntPtr.Zero) return toResume;

        _isSuspended = false;
        foreach (var kvp in _suspendedHotkeys)
        {
            // Always use the latest value from _hotkeyStrings if it exists
            string valueToRegister = _hotkeyStrings.TryGetValue(kvp.Key, out var current) ? current : kvp.Value;
            toResume.Add(new KeyValuePair<int, string>(kvp.Key, valueToRegister));
        }
        _suspendedHotkeys.Clear();

        AppLog.Debug("WindowsGlobalHotkeyService", $"[GlobalHotkey] Resumed {toResume.Count} hotkeys");
        return toResume;
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
        _matcher.Reset();
        _pendingRegistrations.Clear();
    }

    private void RunOnHotkeyThread(Action work)
    {
        var thread = _hotkeyThread;
        if (thread == null)
        {
            work();
            return;
        }

        thread.TryInvoke(work, HotkeyThreadCallTimeout);
    }

    // On timeout the work still runs once the thread gets to it; the caller gets the fallback.
    private T RunOnHotkeyThread<T>(Func<T> work, T fallback)
    {
        var thread = _hotkeyThread;
        if (thread == null)
        {
            return work();
        }

        return thread.TryInvoke(work, HotkeyThreadCallTimeout, out T result) ? result : fallback;
    }

    private (uint mods, uint vkey) ParseHotkey(string hk)
    {
        var (mods, vkey) = HotkeyParsingHelper.ParseHotkey(hk.AsSpan());
        return (mods, vkey);
    }
    
    private void EnsureMessageWindow()
    {
        if (_handle != IntPtr.Zero)
        {
            return;
        }

        _messageWndProc = MessageWindowProc;
        _messageWindowInstance = Kernel32.GetModuleHandle(null);
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
            AppLog.Debug("WindowsGlobalHotkeyService", $"[GlobalHotkey] RegisterClassEx failed: {Marshal.GetLastWin32Error()}");
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
            AppLog.Debug("WindowsGlobalHotkeyService", $"[GlobalHotkey] Create message window failed: {Marshal.GetLastWin32Error()}");
            WriteDebugLog($"message window create failed win32={Marshal.GetLastWin32Error()}");
        }
        else
        {
            AppLog.Debug("WindowsGlobalHotkeyService", $"[GlobalHotkey] Message window created: 0x{_handle.ToInt64():X}");
            WriteDebugLog($"message window created handle=0x{_handle.ToInt64():X}");
            RegisterRawKeyboardSink();
        }
    }

    private IntPtr MessageWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        // Called from native code: an exception escaping here would end the process.
        try
        {
            if (msg == WM_HOTKEY)
            {
                int id = wParam.ToInt32();
                if (_registeredIds.Contains(id) && _matcher.TryFire(id, Environment.TickCount64))
                {
                    FireHotkey(id, "message_window");
                }
            }
            else if (msg == WM_INPUT)
            {
                HandleRawKeyboardInput(lParam);
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("GlobalHotkey.MessageWindow", ex);
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
            ProcessKeyboardEvent(vkCode, isKeyDown, isKeyUp, "raw_input");
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
        var moduleHandle = Kernel32.GetModuleHandle(null);
        _llKeyboardHook = User32.SetWindowsHookEx(User32.WH_KEYBOARD_LL, _llKeyboardDelegate, moduleHandle, 0);
        if (_llKeyboardHook == IntPtr.Zero)
        {
            int firstError = Marshal.GetLastWin32Error();
            _llKeyboardHook = User32.SetWindowsHookEx(User32.WH_KEYBOARD_LL, _llKeyboardDelegate, IntPtr.Zero, 0);
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
        _notifiedElevatedPids.Clear();
    }

    private void ForegroundEventCallback(IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
    {
        // Called from native code: an exception escaping here would end the process.
        try
        {
            HandleForegroundChange(eventType, hwnd);
        }
        catch (Exception ex)
        {
            AppLog.Error("GlobalHotkey.ForegroundMonitor", ex);
        }
    }

    private void HandleForegroundChange(uint eventType, IntPtr hwnd)
    {
        if (eventType != EVENT_SYSTEM_FOREGROUND || hwnd == IntPtr.Zero)
        {
            return;
        }

        if (User32.GetWindowThreadProcessId(hwnd, out uint pid) == 0 || pid == 0)
        {
            return;
        }

        // Any process running at a higher integrity level than us blocks our hotkeys (Windows UIPI):
        // Task Manager, Registry Editor, an elevated viewer/game, etc. This is integrity-based and not
        // tied to any specific application.
        if (!IsProcessHigherIntegrity(pid))
        {
            return;
        }

        // A hotkey RegisterHotKey accepted still fires over an elevated window — WM_HOTKEY reaches our message
        // window whatever has focus. Only a hook-only hotkey actually stops working there, so with none of those
        // registered there is nothing to warn about and the notice would be pure noise.
        string? blockedHotkey = FirstHookOnlyHotkey();
        if (blockedHotkey == null)
        {
            return;
        }

        // Notify once per distinct elevated process so every privileged app is covered, while the same
        // app does not nag repeatedly (including when focus returns from our own dialog).
        if (_notifiedElevatedPids.Contains(pid))
        {
            return;
        }

        long now = Environment.TickCount64;
        if (_lastElevationNoticeTicks != 0 && now - _lastElevationNoticeTicks < 1500)
        {
            // Storm guard: avoid back-to-back popups; this pid stays un-notified so it can fire later.
            return;
        }

        _notifiedElevatedPids.Add(pid);
        _lastElevationNoticeTicks = now;
        WriteDebugLog($"elevated foreground pid={pid} detected; raising hotkey-blocked notice for {blockedHotkey}");
        PostToUi(() => OnElevatedWindowFocused?.Invoke(blockedHotkey), "GlobalHotkey.ElevatedNotice");
    }

    /// <summary>
    /// The first hotkey only the low-level keyboard hook can serve: RegisterHotKey refused it (typically
    /// ERROR_HOTKEY_ALREADY_REGISTERED — PrintScreen owned by the Snipping Tool, a combo taken by another app),
    /// so the hook matches it instead. Those are the only hotkeys an elevated foreground window silences, which
    /// is what the notice is for. Null when every registered hotkey went through RegisterHotKey.
    /// </summary>
    internal string? FirstHookOnlyHotkey()
    {
        foreach (var id in _registeredCombos.Keys)
        {
            if (!_registeredIds.Contains(id)
                && _hotkeyStrings.TryGetValue(id, out var hotkey)
                && !string.IsNullOrWhiteSpace(hotkey))
            {
                return hotkey;
            }
        }

        return null;
    }

    private bool IsProcessHigherIntegrity(uint pid)
    {
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

        if (OpenProcessToken(Kernel32.GetCurrentProcess(), TOKEN_QUERY, out IntPtr hToken))
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

        User32.UnhookWindowsHookEx(_llKeyboardHook);
        _llKeyboardHook = IntPtr.Zero;
        _llKeyboardDelegate = null;
        _matcher.Reset();
    }

    private IntPtr LowLevelKeyboardHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        // Called from native code: an escaping exception terminates the process outright instead of reaching any
        // .NET handler — and this hook sees every key the user types. Log, and let the key through.
        try
        {
            return HandleLowLevelKeyboardHook(nCode, wParam, lParam);
        }
        catch (Exception ex)
        {
            AppLog.Error("GlobalHotkey.KeyboardHook", ex);
            return User32.CallNextHookEx(_llKeyboardHook, nCode, wParam, lParam);
        }
    }

    private IntPtr HandleLowLevelKeyboardHook(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode < 0)
        {
            return User32.CallNextHookEx(_llKeyboardHook, nCode, wParam, lParam);
        }

        Interlocked.Increment(ref _lowLevelHookCalls);
        int msg = wParam.ToInt32();
        int vkCode = Marshal.ReadInt32(lParam);
        // KBDLLHOOKSTRUCT is vkCode, scanCode, flags, then time: when the key was pressed, in GetTickCount terms.
        ReportIfLate(unchecked((uint)Marshal.ReadInt32(lParam, 12)));
        bool isKeyDown = msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN;
        bool isKeyUp = msg == WM_KEYUP || msg == WM_SYSKEYUP;

        if (ProcessKeyboardEvent(vkCode, isKeyDown, isKeyUp, "ll_hook"))
        {
            return new IntPtr(1);
        }

        return User32.CallNextHookEx(_llKeyboardHook, nCode, wParam, lParam);
    }

    // True when the key completes a hotkey: the hotkey fires and the hook swallows the key.
    private bool ProcessKeyboardEvent(int vkCode, bool isKeyDown, bool isKeyUp, string source)
    {
        int? id = _matcher.OnKey(
            vkCode, isKeyDown, isKeyUp, GetAsyncModifierMask, _registeredCombos, _isSuspended, Environment.TickCount64);
        if (id is not int hotkeyId)
        {
            return false;
        }

        FireHotkey(hotkeyId, source);
        return true;
    }

    // Modifiers held down that the hook never saw go down, e.g. ones pressed before it was installed.
    private static uint GetAsyncModifierMask()
    {
        uint modifiers = 0;
        if ((User32.GetAsyncKeyState(0x10) & 0x8000) != 0) modifiers |= LowLevelHotkeyMatcher.ModShift;
        if ((User32.GetAsyncKeyState(0x11) & 0x8000) != 0) modifiers |= LowLevelHotkeyMatcher.ModControl;
        if ((User32.GetAsyncKeyState(0x12) & 0x8000) != 0) modifiers |= LowLevelHotkeyMatcher.ModAlt;
        return modifiers;
    }

    // The hook must give the key back to Windows at once, so the press is posted to the UI thread with its time.
    private void FireHotkey(int id, string source)
    {
        long pressedAt = Stopwatch.GetTimestamp();
        AppLog.Debug("WindowsGlobalHotkeyService", $"[GlobalHotkey] Triggered ID {id} via {source}");
        WriteDebugLog($"triggered id={id} source={source}");
        PostToUi(() => OnHotkeyPressed?.Invoke(new HotkeyPress(id, pressedAt)), "Hotkey.Callback");
    }

    private void PostToUi(Action action, string operation)
    {
        try
        {
            _postToUi(() =>
            {
                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    AppLog.Error(operation, ex);
                }
            });
        }
        catch (Exception ex)
        {
            // The dispatcher is already gone: the app is shutting down.
            AppLog.Warning("GlobalHotkey.PostToUi", ex);
        }
    }

    // Checks in the field that the hook keeps up: Windows stamps each key with when it was pressed. Logs at most
    // once per 10 s.
    private void ReportIfLate(uint keyTime)
    {
        uint lateMs = unchecked((uint)Environment.TickCount - keyTime);
        if (lateMs <= HookLateThresholdMs || lateMs > 60_000)
        {
            return;
        }

        long now = Environment.TickCount64;
        if (now - _lastHookLateLogTicks < 10_000)
        {
            return;
        }

        _lastHookLateLogTicks = now;
        AppLog.Warning("Hotkey.HookLate", $"The keyboard hook got a key {lateMs} ms after it was pressed.");
    }

    /// <summary>How many key events the low-level hook has handled; a stalled or removed hook stops counting.</summary>
    internal long LowLevelHookCalls => Interlocked.Read(ref _lowLevelHookCalls);

    /// <summary>Feeds a key event down the hook's path; true when the hook would swallow the key.</summary>
    internal bool ProcessKeyEventForTest(int virtualKey, bool isKeyDown, bool isKeyUp) =>
        ProcessKeyboardEvent(virtualKey, isKeyDown, isKeyUp, "test");

    private void WriteDebugLog(string message)
    {
        _ = message;
    }

    public void Dispose()
    {
        var thread = _hotkeyThread;
        if (thread == null)
        {
            ReleaseNativeState();
            return;
        }

        // Unhooked and destroyed on the thread that created them; then its loop ends.
        try
        {
            if (!thread.TryInvoke(ReleaseNativeState, TimeSpan.FromSeconds(2)))
            {
                AppLog.Warning("GlobalHotkey.Dispose", "The hotkey thread did not release its hooks within 2 s.");
            }
        }
        finally
        {
            _hotkeyThread = null;
            thread.Dispose();
        }
    }

    private void ReleaseNativeState()
    {
        UnregisterAll();
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
