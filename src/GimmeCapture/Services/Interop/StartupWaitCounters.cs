using System;
using System.Buffers.Binary;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace GimmeCapture.Services.Interop;

/// <summary>
/// What a slow start waited on, for <see cref="Core.Infrastructure.StartupTrace"/>: how busy the machine's processors
/// were, how many of the process's pages had to be read from disk, whether the app's drive has seek time (a hard disk),
/// and a priority Windows lowered. A launch at sign-in took 67-77 s on a desktop whose app drive is a hard disk, against
/// 0.5-7 s at other times, and the log could not tell waiting from working. Windows only; elsewhere every reading is
/// null.
/// </summary>
internal static class StartupWaitCounters
{
    private const int SystemProcessInformation = 5;
    private const int StatusInfoLengthMismatch = unchecked((int)0xC0000004);
    private const int ProcessIoPriority = 33;
    private const int IoPriorityVeryLow = 0;
    private const int IoPriorityLow = 1;
    private const uint IdlePriorityClass = 0x40;
    private const uint BelowNormalPriorityClass = 0x4000;
    private const uint FileShareReadWrite = 0x1 | 0x2;
    private const uint OpenExisting = 3;
    private const uint IoctlStorageQueryProperty = 0x2D1400;
    private const int StorageDeviceSeekPenaltyProperty = 7;

    // SYSTEM_PROCESS_INFORMATION on 64-bit Windows 7 and later. winternl.h names the 48 bytes after NumberOfThreads
    // Reserved1, and they start with WorkingSetPrivateSize and HardFaultCount; PageFaultCount is its Reserved4.
    private const int NextEntryOffsetField = 0;
    private const int HardFaultCountField = 16;
    private const int UniqueProcessIdField = 80;
    private const int PageFaultCountField = 128;
    private const int ProcessEntryMinimum = PageFaultCountField + sizeof(uint);

    /// <summary>The machine's processor time so far, summed over its processors, in 100 ns units.</summary>
    internal readonly record struct SystemCpuTimes(long Busy, long Total)
    {
        /// <summary>How busy the processors were between two readings, in percent; null when no time passed.</summary>
        internal static int? BusyPercent(SystemCpuTimes start, SystemCpuTimes end)
        {
            long total = end.Total - start.Total;
            return total <= 0 ? null : (int)Math.Round(100.0 * (end.Busy - start.Busy) / total);
        }
    }

    /// <summary>The process's page faults so far: those that read from disk, and all of them.</summary>
    internal readonly record struct PageFaults(uint FromDisk, uint All);

    internal static SystemCpuTimes? ReadSystemCpuTimes()
    {
        if (!OperatingSystem.IsWindows() || !GetSystemTimes(out long idle, out long kernel, out long user))
        {
            return null;
        }

        // Kernel time includes the idle time.
        long total = kernel + user;
        return new SystemCpuTimes(total - idle, total);
    }

    /// <summary>
    /// This process's page faults, from a snapshot of every process on the machine (a few ms): Windows keeps the count of
    /// hard faults nowhere else a process can read without tracing.
    /// </summary>
    internal static unsafe PageFaults? ReadPageFaults()
    {
        if (!OperatingSystem.IsWindows() || !Environment.Is64BitProcess)
        {
            return null;
        }

        int size = 1024 * 1024;
        for (int attempt = 0; attempt < 4; attempt++)
        {
            void* buffer = NativeMemory.Alloc((nuint)size);
            try
            {
                int status = NtQuerySystemInformation(SystemProcessInformation, buffer, size, out int written);
                if (status == StatusInfoLengthMismatch)
                {
                    // Processes can start before the next try.
                    size = Math.Max(size * 2, written + 256 * 1024);
                    continue;
                }

                return status == 0
                    ? FindPageFaults(new ReadOnlySpan<byte>(buffer, Math.Min(written, size)), Environment.ProcessId)
                    : null;
            }
            finally
            {
                NativeMemory.Free(buffer);
            }
        }

        return null;
    }

