using System;
using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using GimmeCapture.Services.Core.Infrastructure;

namespace GimmeCapture.Services.Platforms.Windows;

/// <summary>
/// A thread that only runs a Win32 message loop, for native objects whose callbacks arrive on the thread that created
/// them. The global hotkey service keeps its message-only window, raw-input sink, low-level keyboard hook and
/// foreground WinEvent hook here rather than on the UI thread. Windows holds every key typed anywhere until that hook
/// returns, and silently removes a hook that keeps it waiting past LowLevelHooksTimeout, so the hook's thread must
/// never be busy. Work is handed over as thread messages, and nothing on this thread ever waits for another.
/// </summary>
internal sealed class Win32HotkeyThread : IDisposable
{
    private const uint WM_APP = 0x8000;
    private const uint WM_RUN_QUEUED = WM_APP + 0x31;
    private const uint PM_NOREMOVE = 0x0000;

    private readonly ConcurrentQueue<Action> _queue = new();
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _started = new();
    private Action? _setup;
    private uint _nativeThreadId;
    private int _stopRequested;

    internal Win32HotkeyThread(string name)
    {
        _thread = new Thread(Run)
        {
            Name = name,
            IsBackground = true,
            // Above the UI thread, so a busy app cannot starve the hook either.
            Priority = ThreadPriority.AboveNormal,
        };
    }

    internal bool IsCurrentThread => Thread.CurrentThread == _thread;

    /// <summary>Starts the loop, runs <paramref name="setup"/> on it and returns once that has run.</summary>
    internal bool Start(Action setup, TimeSpan timeout)
    {
        _setup = setup;
        _thread.Start();
        return _started.Wait(timeout);
    }

    /// <summary>
    /// Runs <paramref name="work"/> on the thread and waits for it, rethrowing what it throws; on the thread itself it
    /// runs inline. False when the thread has stopped or did not get to the work within <paramref name="timeout"/>.
    /// </summary>
    internal bool TryInvoke(Action work, TimeSpan timeout) =>
        TryInvoke(() =>
        {
            work();
            return true;
        }, timeout, out _);

    /// <inheritdoc cref="TryInvoke(Action, TimeSpan)"/>
    internal bool TryInvoke<T>(Func<T> work, TimeSpan timeout, out T result)
    {
        if (IsCurrentThread)
        {
            result = work();
            return true;
        }

        result = default!;
        if (Volatile.Read(ref _stopRequested) != 0)
        {
            return false;
        }

        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _queue.Enqueue(() =>
        {
            try
            {
                done.TrySetResult(work());
            }
            catch (Exception ex)
            {
                done.TrySetException(ex);
            }
        });

        if (!PostThreadMessage(_nativeThreadId, WM_RUN_QUEUED, IntPtr.Zero, IntPtr.Zero))
        {
            AppLog.Warning("GlobalHotkey.ThreadPost", $"PostThreadMessage failed (win32={Marshal.GetLastWin32Error()}).");
            return false;
        }

        try
        {
            if (!done.Task.Wait(timeout))
            {
                AppLog.Warning("GlobalHotkey.ThreadUnresponsive",
                    $"The hotkey thread did not run a call within {timeout.TotalMilliseconds:F0} ms.");
                return false;
            }
        }
        catch (AggregateException ex) when (ex.InnerExceptions.Count == 1)
        {
            ExceptionDispatchInfo.Capture(ex.InnerExceptions[0]).Throw();
        }

        result = done.Task.Result;
        return true;
    }

    private void Run()
    {
        // Gives this thread its message queue, so thread messages posted from here on are kept.
        PeekMessage(out _, IntPtr.Zero, 0, 0, PM_NOREMOVE);
        _nativeThreadId = GetCurrentThreadId();
        try
        {
            _setup?.Invoke();
        }
        catch (Exception ex)
        {
            AppLog.Error("GlobalHotkey.ThreadSetup", ex);
        }
        finally
        {
            _setup = null;
            _started.Set();
        }

        while (true)
        {
            int got = GetMessage(out MSG msg, IntPtr.Zero, 0, 0);
            if (got == 0)
            {
                break; // WM_QUIT
            }

            if (got == -1)
            {
                AppLog.Warning("GlobalHotkey.ThreadLoop", $"GetMessage failed (win32={Marshal.GetLastWin32Error()}).");
                break;
            }

            if (msg.hwnd == IntPtr.Zero && msg.message == WM_RUN_QUEUED)
            {
                RunQueued();
                continue;
            }

            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }

        RunQueued();
    }

    private void RunQueued()
    {
        // Every queued item catches its own exceptions.
        while (_queue.TryDequeue(out var work))
        {
            work();
        }
    }

    /// <summary>Ends the loop, after the work already queued, and waits up to 2 s for the thread to exit.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _stopRequested, 1) != 0 || !_thread.IsAlive)
        {
            return;
        }

        _queue.Enqueue(() => PostQuitMessage(0));
        PostThreadMessage(_nativeThreadId, WM_RUN_QUEUED, IntPtr.Zero, IntPtr.Zero);
        if (!IsCurrentThread && !_thread.Join(TimeSpan.FromSeconds(2)))
        {
            AppLog.Warning("GlobalHotkey.ThreadStop", "The hotkey thread did not exit within 2 s.");
        }
    }

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

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PeekMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax, uint wRemoveMsg);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref MSG lpMsg);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostThreadMessage(uint idThread, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern void PostQuitMessage(int nExitCode);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
}
