using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;

namespace GimmeCapture.Services.Core.Infrastructure;

/// <summary>
/// Reads the files a start maps from the app's folder ahead of it, each in one sequential pass. Started at sign-in from
/// a hard disk, the app took 46 s until its hotkeys worked, at 1.2 s of CPU: 4,188 page faults read from the disk one
/// small random read at a time, while Steam and Everything read the same disk. A page the loader faults in from a file
/// already in the cache takes microseconds instead: faulting 40% of four start-up DLLs from cold copies on that disk took
/// 2.5 s, and 22 ms after one sequential read of them in 1 s (<c>StartupPrefetchProbeTests</c>). So each start writes
/// down which of the app's files it mapped, those loaded before Main first, and the next start reads them on a thread of
/// its own from the top of Main. Windows only, where the app runs from a folder of files.
/// </summary>
internal static class StartupPrefetch
{
    /// <summary>Beside the version's config.json.</summary>
    internal const string ListFileName = "startup-files.txt";

    private static string? _appDirectory;
    private static string? _listPath;
    private static IReadOnlyList<string> _loadedBeforeMain = Array.Empty<string>();
    private static long _startedAt;
    private static long _finishedAt;
    private static long _bytesRead;
    private static bool _hadList;
    private static bool _failed;

    /// <summary>From the top of Main, before the log exists: nothing here may log.</summary>
    internal static void Start()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            string appDirectory = RuntimePathProvider.GetExecutableDirectory();
            _listPath = Path.Combine(
                AppStoragePaths.GetVersionedDataDirectory(appDirectory, AppVersionInfo.CurrentVersion), ListFileName);
            _appDirectory = appDirectory;
            _startedAt = Stopwatch.GetTimestamp();
            new Thread(Run) { IsBackground = true, Name = "Startup prefetch" }.Start();
        }
        catch (Exception)
        {
            // Any failure at all: a read-ahead that cannot start costs the speed-up, never the start.
            _failed = true;
        }
    }

    /// <summary>Once the start is done: writes down the files it mapped, for the next start to read.</summary>
    internal static void Record()
    {
        if (_appDirectory is not { } appDirectory || _listPath is not { } listPath)
        {
            return;
        }

        try
        {
            var files = Order(Volatile.Read(ref _loadedBeforeMain), LoadedModules(appDirectory), LoadedAssemblies(appDirectory));
            string text = string.Join(Environment.NewLine, files) + Environment.NewLine;
            if (!File.Exists(listPath) || File.ReadAllText(listPath) != text)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(listPath)!);
                File.WriteAllText(listPath + ".tmp", text);
                File.Move(listPath + ".tmp", listPath, overwrite: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception
            or InvalidOperationException)
        {
            AppLog.Debug("StartupPrefetch.Record", ex);
        }
    }

    /// <summary>What this start read, for the <c>Startup.Timings</c> line; empty where nothing was started.</summary>
    internal static string Describe()
    {
        if (_appDirectory == null)
        {
            return _failed ? " prefetch=failed" : "";
        }

        long finishedAt = Volatile.Read(ref _finishedAt);
        return Format(
            Volatile.Read(ref _failed),
            Volatile.Read(ref _hadList),
            finishedAt == 0 ? null : Stopwatch.GetElapsedTime(_startedAt, finishedAt),
            Interlocked.Read(ref _bytesRead));
    }

    /// <summary>
    /// <c>prefetch=1840ms/142MB</c>; <c>none</c> without a list yet, the first start of a version; <c>running</c> when it
    /// is not done by the end of the start.
    /// </summary>
    internal static string Format(bool failed, bool hadList, TimeSpan? took, long bytes)
    {
        if (failed)
        {
            return " prefetch=failed";
        }

        if (took is not { } elapsed)
        {
            return " prefetch=running";
        }

        return hadList
            ? string.Create(CultureInfo.InvariantCulture, $" prefetch={elapsed.TotalMilliseconds:F0}ms/{bytes / (1024.0 * 1024.0):F0}MB")
            : " prefetch=none";
    }

    /// <summary>
    /// The ones loaded before Main, then the loader's modules, then any other assembly, each once. The loader lists the
    /// runtime's assemblies beside the native DLLs, in the order they were loaded, so after the first few the list
    /// follows the start: the log's Serilog, then Avalonia, Skia and the rest.
    /// </summary>
    internal static List<string> Order(
        IEnumerable<string> loadedBeforeMain, IEnumerable<string> modules, IEnumerable<string> assemblies)
    {
        var order = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in new[] { loadedBeforeMain, modules, assemblies })
        {
            foreach (string name in group)
            {
                if (seen.Add(name))
                {
                    order.Add(name);
                }
            }
        }

        return order;
    }

    /// <summary>The list's entries as full paths inside <paramref name="appDirectory"/>; anything else is skipped.</summary>
    internal static List<string> Resolve(IEnumerable<string> lines, string appDirectory)
    {
        var files = new List<string>();
        foreach (string raw in lines)
        {
            string line = raw.Trim();
            if (line.Length == 0 || Path.IsPathRooted(line))
            {
                continue;
            }

            string path = Path.GetFullPath(Path.Combine(appDirectory, line));
            if (RelativeToApp(appDirectory, path) != null)
            {
                files.Add(path);
            }
        }

        return files;
    }

    /// <summary><paramref name="path"/> relative to <paramref name="appDirectory"/>, or null when it is not inside it.</summary>
    internal static string? RelativeToApp(string appDirectory, string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(appDirectory)) + Path.DirectorySeparatorChar;
        string full = Path.GetFullPath(path);
        return full.Length > root.Length && full.StartsWith(root, StringComparison.OrdinalIgnoreCase)
            ? full[root.Length..]
            : null;
    }

    /// <summary>Reads each file through once, in order; one that cannot be read is skipped. The bytes read.</summary>
    internal static long ReadThrough(IEnumerable<string> files)
    {
        var buffer = new byte[1 << 20];
        long total = 0;
        foreach (string file in files)
        {
            try
            {
                // No FileStream buffer of its own: it reads into this one, a megabyte at a time.
                using var stream = new FileStream(
                    file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.SequentialScan);
                int read;
                while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                {
                    total += read;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        return total;
    }

    private static void Run()
    {
        try
        {
            // Only the runtime has loaded anything yet: these go first in the next list.
            Volatile.Write(ref _loadedBeforeMain, LoadedAssemblies(_appDirectory!));
            if (File.Exists(_listPath))
            {
                Volatile.Write(ref _hadList, true);
                Interlocked.Exchange(ref _bytesRead, ReadThrough(Resolve(File.ReadAllLines(_listPath!), _appDirectory!)));
            }
        }
        catch (Exception)
        {
            // Any failure at all: an exception left on this thread would end the process.
            Volatile.Write(ref _failed, true);
        }
        finally
        {
            Volatile.Write(ref _finishedAt, Stopwatch.GetTimestamp());
        }
    }

    private static List<string> LoadedAssemblies(string appDirectory)
    {
        var names = new List<string>();
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (!assembly.IsDynamic && RelativeToApp(appDirectory, assembly.Location) is { } name)
            {
                names.Add(name);
            }
        }

        return names;
    }

    private static List<string> LoadedModules(string appDirectory)
    {
        var names = new List<string>();
        using var process = Process.GetCurrentProcess();
        foreach (ProcessModule module in process.Modules)
        {
            if (RelativeToApp(appDirectory, module.FileName) is { } name)
            {
                names.Add(name);
            }
        }

        return names;
    }
}
