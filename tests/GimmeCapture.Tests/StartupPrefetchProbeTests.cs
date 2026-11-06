using System.Diagnostics;
using System.Runtime.InteropServices;
using GimmeCapture.Launcher;
using GimmeCapture.Services.Interop;
using Xunit.Abstractions;

namespace GimmeCapture.Tests;

/// <summary>
/// What reading files through first does for the page faults the loader takes on them, on the disk this test runs from.
/// Cold copies of a few DLLs (written past the cache by robocopy /J) are each mapped as an image, the way the loader maps
/// them, and 40% of their pages faulted in a scattered order: one set straight away, one after reading them through one
/// at a time (<see cref="ReadAhead.ReadAll"/>). Opt-in (STARTUP_PREFETCH_PROBE=1): it writes copies beside the tests, and
/// on an SSD both read about the same. On the user's hard disk on 2026-11-02, for four start-up DLLs of the installed app
/// (72.7 MB): 2.5 s straight away, 22 ms after reading them through in 1 s.
/// </summary>
public sealed class StartupPrefetchProbeTests
{
    private static readonly string[] Names =
        ["GimmeCapture.dll", "Avalonia.Base.dll", "Avalonia.Controls.dll", "ReactiveUI.dll", "System.Reactive.dll", "SkiaSharp.dll"];

    private readonly ITestOutputHelper _output;

    public StartupPrefetchProbeTests(ITestOutputHelper output) => _output = output;

    [OptInFact("STARTUP_PREFETCH_PROBE=1")]
    public void FaultsOnFilesReadThroughFirst_AreServedFromTheCache()
    {
        string source = Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);
        string root = Path.Combine(source, "prefetch-probe-" + Guid.NewGuid().ToString("N"));
        try
        {
            string straight = ColdCopy(source, Path.Combine(root, "straight"));
            string readFirst = ColdCopy(source, Path.Combine(root, "read-first"));
            _output.WriteLine($"disk: {StartupWaitCounters.DriveKind(root) ?? "unknown"}");

            var (straightMs, megabytes) = FaultAll(straight);

            var watch = Stopwatch.StartNew();
            long read = ReadAhead.ReadAll(Directory.GetFiles(readFirst, "*.dll"), 1);
            double readMs = watch.Elapsed.TotalMilliseconds;
            var (afterReadMs, _) = FaultAll(readFirst);

            _output.WriteLine(
                $"{Directory.GetFiles(straight).Length} DLLs, {megabytes:0.0} MB mapped: faulting 40% of their pages took " +
                $"{straightMs:0} ms straight away, {afterReadMs:0} ms after reading them through in {readMs:0} ms");
            Assert.Equal(Directory.GetFiles(readFirst).Sum(f => new FileInfo(f).Length), read);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // Copies past the cache, so nothing of them is in memory yet.
    private static string ColdCopy(string source, string destination)
    {
        var names = Names.Where(n => File.Exists(Path.Combine(source, n))).Select(n => $"\"{n}\"");
        using var robocopy = Process.Start(new ProcessStartInfo(
            "robocopy", $"\"{source}\" \"{destination}\" {string.Join(' ', names)} /J /NJH /NJS /NFL /NDL /NP")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        })!;
        robocopy.WaitForExit();
        // 0-7 is success, 8 and up a failure.
        Assert.InRange(robocopy.ExitCode, 0, 7);
        return destination;
    }

    private static (double Ms, double Megabytes) FaultAll(string folder)
    {
        double ms = 0;
        long bytes = 0;
        foreach (string file in Directory.GetFiles(folder, "*.dll").Order())
        {
            IntPtr handle = CreateFileW(file, 0x80000000, 0x1 | 0x4, IntPtr.Zero, 3, 0, IntPtr.Zero);
            IntPtr mapping = CreateFileMappingW(handle, IntPtr.Zero, 0x02 | 0x1000000, 0, 0, IntPtr.Zero);
            IntPtr view = MapViewOfFile(mapping, 0x4, 0, 0, UIntPtr.Zero);
            Assert.NotEqual(IntPtr.Zero, view);
            try
            {
                var pages = ReadablePages(view);
                bytes += pages.Count * 4096L;
                new Random(11).Shuffle(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(pages));
                var watch = Stopwatch.StartNew();
                int sum = 0;
                for (int i = 0; i < pages.Count * 2 / 5; i++)
                {
                    sum += Marshal.ReadByte(view, pages[i] * 4096);
                }

                ms += watch.Elapsed.TotalMilliseconds;
                GC.KeepAlive(sum);
            }
            finally
            {
                UnmapViewOfFile(view);
                CloseHandle(mapping);
                CloseHandle(handle);
            }
        }

        return (ms, bytes / (1024.0 * 1024.0));
    }

    // An image maps some of its pages as no-access: only committed, readable ones.
    private static List<int> ReadablePages(IntPtr view)
    {
        var pages = new List<int>();
        IntPtr address = view;
        while (VirtualQuery(address, out var region, Marshal.SizeOf<MemoryBasicInformation>()) != 0
            && region.AllocationBase == view)
        {
            bool readable = region.State == 0x1000 && region.Protect != 0 && (region.Protect & (0x01 | 0x100)) == 0;
            long first = (region.BaseAddress - view) / 4096;
            for (long page = 0; readable && page < (long)region.RegionSize / 4096; page++)
            {
                pages.Add((int)(first + page));
            }

            address = region.BaseAddress + (nint)region.RegionSize;
        }

        return pages;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryBasicInformation
    {
        public IntPtr BaseAddress;
        public IntPtr AllocationBase;
        public uint AllocationProtect;
        public ushort PartitionId;
        public UIntPtr RegionSize;
        public uint State;
        public uint Protect;
        public uint Type;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateFileMappingW(IntPtr file, IntPtr security, uint protect, uint sizeHigh, uint sizeLow, IntPtr name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr MapViewOfFile(IntPtr mapping, uint access, uint offsetHigh, uint offsetLow, UIntPtr size);

    [DllImport("kernel32.dll")]
    private static extern bool UnmapViewOfFile(IntPtr view);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll")]
    private static extern nint VirtualQuery(IntPtr address, out MemoryBasicInformation info, nint length);
}
