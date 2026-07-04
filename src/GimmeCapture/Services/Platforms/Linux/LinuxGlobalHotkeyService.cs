using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using Avalonia.Controls;
using Avalonia.Threading;
using GimmeCapture.Services.Abstractions;
using GimmeCapture.Services.Core.Infrastructure;

namespace GimmeCapture.Services.Platforms.Linux;

/// <summary>
/// X11 global hotkeys for Linux via <c>XGrabKey</c> on the root window — the same keyboard-driven
/// trigger model as Windows (docs/LINUX_PORT_FEASIBILITY.md). All Xlib calls happen on one dedicated
/// thread that owns a private display connection; <see cref="Register"/>/<see cref="Unregister"/>/
/// suspend/resume post commands to it. Hotkey strings and modifier/key codes come from the shared
/// <see cref="HotkeyParsingHelper"/> (Win32 VK + MOD flags), which we map to X11 keysyms/masks.
///
/// Wayland note: XGrabKey only sees X11/XWayland input, so global hotkeys won't fire from native
/// Wayland clients. We do NOT surface OnHotkeyRegistrationFailed on Linux (it drives a Windows-centric
/// error dialog); grab conflicts are logged and skipped.
/// </summary>
public sealed class LinuxGlobalHotkeyService : IGlobalHotkeyService
{
    public Action<int>? OnHotkeyPressed { get; set; }
    public Action<int, string, int>? OnHotkeyRegistrationFailed { get; set; }
    public Action? OnElevatedWindowFocused { get; set; }

    // X11 event types / masks / grab modes.
    private const int KeyPress = 2;
    private const long KeyPressMask = 1L << 0;
    private const int GrabModeAsync = 1;

    // X11 modifier masks, lock combos and Vk/mods mapping live in LinuxX11KeyMap (shared with LinuxSnipKeyGrab).

    // XKeyEvent field offsets within the XEvent union on 64-bit (LP64).
    private const int KeyEventStateOffset = 80;
    private const int KeyEventKeycodeOffset = 84;
    private const short POLLIN = 0x001;

    private readonly ConcurrentQueue<Command> _commands = new();
    private Thread? _thread;
    private volatile bool _running;

    // --- Fields below are touched only on the X thread ---
    private IntPtr _display;
    private IntPtr _root;
    private IntPtr _eventBuffer;
    private IntPtr _prevErrorHandler;
    private IntPtr _installedErrorHandler;
    private XErrorHandler? _errorHandlerDelegate; // held to keep the marshalled fn-ptr alive
    private readonly Dictionary<int, (byte Keycode, uint Mods)> _grabbed = new();
    private readonly Dictionary<int, (uint Win32Mods, uint Vkey)> _desired = new();
    private bool _suspended;

    private enum CommandType { Register, Unregister, Suspend, Resume }

    private readonly record struct Command(CommandType Type, int Id, uint Win32Mods, uint Vkey);

    public void Initialize(Window window)
    {
        if (_thread != null)
        {
            return;
        }

        _running = true;
        _thread = new Thread(RunX11Loop)
        {
            IsBackground = true,
            Name = "LinuxGlobalHotkey"
        };
        _thread.Start();
        AppLog.Information("LinuxGlobalHotkey.Initialize (X11 XGrabKey)");
    }

    public void Register(int id, string hotkey)
    {
        var (win32Mods, vkey) = HotkeyParsingHelper.ParseHotkey(hotkey.AsSpan());
        if (vkey == 0)
        {
            AppLog.Information($"LinuxGlobalHotkey.Register.Unsupported id={id} hotkey={hotkey}");
            return;
        }

        _commands.Enqueue(new Command(CommandType.Register, id, win32Mods, vkey));
    }

    public void Unregister(int id) => _commands.Enqueue(new Command(CommandType.Unregister, id, 0, 0));

    public void SuspendAll() => _commands.Enqueue(new Command(CommandType.Suspend, 0, 0, 0));

    public void ResumeAll() => _commands.Enqueue(new Command(CommandType.Resume, 0, 0, 0));

    public void Dispose()
    {
        _running = false;
        _thread?.Join(1500);
        _thread = null;
    }

    // ---- X thread ----------------------------------------------------------------------------

