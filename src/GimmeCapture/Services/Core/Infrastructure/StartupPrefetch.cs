using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using GimmeCapture.Launcher;

namespace GimmeCapture.Services.Core.Infrastructure;

/// <summary>
/// Reads the files a start maps from the app's folder ahead of it, each in one sequential pass. Started at sign-in from
/// a hard disk, the app took 46 s until its hotkeys worked, at 1.2 s of CPU: 4,188 page faults read from the disk one
/// small random read at a time, while Steam and Everything read the same disk. A page the loader faults in from a file
/// already in the cache takes microseconds instead: faulting 40% of four start-up DLLs from cold copies on that disk took
/// 2.5 s, and 22 ms after one sequential read of them in 1 s (<c>StartupPrefetchProbeTests</c>). So each start writes
/// down which of the app's files it mapped, those loaded before Main first, and the next start reads them, whole and
/// several at a time (<c>ReadAhead.ReadAll</c>), from the top of Main. At sign-in the launcher reads them before it
/// starts the app (<c>ReadAhead.Plan</c>): read from here, alongside the start's own faults, they were not done by the
/// time the hotkeys worked. Windows only, where the app runs from a folder of files.
/// </summary>
internal static class StartupPrefetch
{
    /// <summary>
    /// The most <see cref="RecordWarmUp"/> adds to a list. The warm-up at the 2026-11-06 sign-in loaded 2.4 MB the list
    /// did not have; more than this was loaded by something else meanwhile, a capture's OCR or a video's FFmpeg (190 MB),
    /// which every later sign-in would then read.
    /// </summary>
    internal const long WarmUpLimit = 16L << 20;

    private static string? _appDirectory;
    private static string? _listPath;
    private static IReadOnlyList<string> _loadedBeforeMain = Array.Empty<string>();
    private static long _startedAt;
    private static long _finishedAt;
    private static long _bytesRead;
    private static bool _hadList;
    private static bool _failed;
    private static bool _recorded;

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
                AppStoragePaths.GetVersionedDataDirectory(appDirectory, AppVersionInfo.CurrentVersion), ReadAhead.StartupListFileName);
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

            Volatile.Write(ref _recorded, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception
            or InvalidOperationException)
        {
            AppLog.Debug("StartupPrefetch.Record", ex);
        }
    }

    /// <summary>
    /// After the idle warm-up of a start to the tray (<c>SnipWindowFactory.ScheduleWarmUp</c>): adds the files the app
    /// has loaded since <see cref="Record"/> to the list it wrote, so the next sign-in's launcher reads them with the rest.
    /// At the 2026-11-06 sign-in the warm-up, 11 s in, held the UI thread for 637 ms, 2% of it on the CPU and 1,343 page
    /// faults, loading HarfBuzz, Avalonia.Markup and UI Automation's files, which no start-up read had read.
    /// </summary>
    internal static void RecordWarmUp()
    {
        if (!Volatile.Read(ref _recorded) || _appDirectory is not { } appDirectory || _listPath is not { } listPath)
        {
            return;
        }

        try
        {
            var loaded = Order(Array.Empty<string>(), LoadedModules(appDirectory), LoadedAssemblies(appDirectory));
            var sizes = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            var lines = WithWarmUp(File.ReadAllLines(listPath), loaded, name => sizes[name] = FileSize(appDirectory, name), WarmUpLimit);
            if (lines == null)
            {
                if (sizes.Count > 0)
                {
                    AppLog.Debug("StartupPrefetch.RecordWarmUp", $"left out {sizes.Count} files: more than a warm-up loads");
                }

                return;
            }

            File.WriteAllText(listPath + ".tmp", string.Join(Environment.NewLine, lines) + Environment.NewLine);
            File.Move(listPath + ".tmp", listPath, overwrite: true);
            long added = 0;
            foreach (long size in sizes.Values)
            {
                added += size;
            }

            AppLog.Information(
                "StartupPrefetch.WarmUp",
                string.Create(CultureInfo.InvariantCulture, $"the start-up list gained {sizes.Count} files, {added / (1024.0 * 1024.0):F1} MB"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception
            or InvalidOperationException)
        {
            AppLog.Debug("StartupPrefetch.RecordWarmUp", ex);
        }
    }

    /// <summary>
    /// <paramref name="list"/>, a start-up list's lines, with the files of <paramref name="loaded"/> it does not have
    /// after them, in their order: null when there are none, or when they come to more than <paramref name="limit"/>
    /// bytes (<paramref name="sizeOf"/> each).
    /// </summary>
    internal static List<string>? WithWarmUp(
        IReadOnlyList<string> list, IEnumerable<string> loaded, Func<string, long> sizeOf, long limit)
    {
        var lines = new List<string>(list.Count);
        var listed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string line in list)
        {
            string name = line.Trim();
            if (name.Length > 0)
            {
                lines.Add(name);
                listed.Add(name);
            }
        }

        int count = lines.Count;
        long bytes = 0;
        foreach (string name in loaded)
        {
            if (listed.Add(name))
            {
                lines.Add(name);
                bytes += sizeOf(name);
            }
        }

        return lines.Count > count && bytes <= limit ? lines : null;
    }

    private static long FileSize(string appDirectory, string name)
    {
        try
        {
            return new FileInfo(Path.Combine(appDirectory, name)).Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
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

    private static void Run()
    {
        try
        {
            // Only the runtime has loaded anything yet: these go first in the next list.
            Volatile.Write(ref _loadedBeforeMain, LoadedAssemblies(_appDirectory!));
            if (File.Exists(_listPath))
            {
                Volatile.Write(ref _hadList, true);
                Interlocked.Exchange(
                    ref _bytesRead,
                    ReadAhead.ReadAll(ReadAhead.Resolve(File.ReadAllLines(_listPath!), _appDirectory!), ReadAhead.ReadThreads));
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
            if (!assembly.IsDynamic && ReadAhead.RelativeToApp(appDirectory, assembly.Location) is { } name)
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
            if (ReadAhead.RelativeToApp(appDirectory, module.FileName) is { } name)
            {
                names.Add(name);
            }
        }

        return names;
    }
}
