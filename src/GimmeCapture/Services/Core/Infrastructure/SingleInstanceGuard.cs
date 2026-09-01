using System;
using System.Threading;

namespace GimmeCapture.Services.Core.Infrastructure;

/// <summary>
/// Enforces one running app per installed copy. The first process owns a named mutex (scoped by the
/// install-directory key, so two different installs can still run side by side); any further launch
/// fails to acquire it, signals the running instance to show its main window (Windows), and exits.
/// Named mutexes are cross-process on both Windows and Linux; the activation channel (a named
/// EventWaitHandle) exists only on Windows — on Linux a duplicate launch simply exits.
/// </summary>
internal sealed class SingleInstanceGuard : IDisposable
{
    private readonly Mutex _mutex;
    private EventWaitHandle? _activationEvent;
    private volatile bool _disposed;

    private SingleInstanceGuard(Mutex mutex)
    {
        _mutex = mutex;
    }

    private static string MutexName(string key) => @"Local\GimmeCapture_Instance_" + key;
    private static string ActivationEventName(string key) => @"Local\GimmeCapture_Activate_" + key;

    private static string DefaultKey()
    {
        string exeDir = RuntimePathProvider.GetExecutableDirectory();
        return AppStoragePaths.GetInstallInstanceKey(
            string.IsNullOrEmpty(exeDir) ? AppContext.BaseDirectory : exeDir);
    }

    /// <summary>Returns a guard owning the instance mutex, or null when another instance already owns it.
    /// The mutex is acquired on (and must be disposed from) the calling thread.</summary>
    public static SingleInstanceGuard? TryAcquire(string? key = null)
    {
        var mutex = new Mutex(initiallyOwned: false, MutexName(key ?? DefaultKey()));
        try
        {
            bool owned;
            try
            {
                owned = mutex.WaitOne(TimeSpan.Zero);
            }
            catch (AbandonedMutexException)
            {
                // The previous owner crashed without releasing — the mutex is ours now.
                owned = true;
            }

            if (owned)
            {
                return new SingleInstanceGuard(mutex);
            }

            mutex.Dispose();
            return null;
        }
        catch (Exception ex)
        {
            // A broken mutex must never keep the app from starting — fail open.
            AppLog.Warning("SingleInstance.Acquire", ex);
            mutex.Dispose();
            return new SingleInstanceGuard(new Mutex(initiallyOwned: false));
        }
    }

    /// <summary>Second-instance side: ask the running instance to show its main window (Windows only).</summary>
    public static void SignalRunningInstance(string? key = null)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            if (EventWaitHandle.TryOpenExisting(ActivationEventName(key ?? DefaultKey()), out var handle))
            {
                using (handle)
                {
                    handle.Set();
                }
            }
        }
        catch (Exception ex)
        {
            AppLog.Warning("SingleInstance.Signal", ex);
        }
    }

    /// <summary>First-instance side: invoke <paramref name="onActivationRequested"/> (on a background
    /// thread — marshal to the UI thread yourself) each time a duplicate launch signals us. Windows only;
    /// a no-op elsewhere. Call at most once.</summary>
    public void StartActivationListener(Action onActivationRequested)
    {
        if (!OperatingSystem.IsWindows() || _activationEvent != null)
        {
            return;
        }

        try
        {
            _activationEvent = new EventWaitHandle(initialState: false, EventResetMode.AutoReset, ActivationEventName(DefaultKey()));
        }
        catch (Exception ex)
        {
            AppLog.Warning("SingleInstance.Listener", ex);
            return;
        }

        var thread = new Thread(() =>
        {
            while (!_disposed)
            {
                try
                {
                    _activationEvent.WaitOne();
                    if (_disposed)
                    {
                        return;
                    }

                    onActivationRequested();
                }
                catch (ObjectDisposedException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    AppLog.Warning("SingleInstance.Activation", ex);
                }
            }
        })
        {
            IsBackground = true,
            Name = "SingleInstanceActivation",
        };
        thread.Start();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            _activationEvent?.Set(); // unblock the listener thread so it can observe _disposed
            _activationEvent?.Dispose();
        }
        catch
        {
            // Best-effort cleanup only.
        }

        try
        {
            _mutex.ReleaseMutex();
        }
        catch
        {
            // Not owned by this thread / already released — the OS reclaims it at process exit anyway.
        }

        _mutex.Dispose();
    }
}
