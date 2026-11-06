using System;
using System.Runtime.InteropServices;

namespace GimmeCapture.Services.Core.Media.NativeFFmpeg;

/// <summary>
/// NVIDIA's CUDA driver, which FFmpeg's NVENC loads and starts before it opens an encoder on frames in memory.
/// </summary>
internal static unsafe class CudaDriver
{
    private const int CudaSuccess = 0;

    /// <summary>
    /// Loads the driver NVENC loads and starts it (<c>cuInit</c>) without making the context NVENC goes on to make, and
    /// keeps it loaded. False, with nothing kept, where there is no NVIDIA driver or it does not start: NVENC's open fails
    /// at the same point there.
    /// </summary>
    public static bool TryInitialize()
    {
        DisplayAdapterOrder.SettleBeforeCuda();
        IntPtr library;
        bool loaded = OperatingSystem.IsWindows()
            // The driver installs it in System32, and only that copy is NVIDIA's.
            ? NativeLibrary.TryLoad("nvcuda.dll", typeof(CudaDriver).Assembly, DllImportSearchPath.System32, out library)
            : NativeLibrary.TryLoad("libcuda.so.1", out library);
        if (!loaded)
        {
            return false;
        }

        if (NativeLibrary.TryGetExport(library, "cuInit", out IntPtr cuInit)
            && ((delegate* unmanaged<uint, int>)cuInit)(0) == CudaSuccess)
        {
            return true;
        }

        NativeLibrary.Free(library);
        return false;
    }
}
