using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using GimmeCapture.Services.Platforms.Windows;
using Xunit;
using Xunit.Abstractions;

namespace GimmeCapture.Tests;

/// <summary>
/// The global hotkey service's low-level keyboard hook sees every key typed anywhere in the system, and Windows waits
/// for it before the key goes on to any other app. It used to run on the UI thread, so a busy UI thread (freezes of
/// 0.8-1.8 s were measured) stalled typing system-wide — and a hook that misses LowLevelHooksTimeout is silently
/// removed on Windows 7+, taking the hook-only hotkeys with it.
///
/// A probe hook installed BEFORE the service's sits after it in the chain, so the time a key takes to reach the
/// probe is the delay the service's hook adds. The UI thread is an STA thread pumping a WinForms message loop, as
/// RecordingStartUiStallTests does. Opt-in: it injects F23/F24 presses into the desktop session with SendInput.
/// </summary>
public class GlobalHotkeyHookLatencyTests
{
    private const int HotkeyId = 9_424;
    private const ushort VK_F23 = 0x86;
    private const ushort VK_F24 = 0x87;

    private readonly ITestOutputHelper _output;

    public GlobalHotkeyHookLatencyTests(ITestOutputHelper output) => _output = output;

    [OptInFact("HOTKEY_HOOK_PROBE=1")]
    public void KeysAreNotHeldUp_AndHotkeysStillFire_WhileTheUiThreadIsBlocked()
    {
        using var probe = new ProbeHook(VK_F23);
        using var ui = new UiThread();

        int callbacks = 0;
        WindowsGlobalHotkeyService service = ui.Invoke(() =>
        {
            var s = new WindowsGlobalHotkeyService(ui.Post);
            s.OnHotkeyPressed = press =>
            {
                if (press.Id == HotkeyId)
                {
                    Interlocked.Increment(ref callbacks);
                }
            };
            s.Register(HotkeyId, "F24");
            s.Initialize(null!);
            return s;
        });

        try
        {
            KeyResult idle = probe.Press();

            // Block the UI thread for 2 s and type while it is blocked.
            ui.Post(() => Thread.Sleep(2000));
            Thread.Sleep(100);
            KeyResult blocked = probe.Press();
            for (int i = 0; i < 3; i++)
            {
                SendKey(VK_F24);
                Thread.Sleep(250); // past the service's 150 ms per-hotkey debounce
            }

            // Let the block end, then check the hook is still in the chain and still matching.
            Thread.Sleep(2000);
            long hookCallsBefore = service.LowLevelHookCalls;
            KeyResult after = probe.Press();
            SendKey(VK_F24);
            Thread.Sleep(500);
            long hookCallsAfter = service.LowLevelHookCalls - hookCallsBefore;

            _output.WriteLine($"idle: {idle}; UI blocked: {blocked}; after: {after}; "
                + $"hotkey callbacks {Volatile.Read(ref callbacks)} of 4; the hook handled {hookCallsAfter} of 4 key events after the block");

            Assert.True(blocked.HeldUpMs < 50, $"with the UI thread blocked, typing a key was held up {blocked.HeldUpMs:F0} ms");
            Assert.True(blocked.ReachedNextHook, "with the UI thread blocked, a key never reached the next keyboard hook");
            Assert.True(after.HeldUpMs < 50 && after.ReachedNextHook, $"after the block: {after}");
            Assert.Equal(4, Volatile.Read(ref callbacks));
            Assert.Equal(4, hookCallsAfter); // F23 and F24, down and up: the hook was not removed
        }
        finally
        {
            ui.Invoke(() =>
            {
                service.Dispose();
                return 0;
            });
        }
    }

    private sealed record KeyResult(double HeldUpMs, bool ReachedNextHook, double NextHookLagMs)
    {
        public override string ToString() =>
            $"held up {HeldUpMs:F0} ms, " + (ReachedNextHook ? $"reached the next hook after {NextHookLagMs:F0} ms" : "never reached the next hook");
    }

    private static void SendKey(ushort vk)
    {
        var inputs = new INPUT[]
        {
            new() { type = INPUT_KEYBOARD, U = new InputUnion { ki = new KEYBDINPUT { wVk = vk } } },
            new() { type = INPUT_KEYBOARD, U = new InputUnion { ki = new KEYBDINPUT { wVk = vk, dwFlags = KEYEVENTF_KEYUP } } },
        };
        uint sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
        Assert.Equal((uint)inputs.Length, sent);
    }

