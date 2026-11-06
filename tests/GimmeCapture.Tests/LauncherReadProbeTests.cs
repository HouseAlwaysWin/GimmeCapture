using System.Diagnostics;
using System.Runtime.InteropServices;
using GimmeCapture.Launcher;
using Microsoft.Win32.SafeHandles;
using Xunit.Abstractions;

namespace GimmeCapture.Tests;

/// <summary>
/// How long the sign-in launcher's read of the installed app's start-up files takes from a hard disk that other programs
/// read at the same time, as at sign-in, on 1, 2, 4 and 8 threads (<see cref="ReadAhead.ReadAll"/>). Each way reads its
/// own cold copy of the files (written past the cache by robocopy /J, then pushed out of the drive's own cache), once with
/// the disk to itself and once while threads of the probe read 64 KB at random places of a file of its own, past the
/// cache. Opt-in (LAUNCHER_READ_PROBE=1): it writes copies beside the tests. LAUNCHER_READ_PROBE_APP names the installed
/// app's folder (D:\GimmeCapture_win-x64), LAUNCHER_READ_PROBE_LOAD the threads that keep the disk busy (8) and
/// LAUNCHER_READ_PROBE_ROUNDS how often each way runs (2).
/// <para>
/// On the user's hard disk on 2026-11-06, for the 82 files (133.5 MB) of a start to the tray, with eight threads of load:
/// one thread 10.2-12.0 s, two 5.4-6.3 s, four 4.1-4.8 s, eight 2.9-4.6 s; with the disk to itself 3.5-4.0, 2.5-2.8,
/// 2.0-2.2 and 2.0-2.4 s. Eight took more from the load (54-64 of its reads a second against 65-70 with four, 95 with
/// one).
/// v0.74.0 read only the stretches of each file its last start had used (70.1 MB in 134 stretches, joined where less than
/// 256 KB apart), one file after another: 35.8 s with eight threads of load; with four, 15.3-15.8 s against 7.4-8.2 for
/// the whole files; with the disk to itself 3.9-4.0 s, no faster than the whole files. On four threads the stretches
/// took 6.0-6.3 s with four threads of load and 8.5 s with eight; joined where less than 1 MB apart (86.6 MB in 92),
/// 6.9-7.7 and 10.2 s; read with read-ahead, in one ReadFile each or through PrefetchVirtualMemory, 14.3-16.6 s with four
/// threads of load. A busy disk makes each read wait behind the others' however small it is, and many small reads waited
/// far longer than a few whole files: at the first sign-in with them the launcher read 87 MB in 42.6 s, against 141 MB in
/// 22.6 s the day before.
/// </para>
/// </summary>
public sealed class LauncherReadProbeTests
{
    private const double MiB = 1024.0 * 1024.0;
    private const int LoadFileMegabytes = 512;
    private const int LoadReadSize = 64 * 1024;
    private static readonly int[] ThreadCounts = [1, 2, 4, 8];

    private readonly ITestOutputHelper _output;

    public LauncherReadProbeTests(ITestOutputHelper output) => _output = output;

