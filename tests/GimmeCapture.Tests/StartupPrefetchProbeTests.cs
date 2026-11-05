using System.Diagnostics;
using System.Runtime.InteropServices;
using GimmeCapture.Launcher;
using GimmeCapture.Services.Interop;
using Xunit.Abstractions;

namespace GimmeCapture.Tests;

/// <summary>
/// What reading files first does for the page faults the loader takes on them, on the disk this test runs from. Cold
/// copies of a few DLLs (written past the cache by robocopy /J) are each mapped as an image, the way the loader maps
/// them, and 40% of their pages faulted in a scattered order: one set straight away, one after reading the files whole,
/// one after reading only the stretches of them those pages lie in (<see cref="ImagePagesInUse.ToFileRanges"/>), as
/// <see cref="StartupPrefetch.ReadAll"/> does. Opt-in (STARTUP_PREFETCH_PROBE=1): it writes copies beside the tests, and
/// on an SSD all read about the same. On the user's hard disk on 2026-11-02, for four start-up DLLs of the installed app
/// (72.7 MB): 2.5 s straight away, 22 ms after reading them through in 1 s. On 2026-11-05, five DLLs of the tests' build
/// (39.9 MB mapped): 1.3-1.9 s straight away, 12 ms after reading them whole in 0.93-0.98 s, 18 ms after reading their
/// stretches. 40% of the pages scattered over every file join into the whole files (41.8 MB either way), which then read
/// as long: what a real start saves depends on how its pages cluster. Mapping a copy as an image before reading it reads
/// much of it: the stretches once read in a quarter of the time that way, so they are worked out on another copy.
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
            // The copy written last can still be in the drive's own cache: the one read straight away, which is slowest.
            string stretchesFirst = ColdCopy(source, Path.Combine(root, "stretches-first"));
            string readFirst = ColdCopy(source, Path.Combine(root, "read-first"));
            string straight = ColdCopy(source, Path.Combine(root, "straight"));
            _output.WriteLine($"disk: {StartupWaitCounters.DriveKind(root) ?? "unknown"}");

            var (straightMs, megabytes) = FaultAll(straight);

            var watch = Stopwatch.StartNew();
            long read = StartupPrefetch.ReadAll(Directory.GetFiles(readFirst, "*.dll").Select(f => new StartupFile(f, null)));
            double readMs = watch.Elapsed.TotalMilliseconds;
            var (afterReadMs, _) = FaultAll(readFirst);

            // The stretches the faulted pages lie in, worked out on the copy already faulted, so this one is untouched.
            var stretches = Directory.GetFiles(stretchesFirst, "*.dll").Order()
                .Select(f =>
                {
                    string twin = Path.Combine(straight, Path.GetFileName(f));
                    return new StartupFile(f, StretchesOf(twin, PagesToFault(twin)));
                })
                .ToList();
            watch.Restart();
            long stretchesRead = StartupPrefetch.ReadAll(stretches);
            double stretchesMs = watch.Elapsed.TotalMilliseconds;
            var (afterStretchesMs, _) = FaultAll(stretchesFirst);

            _output.WriteLine(
                $"{Directory.GetFiles(straight).Length} DLLs, {megabytes:0.0} MB mapped: faulting 40% of their pages took " +
                $"{straightMs:0} ms straight away, {afterReadMs:0} ms after reading them whole ({read / 1e6:0.0} MB) in " +
                $"{readMs:0} ms, {afterStretchesMs:0} ms after reading their stretches ({stretchesRead / 1e6:0.0} MB) in " +
                $"{stretchesMs:0} ms");
            Assert.Equal(Directory.GetFiles(readFirst).Sum(f => new FileInfo(f).Length), read);
            Assert.True(stretchesRead <= read, "The stretches read more than the files hold.");
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
            using var image = new MappedImage(file);
            var pages = ReadablePages(image.View);
            bytes += pages.Count * 4096L;
            var toFault = Scattered(pages);
            var watch = Stopwatch.StartNew();
            int sum = 0;
            foreach (int page in toFault)
            {
                sum += Marshal.ReadByte(image.View, page * 4096);
            }

            ms += watch.Elapsed.TotalMilliseconds;
            GC.KeepAlive(sum);
        }

        return (ms, bytes / (1024.0 * 1024.0));
    }

    // The pages FaultAll faults in a file, found without touching them: its mapping's readable pages, as it finds them.
    private static List<int> PagesToFault(string file)
    {
        using var image = new MappedImage(file);
        return Scattered(ReadablePages(image.View));
    }

    // 40% of the pages, the same ones each time, in a scattered order.
    private static List<int> Scattered(List<int> pages)
    {
        new Random(11).Shuffle(CollectionsMarshal.AsSpan(pages));
        return pages.Take(pages.Count * 2 / 5).ToList();
    }

    private static List<FileRange> StretchesOf(string file, IEnumerable<int> pages)
    {
        var headers = new byte[4096];
        using (var stream = File.OpenRead(file))
        {
            stream.ReadExactly(headers);
        }

        return ImagePagesInUse.ToFileRanges(pages.Select(p => p * 4096L), ImageLayout.Parse(headers)!, ImagePagesInUse.JoinGap);
    }

    // A file mapped as an image, the way the loader maps it.
    private sealed class MappedImage : IDisposable
    {
        private readonly IntPtr _file;
        private readonly IntPtr _mapping;

        public MappedImage(string path)
        {
            _file = CreateFileW(path, 0x80000000, 0x1 | 0x4, IntPtr.Zero, 3, 0, IntPtr.Zero);
            _mapping = CreateFileMappingW(_file, IntPtr.Zero, 0x02 | 0x1000000, 0, 0, IntPtr.Zero);
            View = MapViewOfFile(_mapping, 0x4, 0, 0, UIntPtr.Zero);
            Assert.NotEqual(IntPtr.Zero, View);
        }

        public IntPtr View { get; }

        public void Dispose()
        {
            UnmapViewOfFile(View);
            CloseHandle(_mapping);
            CloseHandle(_file);
        }
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