    /// <summary>An STA thread pumping a WinForms message loop: the stand-in for the app's UI thread.</summary>
    private sealed class UiThread : IDisposable
    {
        private readonly Thread _thread;
        private readonly ManualResetEventSlim _ready = new();
        private System.Windows.Forms.ApplicationContext? _context;
        private SynchronizationContext? _sync;

        public UiThread()
        {
            _thread = new Thread(() =>
            {
                _context = new System.Windows.Forms.ApplicationContext();
                // Creating a control installs the WinForms SynchronizationContext on this thread.
                using var marshal = new System.Windows.Forms.Control();
                _ = marshal.Handle;
                _sync = SynchronizationContext.Current;
                _ready.Set();
                System.Windows.Forms.Application.Run(_context);
            });
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.IsBackground = true;
            _thread.Start();
            Assert.True(_ready.Wait(TimeSpan.FromSeconds(5)), "the UI thread did not start");
        }

        public void Post(Action action) => _sync!.Post(_ => action(), null);

        public T Invoke<T>(Func<T> func)
        {
            T result = default!;
            Exception? error = null;
            using var done = new ManualResetEventSlim();
            _sync!.Post(_ =>
            {
                try
                {
                    result = func();
                }
                catch (Exception ex)
                {
                    error = ex;
                }
                finally
                {
                    done.Set();
                }
            }, null);
            Assert.True(done.Wait(TimeSpan.FromSeconds(10)), "the UI thread did not run the call");
            if (error != null)
            {
                throw new InvalidOperationException("UI thread call failed", error);
            }

            return result;
        }

        public void Dispose()
        {
            Post(() => _context!.ExitThread());
            _thread.Join(TimeSpan.FromSeconds(5));
        }
    }

    /// <summary>
    /// A low-level keyboard hook on its own message-pumping thread that timestamps one key. Installed before the
    /// service's hook, it is called after it.
    /// </summary>
    private sealed class ProbeHook : IDisposable
    {
        private readonly ushort _vk;
        private readonly ConcurrentQueue<long> _seen = new();
        private readonly Thread _thread;
        private readonly ManualResetEventSlim _ready = new();
        private LowLevelKeyboardProc? _proc;
        private uint _threadId;

        public ProbeHook(ushort vk)
        {
            _vk = vk;
            _thread = new Thread(Run) { IsBackground = true, Name = "HookLatencyProbe" };
            _thread.Start();
            Assert.True(_ready.Wait(TimeSpan.FromSeconds(5)), "the probe hook did not install");
        }

        private void Run()
        {
            _threadId = GetCurrentThreadId();
            _proc = (nCode, wParam, lParam) =>
            {
                int message = (int)wParam;
                if (nCode >= 0 && (message == WM_KEYDOWN || message == WM_SYSKEYDOWN) && Marshal.ReadInt32(lParam) == _vk)
                {
                    _seen.Enqueue(Stopwatch.GetTimestamp());
                }

                return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
            };
            IntPtr hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(null), 0);
            _ready.Set();
            while (GetMessage(out _, IntPtr.Zero, 0, 0) > 0)
            {
            }

            UnhookWindowsHookEx(hook);
        }

        /// <summary>
        /// Presses the probe's key. SendInput returns once every low-level hook has handled the key, so its duration
        /// is how long the hooks held the input up; the probe hook then reports whether the key reached it at all.
        /// </summary>
        public KeyResult Press()
        {
            while (_seen.TryDequeue(out _))
            {
            }

            long sentAt = Stopwatch.GetTimestamp();
            SendKey(_vk);
            double heldUpMs = Stopwatch.GetElapsedTime(sentAt).TotalMilliseconds;

            var deadline = Stopwatch.StartNew();
            while (deadline.Elapsed < TimeSpan.FromSeconds(1))
            {
                if (_seen.TryDequeue(out long seenAt))
                {
                    return new KeyResult(heldUpMs, true, Stopwatch.GetElapsedTime(sentAt, seenAt).TotalMilliseconds);
                }

                Thread.Sleep(1);
            }

            return new KeyResult(heldUpMs, false, double.NaN);
        }

        public void Dispose()
        {
            PostThreadMessage(_threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
            _thread.Join(TimeSpan.FromSeconds(5));
        }
    }

    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const uint WM_QUIT = 0x0012;
    private const uint INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_KEYUP = 0x0002;

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int ptX;
        public int ptY;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public InputUnion U;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public KEYBDINPUT ki;
        [FieldOffset(0)] public MOUSEINPUT mi;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostThreadMessage(uint idThread, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();


    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);
}
