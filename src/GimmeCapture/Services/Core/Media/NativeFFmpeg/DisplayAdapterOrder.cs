using System;
using System.Runtime.InteropServices;
using GimmeCapture.Services.Interop;

namespace GimmeCapture.Services.Core.Media.NativeFFmpeg;

/// <summary>
/// Keeps DXGI listing a laptop's display under the graphics chip it hangs off, which Desktop Duplication needs. A
/// process that starts the NVIDIA driver's CUDA (NVENC does) before it first asks DXGI anything is put on the NVIDIA
/// card for good: DXGI then lists that card first with the display under it, nothing under the chip, and duplicating
/// the display fails ("Selected output not supported"), so a recording falls back to gdigrab. Asking DXGI first keeps
/// the order Windows gives programs that never touch CUDA, and CUDA and NVENC start and encode the same after it.
/// </summary>
internal static class DisplayAdapterOrder
{
    private static readonly object Gate = new();
    private static volatile bool _settled;

    /// <summary>
    /// Asks DXGI for a factory, once a process, on Windows, and returns only once it has: called before anything starts
    /// CUDA (the warm-up's cuInit, every NVENC open), a second caller waits for the first. The first takes 35-40 ms,
    /// loading dxgi.dll, so not on the UI thread.
    /// </summary>
    public static void SettleBeforeCuda()
    {
        if (_settled || !OperatingSystem.IsWindows())
        {
            return;
        }

        lock (Gate)
        {
            if (_settled)
            {
                return;
            }

            try
            {
                if (Dxgi.CreateDXGIFactory1(in Dxgi.Factory1, out IntPtr factory) >= 0 && factory != IntPtr.Zero)
                {
                    Marshal.Release(factory);
                }
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
            {
                // No DXGI, so no Desktop Duplication either.
                AppLog.Debug("DisplayAdapterOrder", ex);
            }

            _settled = true;
        }
    }
}
