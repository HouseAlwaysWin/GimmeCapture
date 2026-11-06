using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace GimmeCapture.Launcher;

/// <summary>
/// What the sign-in launcher reads ahead and what it hands the app. Compiled into the launcher (.NET Framework 4.8) and
/// into the app, so both read the same names, the same start-up list and the same hand-off.
/// </summary>
internal static class ReadAhead
{
    /// <summary>The launcher, beside the app's exe in a published build.</summary>
    internal const string LauncherFileName = "GimmeCapture.Launcher.exe";

    internal const string AppFileName = "GimmeCapture.exe";

    /// <summary>The app's own assembly, whose product version names the app's version folder.</summary>
    internal const string AppAssemblyFileName = "GimmeCapture.dll";

    /// <summary>
    /// What the app writes down after each start, in its version's data folder: the files of its own folder that the
    /// start mapped, in the order it loaded them (<c>StartupPrefetch</c>).
    /// </summary>
    internal const string StartupListFileName = "startup-files.txt";

    /// <summary>Set for the app by the launcher: when the launcher started and what it read.</summary>
    internal const string HandOffVariable = "GIMMECAPTURE_LAUNCHER";

    /// <summary>
    /// How many files <see cref="ReadAll"/> reads at once. At sign-in other programs read the same hard disk, and each
    /// read of the launcher's waited behind theirs: one file after another, its 141 MB took 22.6 s on 2026-11-05. With
    /// four of its files being read, the disk always has reads of the launcher's to choose from. On cold copies of the
    /// installed app's files, with eight threads of <c>LauncherReadProbeTests</c> reading elsewhere on that disk, 133.5 MB
    /// took 4.1-4.8 s instead of 10.2-12.0, and 2.0-2.2 s instead of 3.5-4.0 with the disk to itself. Two threads took
    /// 5.4-6.3 s, eight 2.9-4.6 s, no faster with the disk to itself (2.0-2.4 s) and taking more of it from the others.
    /// </summary>
    internal const int ReadThreads = 4;

    /// <summary>
    /// What v0.74.0 wrote after each file of the list, before the stretches of it its start used, which it then read
    /// instead of the whole file: on a busy hard disk the many small reads took 2-3 times as long as the whole files
    /// (87 MB in 42.6 s at a sign-in, against 141 MB in 22.6 s the day before). No file name has one.
    /// </summary>
    private const char StretchesSeparator = '|';

    /// <summary>
    /// The files .NET's host and runtime read before the app's Main, in the order they are needed. At a sign-in from a
    /// hard disk they took 18-37 s to fault in, a few kilobytes at a time; read through once each, a few seconds.
    /// </summary>
    internal static readonly string[] BeforeMain =
    {
        AppFileName,
        "hostfxr.dll",
        "GimmeCapture.runtimeconfig.json",
        "GimmeCapture.deps.json",
        "hostpolicy.dll",
        "coreclr.dll",
        "System.Private.CoreLib.dll",
        "clrjit.dll",
        AppAssemblyFileName,
        "System.Runtime.dll",
    };

    /// <summary>
    /// What the launcher reads after <see cref="BeforeMain"/>: the start-up list's files, in the list's order, each once
    /// and none of BeforeMain's again. At sign-in the app still waited 10.7 s on the disk after Main, at 1.2 s of CPU,
    /// with its own read of this list not done by then: it read while the start faulted in pages elsewhere on the disk.
    /// </summary>
    internal static List<string> FromList(IEnumerable<string> lines, string appDirectory)
    {
        var read = new HashSet<string>(Resolve(BeforeMain, appDirectory), StringComparer.OrdinalIgnoreCase);
        var files = new List<string>();
        foreach (string file in Resolve(lines, appDirectory))
        {
            if (read.Add(file))
            {
                files.Add(file);
            }
        }

        return files;
    }

    /// <summary>What the launcher reads: <see cref="BeforeMain"/>, then the start-up list's other files in its order.</summary>
    internal static List<string> Plan(IEnumerable<string> lines, string appDirectory)
    {
        var files = Resolve(BeforeMain, appDirectory);
        files.AddRange(FromList(lines, appDirectory));
        return files;
    }

    /// <summary>
    /// The list's entries as full paths inside <paramref name="appDirectory"/>; a blank line, a rooted path, one that
    /// leaves the folder or one that is no path is skipped, and the stretches v0.74.0 wrote after a file are left out.
    /// </summary>
    internal static List<string> Resolve(IEnumerable<string> lines, string appDirectory)
    {
        var files = new List<string>();
        foreach (string raw in lines)
        {
            int stretches = raw.IndexOf(StretchesSeparator);
            string line = (stretches < 0 ? raw : raw.Substring(0, stretches)).Trim();
            if (line.Length == 0 || Path.IsPathRooted(line))
            {
                continue;
            }

            try
            {
                string path = Path.GetFullPath(Path.Combine(appDirectory, line));
                if (RelativeToApp(appDirectory, path) != null)
                {
                    files.Add(path);
                }
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
            }
        }

        return files;
    }