    [OptInFact("LAUNCHER_READ_PROBE=1")]
    public void TheStartupFiles_ReadOnEachNumberOfThreads_WhileTheDiskIsBusy()
    {
        string app = Setting("LAUNCHER_READ_PROBE_APP", @"D:\GimmeCapture_win-x64");
        int loadThreads = int.Parse(Setting("LAUNCHER_READ_PROBE_LOAD", "8"));
        int rounds = int.Parse(Setting("LAUNCHER_READ_PROBE_ROUNDS", "2"));
        string version = ReadAhead.AppVersion(
            FileVersionInfo.GetVersionInfo(Path.Combine(app, ReadAhead.AppAssemblyFileName)).ProductVersion)!;
        string list = ReadAhead.StartupListPath(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GimmeCapture"), app, version);
        var names = ReadAhead.Plan(File.ReadAllLines(list), app).Select(f => Path.GetRelativePath(app, f)).ToList();
        _output.WriteLine(
            $"{list} (written {File.GetLastWriteTime(list):yyyy-MM-dd HH:mm}): {names.Count} files, " +
            $"{names.Sum(n => new FileInfo(Path.Combine(app, n)).Length) / MiB:0.0} MB");

        string root = Path.Combine(
            Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory), "launcher-read-probe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string loadFile = Path.Combine(root, "load.bin");
            WriteUncached(loadFile, LoadFileMegabytes);
            for (int round = 0; round < rounds; round++)
            {
                foreach (bool busy in new[] { false, true })
                {
                    var copies = ThreadCounts
                        .Select(t => ColdCopy(app, Path.Combine(root, $"{round}-{(busy ? "busy" : "idle")}-{t}"), names))
                        .ToList();

                    // Out of the drive's own cache too.
                    string flush = Path.Combine(root, "flush.bin");
                    WriteUncached(flush, 128);
                    File.Delete(flush);

                    for (int k = 0; k < ThreadCounts.Length; k++)
                    {
                        int i = (k + round) % ThreadCounts.Length;
                        var files = names.Select(n => Path.Combine(copies[i], n)).ToList();
                        double ms;
                        long bytes;
                        double loadRate = 0;
                        using (var load = busy ? new DiskLoad(loadFile, loadThreads) : null)
                        {
                            if (load != null)
                            {
                                Thread.Sleep(1000);
                            }

                            var watch = Stopwatch.StartNew();
                            bytes = ReadAhead.ReadAll(files, ThreadCounts[i]);
                            ms = watch.Elapsed.TotalMilliseconds;
                            loadRate = load?.ReadsPerSecond ?? 0;
                        }

                        // What the start would then find: the files read again, from memory.
                        var again = Stopwatch.StartNew();
                        ReadAhead.ReadAll(files, 1);
                        double againMs = again.Elapsed.TotalMilliseconds;

                        _output.WriteLine(
                            $"round {round + 1} {(busy ? "busy" : "idle")}  {ThreadCounts[i]} thread(s): {bytes / MiB:0.0} MB in " +
                            $"{ms,6:0} ms = {bytes / MiB / (ms / 1000),5:0.0} MB/s; read again in {againMs:0} ms" +
                            (busy ? $"; the load's reads {loadRate:0}/s" : ""));
                        Assert.True(bytes > 0, "Nothing was read.");
                        Directory.Delete(copies[i], recursive: true);
                    }
                }
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string Setting(string name, string fallback) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value : fallback;

    // Copies past the cache, so nothing of them is in memory yet.
    private static string ColdCopy(string source, string destination, IEnumerable<string> names)
    {
        foreach (var folder in names.GroupBy(n => Path.GetDirectoryName(n) ?? ""))
        {
            using var robocopy = Process.Start(new ProcessStartInfo(
                "robocopy",
                $"\"{Path.Combine(source, folder.Key)}\" \"{Path.Combine(destination, folder.Key)}\" " +
                string.Join(' ', folder.Select(n => $"\"{Path.GetFileName(n)}\"")) + " /J /NJH /NJS /NFL /NDL /NP")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            })!;
            robocopy.WaitForExit();
            // 0-7 is success, 8 and up a failure.
            Assert.InRange(robocopy.ExitCode, 0, 7);
        }

        return destination;
    }

    private static void WriteUncached(string path, int megabytes)
    {
        using var handle = Open(path, GenericWrite, CreateAlways, FileFlagNoBuffering | FileFlagWriteThrough);
        var block = AlignedBlock(1 << 20, out _);
        Random.Shared.NextBytes(block.Span);
        for (long i = 0; i < megabytes; i++)
        {
            RandomAccess.Write(handle, block.Span, i << 20);
        }
    }

    // A pinned block whose start is page-aligned, as reads and writes past the cache need.
    private static Memory<byte> AlignedBlock(int size, out byte[] array)
    {
        array = GC.AllocateArray<byte>(size + 4096, pinned: true);
        long address = Marshal.UnsafeAddrOfPinnedArrayElement(array, 0);
        int skip = (int)((4096 - address % 4096) % 4096);
        return array.AsMemory(skip, size);
    }

    private static SafeFileHandle Open(string path, uint access, uint disposition, uint flags)
    {
        var handle = CreateFileW(path, access, FileShareRead | FileShareWrite | FileShareDelete, IntPtr.Zero, disposition, flags, IntPtr.Zero);
        Assert.False(handle.IsInvalid, $"Cannot open {path}: {Marshal.GetLastWin32Error()}");
        return handle;
    }

    // Threads reading 64 KB at random places of a file, past the cache, as long as it runs.
    private sealed class DiskLoad : IDisposable
    {
        private readonly List<Thread> _threads = new();
        private readonly Stopwatch _watch = Stopwatch.StartNew();
        private volatile bool _stop;
        private long _reads;

        public DiskLoad(string file, int threads)
        {
            long blocks = new FileInfo(file).Length / LoadReadSize;
            for (int t = 0; t < threads; t++)
            {
                int seed = t + 1;
                var thread = new Thread(() =>
                {
                    using var handle = Open(file, GenericRead, OpenExisting, FileFlagNoBuffering);
                    var block = AlignedBlock(LoadReadSize, out var keep);
                    var random = new Random(seed);
                    while (!_stop)
                    {
                        RandomAccess.Read(handle, block.Span, random.NextInt64(blocks) * LoadReadSize);
                        Interlocked.Increment(ref _reads);
                    }

                    GC.KeepAlive(keep);
                })
                { IsBackground = true, Name = "Disk load " + t };
                thread.Start();
                _threads.Add(thread);
            }
        }

        public double ReadsPerSecond => Interlocked.Read(ref _reads) / _watch.Elapsed.TotalSeconds;

        public void Dispose()
        {
            _stop = true;
            _threads.ForEach(t => t.Join());
        }
    }

    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint FileShareRead = 0x1;
    private const uint FileShareWrite = 0x2;
    private const uint FileShareDelete = 0x4;
    private const uint CreateAlways = 2;
    private const uint OpenExisting = 3;
    private const uint FileFlagNoBuffering = 0x20000000;
    private const uint FileFlagWriteThrough = 0x80000000;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(
        string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
}
