using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace GimmeCapture.Services.Interop;

/// <summary>
/// The process's page faults so far, served from memory or read from disk alike. A UI thread blocked through a stall
/// while its process faulted thousands of times was most likely waiting on pages coming back from disk, from a hard disk
/// after an idle spell say, rather than on a lock (<see cref="Core.Infrastructure.UiStallReport"/>). Windows only;
/// elsewhere null.
/// </summary>
internal static class ProcessPageFaults
{
    internal static uint? Read()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            var counters = new ProcessMemoryCounters { Size = (uint)Marshal.SizeOf<ProcessMemoryCounters>() };
            return K32GetProcessMemoryInfo(Kernel32.GetCurrentProcess(), ref counters, counters.Size)
                ? counters.PageFaultCount
                : null;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessMemoryCounters
    {
        public uint Size;
        public uint PageFaultCount;
        public nuint PeakWorkingSetSize;
        public nuint WorkingSetSize;
        public nuint QuotaPeakPagedPoolUsage;
        public nuint QuotaPagedPoolUsage;
        public nuint QuotaPeakNonPagedPoolUsage;
        public nuint QuotaNonPagedPoolUsage;
        public nuint PagefileUsage;
        public nuint PeakPagefileUsage;
    }

    [SupportedOSPlatform("windows")]
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool K32GetProcessMemoryInfo(IntPtr process, ref ProcessMemoryCounters counters, uint size);
}