    /// <summary><paramref name="path"/> relative to <paramref name="appDirectory"/>, or null when it is not inside it.</summary>
    internal static string? RelativeToApp(string appDirectory, string? path)
    {
        if (path == null || path.Length == 0)
        {
            return null;
        }

        string root = Path.GetFullPath(appDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        string full = Path.GetFullPath(path);
        return full.Length > root.Length && full.StartsWith(root, StringComparison.OrdinalIgnoreCase)
            ? full.Substring(root.Length)
            : null;
    }

    /// <summary>
    /// Where the app in <paramref name="appDirectory"/> keeps its start-up list at <paramref name="appVersion"/>, under its
    /// data folder <paramref name="dataRoot"/>: the version's folder of <c>AppStoragePaths</c>, worked out again here for
    /// the launcher, which runs without the app's code.
    /// </summary>
    internal static string StartupListPath(string dataRoot, string appDirectory, string appVersion) =>
        Path.Combine(
            dataRoot, "instances", InstallInstanceKey(appDirectory), "versions", VersionFolderName(appVersion), StartupListFileName);

    /// <summary>
    /// <c>AppStoragePaths.GetInstallInstanceKey</c> in what .NET Framework 4.8 has: the first 8 bytes of the SHA-256 of the
    /// folder's full path, upper case, as hex.
    /// </summary>
    internal static string InstallInstanceKey(string appDirectory)
    {
        string normalized = Path.GetFullPath(appDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .ToUpperInvariant();
        byte[] hash;
        using (var sha256 = SHA256.Create())
        {
            hash = sha256.ComputeHash(Encoding.UTF8.GetBytes(normalized));
        }

        var key = new StringBuilder(16);
        for (int i = 0; i < 8; i++)
        {
            key.Append(hash[i].ToString("x2", CultureInfo.InvariantCulture));
        }

        return key.ToString();
    }

    /// <summary><c>AppStoragePaths</c>' name for a version's folder.</summary>
    internal static string VersionFolderName(string appVersion)
    {
        string normalized = string.IsNullOrWhiteSpace(appVersion) ? "unknown" : appVersion.Trim().TrimStart('v');
        foreach (char invalid in Path.GetInvalidFileNameChars())
        {
            normalized = normalized.Replace(invalid, '_');
        }

        return normalized.Replace(' ', '_');
    }

    /// <summary>
    /// The app's version from its assembly's product version, <c>0.71.0+bed41298...</c>: what comes before the commit, as
    /// <c>AppVersionInfo.CurrentVersion</c> reads it. Null when there is none.
    /// </summary>
    internal static string? AppVersion(string? productVersion)
    {
        string? version = productVersion?.Split('+')[0].Trim();
        return version == null || version.Length == 0 ? null : version;
    }

    /// <summary>Reads a file through once, so its pages are in memory when they are mapped; 0 when it cannot be read.</summary>
    internal static long ReadThrough(string path, byte[] buffer)
    {
        try
        {
            using (var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.SequentialScan))
            {
                long total = 0;
                int read;
                while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                {
                    total += read;
                }

                return total;
            }
        }
        catch (IOException)
        {
            return 0;
        }
        catch (UnauthorizedAccessException)
        {
            return 0;
        }
    }

    /// <summary>
    /// Reads each file through once, <paramref name="threads"/> at a time, each thread taking the next file in the list's
    /// order; a file that cannot be read counts nothing. The bytes read.
    /// </summary>
    internal static long ReadAll(IReadOnlyList<string> files, int threads)
    {
        long total = 0;
        int next = -1;
        ThreadStart readNext = () =>
        {
            try
            {
                // A megabyte at a time into this one buffer: the streams keep none of their own.
                var buffer = new byte[1 << 20];
                int i;
                while ((i = Interlocked.Increment(ref next)) < files.Count)
                {
                    Interlocked.Add(ref total, ReadThrough(files[i], buffer));
                }
            }
            catch (Exception)
            {
                // Left on a thread of its own, it would end the launcher before it started the app.
            }
        };

        var helpers = new List<Thread>();
        for (int i = 1; i < Math.Min(threads, files.Count); i++)
        {
            var helper = new Thread(readNext) { IsBackground = true, Name = "Read-ahead" };
            helper.Start();
            helpers.Add(helper);
        }

        readNext();
        foreach (var helper in helpers)
        {
            helper.Join();
        }

        return Interlocked.Read(ref total);
    }

    /// <summary>
    /// A command line without its program: what the launcher passes on to the app. The program ends at its closing
    /// quote, or at the first space when it is not quoted.
    /// </summary>
    internal static string ArgumentsAfterProgram(string commandLine)
    {
        string line = commandLine.TrimStart();
        int end;
        if (line.StartsWith("\"", StringComparison.Ordinal))
        {
            int closing = line.IndexOf('"', 1);
            end = closing < 0 ? line.Length : closing + 1;
        }
        else
        {
            end = line.IndexOfAny(new[] { ' ', '\t' });
            if (end < 0)
            {
                end = line.Length;
            }
        }

        return line.Substring(end).TrimStart();
    }

    internal static string FormatHandOff(DateTime launcherStartedUtc, long bytesRead) =>
        launcherStartedUtc.Ticks.ToString(CultureInfo.InvariantCulture) + ";" + bytesRead.ToString(CultureInfo.InvariantCulture);

    internal static bool TryParseHandOff(string? value, out DateTime launcherStartedUtc, out long bytesRead)
    {
        launcherStartedUtc = default;
        bytesRead = 0;
        if (value == null)
        {
            return false;
        }

        string[] parts = value.Split(';');
        if (parts.Length != 2
            || !long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out long ticks)
            || !long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out long bytes)
            || ticks > DateTime.MaxValue.Ticks)
        {
            return false;
        }

        launcherStartedUtc = new DateTime(ticks, DateTimeKind.Utc);
        bytesRead = bytes;
        return true;
    }
}
