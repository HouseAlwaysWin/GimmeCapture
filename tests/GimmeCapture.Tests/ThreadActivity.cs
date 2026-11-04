using System.Diagnostics;
using System.Runtime.InteropServices;

namespace GimmeCapture.Tests;

/// <summary>
/// How often each of this process's threads wakes (its context switches) and how many CPU cycles it takes, for the
/// probes that measure what sits idle on the UI thread. Windows only.
/// </summary>
internal static class ThreadActivity
{
    internal readonly record struct Rate(uint Thread, double Wakeups, double MegaCycles, bool New);

    internal static uint CurrentThreadId => GetCurrentThreadId();

    // Each thread's rate while `run` runs, busiest first. Thread times advance in scheduler ticks (15.6 ms), so a thread
    // that wakes for microseconds at a time reads as using none; its cycle count is exact.
    internal static List<Rate> Measure(Action run)
    {
        var before = Snapshot();
        var started = Stopwatch.GetTimestamp();
        run();
        double seconds = Stopwatch.GetElapsedTime(started).TotalSeconds;
        var after = Snapshot();
        return after
            .Select(t =>
            {
                var was = before.TryGetValue(t.Key, out var b) ? b : default;
                return new Rate(t.Key, (t.Value.Switches - was.Switches) / seconds, (t.Value.Cycles - was.Cycles) / 1e6 / seconds, !before.ContainsKey(t.Key));
            })
            .Where(t => t.Wakeups > 0.05)
            .OrderByDescending(t => t.Wakeups)
            .ToList();
    }

    // Context switches and CPU cycles of each of this process's threads. The switches come from the snapshot of every
    // process (SYSTEM_PROCESS_INFORMATION, 256 bytes on 64-bit Windows, then one 80-byte SYSTEM_THREAD_INFORMATION a
    // thread).
    private static Dictionary<uint, (long Switches, long Cycles)> Snapshot()
    {
        int size = 4 << 20;
        while (true)
        {
            IntPtr buffer = Marshal.AllocHGlobal(size);
            try
            {
                int status = NtQuerySystemInformation(5, buffer, size, out int written);
                if (status == unchecked((int)0xC0000004))
                {
                    size = Math.Max(size * 2, written + (1 << 20));
                    continue;
                }

                Assert.Equal(0, status);
                long offset = 0;
                while (true)
                {
                    IntPtr entry = buffer + (int)offset;
                    if (Marshal.ReadInt64(entry, 80) == Environment.ProcessId)
                    {
                        var threads = new Dictionary<uint, (long, long)>();
                        int count = Marshal.ReadInt32(entry, 4);
                        for (int i = 0; i < count; i++)
                        {
                            IntPtr thread = entry + 256 + (i * 80);
                            uint id = (uint)Marshal.ReadInt64(thread, 48);
                            threads[id] = ((uint)Marshal.ReadInt32(thread, 64), Cycles(id));
                        }

                        return threads;
                    }

                    uint next = (uint)Marshal.ReadInt32(entry, 0);
                    Assert.NotEqual(0u, next);
                    offset += next;
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
    }

    // 0 for a thread gone since the snapshot.
    private static long Cycles(uint threadId)
    {
        IntPtr handle = OpenThread(0x0800, false, threadId);
        if (handle == IntPtr.Zero)
        {
            return 0;
        }

        try
        {
            return QueryThreadCycleTime(handle, out ulong cycles) ? (long)cycles : 0;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    internal static string Describe(uint threadId)
    {
        IntPtr handle = OpenThread(0x0800, false, threadId);
        if (handle == IntPtr.Zero)
        {
            return "?";
        }

        try
        {
            if (GetThreadDescription(handle, out IntPtr text) < 0 || text == IntPtr.Zero)
            {
                return "?";
            }

            string name = Marshal.PtrToStringUni(text) ?? "";
            LocalFree(text);
            return name.Length > 0 ? name : "(no name)";
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    [DllImport("ntdll.dll")]
    private static extern int NtQuerySystemInformation(int infoClass, IntPtr buffer, int length, out int written);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenThread(uint access, bool inherit, uint threadId);

    [DllImport("kernel32.dll")]
    private static extern bool QueryThreadCycleTime(IntPtr thread, out ulong cycles);

    [DllImport("kernel32.dll")]
    private static extern int GetThreadDescription(IntPtr thread, out IntPtr description);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);
}
