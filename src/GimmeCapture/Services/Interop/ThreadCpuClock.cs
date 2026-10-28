using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace GimmeCapture.Services.Interop;

/// <summary>
/// The CPU time one thread has used so far, readable from any thread: what tells a UI thread that was busy through a
/// stall from one that was waiting (<see cref="UiThreadStallMonitor"/>). Windows only; elsewhere there is no clock and
/// the stall log says so.
/// </summary>
internal sealed class ThreadCpuClock : IDisposable
{
    private const uint ThreadQueryLimitedInformation = 0x0800;

    private readonly SafeWaitHandle _thread;

    private ThreadCpuClock(SafeWaitHandle thread)
    {
        _thread = thread;
    }

    /// <summary>A clock for the calling thread, or null where one cannot be had.</summary>
    internal static ThreadCpuClock? ForCurrentThread()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            // GetCurrentThread is a pseudo-handle that means whichever thread uses it, so a thread reading this one
            // needs a real handle to it.
            IntPtr process = Kernel32.GetCurrentProcess();
            if (!DuplicateHandle(process, GetCurrentThread(), process, out IntPtr handle, ThreadQueryLimitedInformation, false, 0))
            {
                return null;
            }

            return new ThreadCpuClock(new SafeWaitHandle(handle, ownsHandle: true));
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    /// <summary>User plus kernel time so far, or null when the read fails.</summary>
    internal TimeSpan? Read()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            // FILETIME counts 100 ns units, as TimeSpan ticks do.
            return GetThreadTimes(_thread, out _, out _, out long kernel, out long user)
                ? TimeSpan.FromTicks(kernel + user)
                : null;
        }
        catch (ObjectDisposedException)
        {
            return null; // stopped while another thread was reading it
        }
    }

    public void Dispose() => _thread.Dispose();

    [SupportedOSPlatform("windows")]
    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentThread();

    [SupportedOSPlatform("windows")]
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DuplicateHandle(
        IntPtr hSourceProcessHandle,
        IntPtr hSourceHandle,
        IntPtr hTargetProcessHandle,
        out IntPtr lpTargetHandle,
        uint dwDesiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle,
        uint dwOptions);

    [SupportedOSPlatform("windows")]
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetThreadTimes(
        SafeHandle hThread,
        out long lpCreationTime,
        out long lpExitTime,
        out long lpKernelTime,
        out long lpUserTime);
}
