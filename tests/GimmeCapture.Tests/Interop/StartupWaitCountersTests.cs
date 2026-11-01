using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using GimmeCapture.Services.Interop;

namespace GimmeCapture.Tests;

// What Startup.Timings says a slow start waited on.
public sealed class StartupWaitCountersTests
{
    private const int EntrySize = 256;

    [Fact]
    public void FindPageFaults_ReadsTheEntryOfTheProcessAskedFor()
    {
        byte[] snapshot = Snapshot((4, 7, 100), (1234, 99, 5000), (88, 1, 2));

        Assert.Equal(new StartupWaitCounters.PageFaults(99, 5000), StartupWaitCounters.FindPageFaults(snapshot, 1234));
        Assert.Equal(new StartupWaitCounters.PageFaults(7, 100), StartupWaitCounters.FindPageFaults(snapshot, 4));
        Assert.Equal(new StartupWaitCounters.PageFaults(1, 2), StartupWaitCounters.FindPageFaults(snapshot, 88));
        Assert.Null(StartupWaitCounters.FindPageFaults(snapshot, 999));
    }

    [Fact]
    public void FindPageFaults_StopsAtAnEntryPastTheEnd()
    {
        byte[] snapshot = Snapshot((4, 7, 100), (1234, 99, 5000));
        BinaryPrimitives.WriteUInt32LittleEndian(snapshot, 10_000);

        Assert.Null(StartupWaitCounters.FindPageFaults(snapshot, 1234));
        Assert.Null(StartupWaitCounters.FindPageFaults(snapshot.AsSpan(0, 100), 4));
    }

    [WindowsFact]
    public void ReadPageFaults_FindsThisProcess_WithTheCountWindowsKeeps()
    {
        uint before = PageFaultCount();
        var faults = StartupWaitCounters.ReadPageFaults();
        uint after = PageFaultCount();

        // Page faults only add up, so the snapshot's count lies between two read around it.
        Assert.NotNull(faults);
        Assert.InRange(faults.Value.All, before, after);
        Assert.True(faults.Value.FromDisk <= faults.Value.All);
    }

    [Fact]
    public void BusyPercent_IsTheBusyShareOfTheTimeBetweenTwoReadings()
    {
        var start = new StartupWaitCounters.SystemCpuTimes(Busy: 100, Total: 1000);

        Assert.Equal(50, StartupWaitCounters.SystemCpuTimes.BusyPercent(start, new(Busy: 600, Total: 2000)));
        Assert.Equal(0, StartupWaitCounters.SystemCpuTimes.BusyPercent(start, new(Busy: 100, Total: 1400)));
        Assert.Null(StartupWaitCounters.SystemCpuTimes.BusyPercent(start, start));
    }

    [WindowsFact]
    public void ReadSystemCpuTimes_Advances()
    {
        var start = StartupWaitCounters.ReadSystemCpuTimes();
        Assert.NotNull(start);

        // The clock behind it moves in steps of about 16 ms.
        var waited = Stopwatch.StartNew();
        StartupWaitCounters.SystemCpuTimes? end;
        do
        {
            end = StartupWaitCounters.ReadSystemCpuTimes();
        }
        while (end?.Total == start.Value.Total && waited.Elapsed < TimeSpan.FromSeconds(5));

        Assert.NotNull(end);
        Assert.True(end.Value.Total > start.Value.Total);
        Assert.InRange(StartupWaitCounters.SystemCpuTimes.BusyPercent(start.Value, end.Value)!.Value, 0, 100);
    }

    [Fact]
    public void DriveKind_IsUnknown_WithoutADriveLetter()
    {
        Assert.Null(StartupWaitCounters.DriveKind(@"\\server\share\GimmeCapture"));
    }

    [WindowsFact]
    public void DriveKind_OfTheTestsFolder_AsksTheDriveWithoutFailing()
    {
        // Which it is depends on the machine, and a virtual disk may not say.
        Assert.Contains(StartupWaitCounters.DriveKind(AppContext.BaseDirectory), new[] { "hdd", "ssd", null });
    }

    [Theory]
    [InlineData(0x20u, null)] // normal
    [InlineData(0x8000u, null)] // above normal
    [InlineData(0x80u, null)] // high
    [InlineData(0x4000u, "belowNormal")]
    [InlineData(0x40u, "idle")]
    public void LoweredCpuPriority_NamesOnlyTheClassesBelowNormal(uint priorityClass, string? expected)
    {
        Assert.Equal(expected, StartupWaitCounters.LoweredCpuPriority(priorityClass));
    }

    [Theory]
    [InlineData(0, "veryLow")]
    [InlineData(1, "low")]
    [InlineData(2, null)] // normal
    [InlineData(3, null)] // high
    public void LoweredIoPriority_NamesOnlyTheHintsBelowNormal(int hint, string? expected)
    {
        Assert.Equal(expected, StartupWaitCounters.LoweredIoPriority(hint));
    }

    [WindowsFact]
    public void LoweredPriority_ReadsTheClassWindowsGivesThisProcess()
    {
        // What started the test host decides its priority: a GitHub runner starts it below normal, with low I/O.
        using var process = Process.GetCurrentProcess();
        string? expected = process.PriorityClass switch
        {
            ProcessPriorityClass.Idle => "idle",
            ProcessPriorityClass.BelowNormal => "belowNormal",
            _ => null,
        };

        var (cpu, io) = StartupWaitCounters.LoweredPriority();

        Assert.Equal(expected, cpu);
        // .NET has no reading of a process's I/O priority to compare with; the hints are named above.
        Assert.Contains(io, new[] { null, "low", "veryLow" });
    }

    private static byte[] Snapshot(params (long ProcessId, uint FromDisk, uint All)[] processes)
    {
        var buffer = new byte[EntrySize * processes.Length];
        for (int i = 0; i < processes.Length; i++)
        {
            var entry = buffer.AsSpan(i * EntrySize, EntrySize);
            BinaryPrimitives.WriteUInt32LittleEndian(entry, i == processes.Length - 1 ? 0u : EntrySize);
            BinaryPrimitives.WriteUInt32LittleEndian(entry[16..], processes[i].FromDisk);
            BinaryPrimitives.WriteInt64LittleEndian(entry[80..], processes[i].ProcessId);
            BinaryPrimitives.WriteUInt32LittleEndian(entry[128..], processes[i].All);
        }

        return buffer;
    }

    private static uint PageFaultCount()
    {
        Assert.True(K32GetProcessMemoryInfo(Kernel32.GetCurrentProcess(), out var counters, (uint)Marshal.SizeOf<ProcessMemoryCounters>()));
        return counters.PageFaultCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessMemoryCounters
    {
        public uint Cb;
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

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool K32GetProcessMemoryInfo(IntPtr process, out ProcessMemoryCounters counters, uint size);
}