    /// <summary>A process's page faults from a <c>SystemProcessInformation</c> snapshot; null when it is not there.</summary>
    internal static PageFaults? FindPageFaults(ReadOnlySpan<byte> processes, long processId)
    {
        int offset = 0;
        while (offset >= 0 && offset <= processes.Length - ProcessEntryMinimum)
        {
            var entry = processes[offset..];
            if (BinaryPrimitives.ReadInt64LittleEndian(entry[UniqueProcessIdField..]) == processId)
            {
                return new PageFaults(
                    BinaryPrimitives.ReadUInt32LittleEndian(entry[HardFaultCountField..]),
                    BinaryPrimitives.ReadUInt32LittleEndian(entry[PageFaultCountField..]));
            }

            uint next = BinaryPrimitives.ReadUInt32LittleEndian(entry[NextEntryOffsetField..]);
            if (next == 0)
            {
                break;
            }

            offset += (int)next;
        }

        return null;
    }

    /// <summary>"hdd" when the drive a path is on reports seek time, "ssd" when it does not; null when it cannot say.</summary>
    internal static unsafe string? DriveKind(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            string? root = Path.GetPathRoot(Path.GetFullPath(path));
            if (root is not { Length: >= 2 } || root[1] != ':')
            {
                return null;
            }

            // No access asked for: enough to query the device, and allowed without elevation.
            using SafeFileHandle volume = CreateFileW(@"\\.\" + root[..2], 0, FileShareReadWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
            if (volume.IsInvalid)
            {
                return null;
            }

            // STORAGE_PROPERTY_QUERY in, DEVICE_SEEK_PENALTY_DESCRIPTOR (Version, Size, IncursSeekPenalty) out.
            int* query = stackalloc int[3] { StorageDeviceSeekPenaltyProperty, 0, 0 };
            int* descriptor = stackalloc int[3];
            if (!DeviceIoControl(volume, IoctlStorageQueryProperty, query, 3 * sizeof(int), descriptor, 3 * sizeof(int), out int returned, IntPtr.Zero)
                || returned < 2 * sizeof(int) + 1)
            {
                return null;
            }

            return (descriptor[2] & 0xFF) != 0 ? "hdd" : "ssd";
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>The process's CPU priority class and I/O priority, each null unless lowered.</summary>
    internal static (string? Cpu, string? Io) LoweredPriority()
    {
        if (!OperatingSystem.IsWindows())
        {
            return (null, null);
        }

        IntPtr process = Kernel32.GetCurrentProcess();
        string? io = NtQueryInformationProcess(process, ProcessIoPriority, out int hint, sizeof(int), out _) == 0
            ? LoweredIoPriority(hint)
            : null;
        return (LoweredCpuPriority(GetPriorityClass(process)), io);
    }

    /// <summary>The name of a priority class below normal; null for normal and above.</summary>
    internal static string? LoweredCpuPriority(uint priorityClass) => priorityClass switch
    {
        IdlePriorityClass => "idle",
        BelowNormalPriorityClass => "belowNormal",
        _ => null,
    };

    /// <summary>The name of an I/O priority hint below normal; null for normal and above.</summary>
    internal static string? LoweredIoPriority(int hint) => hint switch
    {
        IoPriorityVeryLow => "veryLow",
        IoPriorityLow => "low",
        _ => null,
    };

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out long idleTime, out long kernelTime, out long userTime);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetPriorityClass(IntPtr hProcess);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(
        string lpFileName, uint dwDesiredAccess, uint dwShareMode, IntPtr lpSecurityAttributes,
        uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern unsafe bool DeviceIoControl(
        SafeFileHandle hDevice, uint dwIoControlCode, void* lpInBuffer, int nInBufferSize,
        void* lpOutBuffer, int nOutBufferSize, out int lpBytesReturned, IntPtr lpOverlapped);

    [DllImport("ntdll.dll")]
    private static extern unsafe int NtQuerySystemInformation(
        int systemInformationClass, void* systemInformation, int systemInformationLength, out int returnLength);

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(
        IntPtr processHandle, int processInformationClass, out int processInformation, int processInformationLength,
        out int returnLength);
}
