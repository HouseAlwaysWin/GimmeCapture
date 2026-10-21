using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace GimmeCapture.Services.Core.Infrastructure;

/// <summary>
/// Deletes the copies that single-file builds unpacked on earlier launches. A single-file .NET app unpacks itself into
/// <c>&lt;base&gt;/.net/GimmeCapture/&lt;bundle id&gt;</c> (%TEMP% on Windows, $HOME on Linux) the first time each version
/// runs, and nothing ever removes it: 478 MB per version. The Linux build is still a single file and leaves one copy
/// behind per update. The Windows build is a folder now, so every copy its single-file versions left is stale.
/// A copy in use (another install still running it) or touched in the last hour (being unpacked) is left alone.
/// </summary>
public static class BundleExtractionCleaner
{
    private static readonly TimeSpan MinimumAge = TimeSpan.FromHours(1);

    // Suffix for a copy moved aside to be deleted; on Windows the move is what proves nothing has it open.
    private const string StaleSuffix = ".stale";

    internal sealed record Layout(string Root, string? Current);

    internal sealed record SweepResult(int Removed, long BytesFreed, int InUse);

    /// <summary>Run the sweep. Never throws. Call off the UI thread once the app has settled.</summary>
    public static void SweepStaleCopies()
    {
        try
        {
            string? exePath = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exePath))
            {
                return;
            }

            var layout = ResolveLayout(
                AppContext.BaseDirectory,
                exePath,
                Environment.GetEnvironmentVariable("DOTNET_BUNDLE_EXTRACT_BASE_DIR"),
                Path.GetTempPath(),
                OperatingSystem.IsWindows());
            if (layout == null || !Directory.Exists(layout.Root))
            {
                return;
            }

            var result = Sweep(layout, DateTime.UtcNow);
            if (result.Removed > 0 || result.InUse > 0)
            {
                AppLog.Information(
                    "BundleExtraction.Sweep",
                    $"removed {result.Removed} unpacked copies ({result.BytesFreed / (1024 * 1024)} MB) from {layout.Root}; " +
                    $"{result.InUse} in use, kept");
            }
        }
        catch (Exception ex)
        {
            AppLog.Warning("BundleExtraction.Sweep", ex);
        }
    }

    /// <summary>
    /// Where the unpacked copies live, and which one this process runs from (null when it runs from a folder).
    /// Null when there is nothing to sweep, or when the layout is not one the .NET host would have made: a sweep
    /// deletes every other folder under the root, so it only runs on a root it can positively identify.
    /// </summary>
    internal static Layout? ResolveLayout(
        string baseDirectory, string exePath, string? extractBaseOverride, string tempPath, bool isWindows)
    {
        string? exeDirectory = Path.GetDirectoryName(exePath);
        string hostName = Path.GetFileNameWithoutExtension(exePath);
        if (string.IsNullOrEmpty(exeDirectory) || string.IsNullOrEmpty(hostName))
        {
            return null;
        }

        string current = TrimSeparators(baseDirectory);
        if (!SamePath(current, exeDirectory, isWindows))
        {
            // Single file: the app runs from <base>/<host name>/<bundle id>, unpacked by the host.
            string? root = Path.GetDirectoryName(current);
            string? extractBase = root == null ? null : Path.GetDirectoryName(root);
            bool hostMadeIt = root != null
                && extractBase != null
                && string.Equals(Path.GetFileName(root), hostName, isWindows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
                && (Path.GetFileName(extractBase) == ".net"
                    || (!string.IsNullOrEmpty(extractBaseOverride) && SamePath(extractBase, TrimSeparators(extractBaseOverride), isWindows)));
            return hostMadeIt ? new Layout(root!, current) : null;
        }

        // A folder build. Only Windows ever shipped single-file builds that could have left copies behind.
        if (!isWindows)
        {
            return null;
        }

        string baseDir = string.IsNullOrEmpty(extractBaseOverride)
            ? Path.Combine(TrimSeparators(tempPath), ".net")
            : TrimSeparators(extractBaseOverride);
        return new Layout(Path.Combine(baseDir, hostName), null);
    }

    internal static SweepResult Sweep(Layout layout, DateTime nowUtc)
    {
        int removed = 0;
        int inUse = 0;
        long bytes = 0;
        bool isWindows = OperatingSystem.IsWindows();

        foreach (string directory in Directory.EnumerateDirectories(layout.Root).ToList())
        {
            if (layout.Current != null && SamePath(directory, layout.Current, isWindows))
            {
                continue;
            }

            try
            {
                if (nowUtc - Directory.GetLastWriteTimeUtc(directory) < MinimumAge)
                {
                    continue;
                }

                string? doomed = MoveAsideIfUnused(directory, isWindows);
                if (doomed == null)
                {
                    inUse++;
                    continue;
                }

                long size = SizeOf(doomed);
                Directory.Delete(doomed, recursive: true);
                removed++;
                bytes += size;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Locked by a scanner, or gone already: the next launch tries again.
            }
        }

        if (layout.Current == null && !Directory.EnumerateFileSystemEntries(layout.Root).Any())
        {
            try
            {
                Directory.Delete(layout.Root);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        return new SweepResult(removed, bytes, inUse);
    }

    /// <summary>
    /// The path to delete, or null when another process still uses the copy. Windows refuses to rename a folder while a
    /// file in it is open or loaded, so a successful move is the check. Linux renames regardless, so it asks /proc
    /// whether any process has a file from that folder mapped.
    /// </summary>
    private static string? MoveAsideIfUnused(string directory, bool isWindows)
    {
        if (!isWindows)
        {
            return IsMappedByAnyProcess(directory) ? null : directory;
        }

        if (directory.EndsWith(StaleSuffix, StringComparison.OrdinalIgnoreCase))
        {
            return directory; // moved aside by an earlier sweep that could not finish deleting it
        }

        string target = directory + StaleSuffix;
        try
        {
            Directory.Move(directory, target);
            return target;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static bool IsMappedByAnyProcess(string directory)
    {
        string prefix = TrimSeparators(directory) + "/";
        foreach (string process in Directory.EnumerateDirectories("/proc"))
        {
            if (!int.TryParse(Path.GetFileName(process), out _))
            {
                continue;
            }

            string maps;
            try
            {
                maps = File.ReadAllText(Path.Combine(process, "maps"));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue; // exited, or another user's: their processes cannot use our 0700 copy
            }

            if (MapsReference(maps, prefix))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Whether a /proc/&lt;pid&gt;/maps listing maps any file under <paramref name="directoryPrefix"/>.</summary>
    internal static bool MapsReference(string maps, string directoryPrefix) =>
        maps.Contains(directoryPrefix, StringComparison.Ordinal);

    private static long SizeOf(string directory)
    {
        long total = 0;
        foreach (string file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            try
            {
                total += new FileInfo(file).Length;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        return total;
    }

    private static string TrimSeparators(string path) =>
        path.Length > 1 ? path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) : path;

    private static bool SamePath(string a, string b, bool isWindows) =>
        string.Equals(
            TrimSeparators(Path.GetFullPath(a)),
            TrimSeparators(Path.GetFullPath(b)),
            isWindows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