    private void RunX11Loop()
    {
        try
        {
            _display = XOpenDisplay(null);
            if (_display == IntPtr.Zero)
            {
                AppLog.Information("LinuxGlobalHotkey.NoDisplay (no X11 display; hotkeys disabled — Wayland-only session?)");
                return;
            }

            _eventBuffer = Marshal.AllocHGlobal(256); // XEvent union is ~192 bytes on 64-bit
            _root = XDefaultRootWindow(_display);

            // Swallow errors from OUR display (e.g. BadAccess when a combo is already grabbed) so a grab
            // conflict can't reach the default Xlib handler, which calls exit(). Other displays chain.
            _errorHandlerDelegate = ErrorHandler;
            _installedErrorHandler = Marshal.GetFunctionPointerForDelegate(_errorHandlerDelegate);
            _prevErrorHandler = XSetErrorHandler(_installedErrorHandler);

            XSelectInput(_display, _root, KeyPressMask);

            var pfds = new[] { new Pollfd { fd = XConnectionNumber(_display), events = POLLIN, revents = 0 } };

            while (_running)
            {
                DrainCommands();

                // Block for X input or wake every 200ms to re-check _running / the command queue.
                poll(pfds, 1, 200);

                while (_running && XPending(_display) > 0)
                {
                    XNextEvent(_display, _eventBuffer);
                    if (Marshal.ReadInt32(_eventBuffer, 0) == KeyPress)
                    {
                        uint state = unchecked((uint)Marshal.ReadInt32(_eventBuffer, KeyEventStateOffset));
                        uint keycode = unchecked((uint)Marshal.ReadInt32(_eventBuffer, KeyEventKeycodeOffset));
                        HandleKeyPress(keycode, state);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            AppLog.Warning("LinuxGlobalHotkey.Loop", ex);
        }
        finally
        {
            CleanupOnThread();
        }
    }

    private void DrainCommands()
    {
        while (_commands.TryDequeue(out var cmd))
        {
            switch (cmd.Type)
            {
                case CommandType.Register:
                    _desired[cmd.Id] = (cmd.Win32Mods, cmd.Vkey);
                    if (!_suspended)
                    {
                        GrabKey(cmd.Id, cmd.Win32Mods, cmd.Vkey);
                    }
                    break;

                case CommandType.Unregister:
                    _desired.Remove(cmd.Id);
                    UngrabKey(cmd.Id);
                    break;

                case CommandType.Suspend:
                    _suspended = true;
                    foreach (var id in new List<int>(_grabbed.Keys))
                    {
                        UngrabKey(id);
                    }
                    break;

                case CommandType.Resume:
                    _suspended = false;
                    foreach (var kvp in _desired)
                    {
                        GrabKey(kvp.Key, kvp.Value.Win32Mods, kvp.Value.Vkey);
                    }
                    break;
            }
        }
    }

    private void GrabKey(int id, uint win32Mods, uint vkey)
    {
        UngrabKey(id); // drop any prior grab for this id first

        ulong keysym = LinuxX11KeyMap.VkToKeysym(vkey);
        if (keysym == 0)
        {
            return;
        }

        byte keycode = XKeysymToKeycode(_display, keysym);
        if (keycode == 0)
        {
            AppLog.Information($"LinuxGlobalHotkey.Grab.NoKeycode id={id} vkey=0x{vkey:X} keysym=0x{keysym:X}");
            return;
        }

        uint x11mods = LinuxX11KeyMap.Win32ToX11Mods(win32Mods);
        foreach (var lockMask in LinuxX11KeyMap.LockCombos)
        {
            XGrabKey(_display, keycode, x11mods | lockMask, _root, false, GrabModeAsync, GrabModeAsync);
        }
        XSync(_display, false);

        _grabbed[id] = (keycode, x11mods);
        AppLog.Information($"LinuxGlobalHotkey.Grabbed id={id} keycode={keycode} x11mods={x11mods}");
    }

    private void UngrabKey(int id)
    {
        if (!_grabbed.TryGetValue(id, out var g))
        {
            return;
        }

        foreach (var lockMask in LinuxX11KeyMap.LockCombos)
        {
            XUngrabKey(_display, g.Keycode, g.Mods | lockMask, _root);
        }
        XSync(_display, false);
        _grabbed.Remove(id);
    }

    private void HandleKeyPress(uint keycode, uint state)
    {
        uint mods = state & LinuxX11KeyMap.RelevantModMask;
        foreach (var kvp in _grabbed)
        {
            if (kvp.Value.Keycode == keycode && kvp.Value.Mods == mods)
            {
                int id = kvp.Key;
                Dispatcher.UIThread.Post(() =>
                {
                    try
                    {
                        OnHotkeyPressed?.Invoke(id);
                    }
                    catch (Exception ex)
                    {
                        AppLog.Error("LinuxGlobalHotkey.Callback", ex);
                    }
                });
                return;
            }
        }
    }

    private void CleanupOnThread()
    {
        if (_display != IntPtr.Zero)
        {
            foreach (var id in new List<int>(_grabbed.Keys))
            {
                UngrabKey(id);
            }

            // XSetErrorHandler is process-global. Only restore the previous handler if OURS is still the
            // top one; if something installed a newer handler after us, put that back — don't clobber it.
            if (_installedErrorHandler != IntPtr.Zero)
            {
                IntPtr cur = XSetErrorHandler(_prevErrorHandler);
                if (cur != _installedErrorHandler)
                {
                    XSetErrorHandler(cur);
                }
                _prevErrorHandler = IntPtr.Zero;
                _installedErrorHandler = IntPtr.Zero;
            }

            XCloseDisplay(_display);
            _display = IntPtr.Zero;
        }

        if (_eventBuffer != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_eventBuffer);
            _eventBuffer = IntPtr.Zero;
        }
    }

    private int ErrorHandler(IntPtr display, IntPtr errorEvent)
    {
        // XErrorEvent.display is a pointer at offset 8 on LP64. Swallow errors from our own display
        // (grab conflicts etc.); delegate everything else to the previously-installed handler.
        IntPtr errDisplay = Marshal.ReadIntPtr(errorEvent, 8);
        if (errDisplay == _display)
        {
            return 0;
        }

        if (_prevErrorHandler != IntPtr.Zero)
        {
            var prev = Marshal.GetDelegateForFunctionPointer<XErrorHandler>(_prevErrorHandler);
            return prev(display, errorEvent);
        }

        return 0;
    }

    // ---- libX11 / libc interop ---------------------------------------------------------------

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int XErrorHandler(IntPtr display, IntPtr errorEvent);

    [StructLayout(LayoutKind.Sequential)]
    private struct Pollfd
    {
        public int fd;
        public short events;
        public short revents;
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int poll([In, Out] Pollfd[] fds, uint nfds, int timeout);

    [DllImport("libX11.so.6")]
    private static extern IntPtr XOpenDisplay(string? displayName);

    [DllImport("libX11.so.6")]
    private static extern int XCloseDisplay(IntPtr display);

    [DllImport("libX11.so.6")]
    private static extern IntPtr XDefaultRootWindow(IntPtr display);

    [DllImport("libX11.so.6")]
    private static extern int XConnectionNumber(IntPtr display);

    [DllImport("libX11.so.6")]
    private static extern int XSelectInput(IntPtr display, IntPtr window, long eventMask);

    [DllImport("libX11.so.6")]
    private static extern byte XKeysymToKeycode(IntPtr display, ulong keysym);

    [DllImport("libX11.so.6")]
    private static extern int XGrabKey(IntPtr display, int keycode, uint modifiers, IntPtr grabWindow, bool ownerEvents, int pointerMode, int keyboardMode);

    [DllImport("libX11.so.6")]
    private static extern int XUngrabKey(IntPtr display, int keycode, uint modifiers, IntPtr grabWindow);

    [DllImport("libX11.so.6")]
    private static extern int XPending(IntPtr display);

    [DllImport("libX11.so.6")]
    private static extern int XNextEvent(IntPtr display, IntPtr eventReturn);

    [DllImport("libX11.so.6")]
    private static extern int XSync(IntPtr display, bool discard);

    [DllImport("libX11.so.6")]
    private static extern IntPtr XSetErrorHandler(IntPtr handler);
}
