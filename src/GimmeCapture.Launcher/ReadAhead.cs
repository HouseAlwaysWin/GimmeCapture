using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;

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
    /// start mapped, in the order it loaded them, each with the stretches of it the start used (<c>StartupPrefetch</c>).
    /// </summary>
    internal const string StartupListFileName = "startup-files.txt";

    /// <summary>Between a file and its stretches in a line of the start-up list; no file name has one.</summary>
    internal const char StretchesSeparator = '|';

    /// <summary>Set for the app by the launcher: when the launcher started and what it read.</summary>
    internal const string HandOffVariable = "GIMMECAPTURE_LAUNCHER";

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
    /// What the launcher reads: <see cref="BeforeMain"/>, then the start-up list's other files in the list's order, each
    /// once, of each the stretches the list gives for it, or all of it. At sign-in the app still waited 10.7 s on the
    /// disk after Main, at 1.2 s of CPU, its own read of the list running alongside the start's faults; read here
    /// first, whole, the 141 MB took 22.6 s, and the start then used less than half of them.
    /// </summary>
    internal static List<StartupFile> Plan(IEnumerable<string> lines, string appDirectory)
    {
        var listed = Listed(lines, appDirectory);
        var stretches = new Dictionary<string, IReadOnlyList<FileRange>?>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in listed)
        {
            stretches[file.Path] = file.Stretches;
        }

        var plan = new List<StartupFile>();
        var read = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string path in Resolve(BeforeMain, appDirectory))
        {
            if (read.Add(path))
            {
                plan.Add(new StartupFile(path, stretches.TryGetValue(path, out var known) ? known : null));
            }
        }

        foreach (var file in listed)
        {
            if (read.Add(file.Path))
            {
                plan.Add(file);
            }
        }

        return plan;
    }

    /// <summary>The start-up list's files inside <paramref name="appDirectory"/>, in its order, each once.</summary>
    internal static List<StartupFile> Listed(IEnumerable<string> lines, string appDirectory)
    {
        var files = new List<StartupFile>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string line in lines)
        {
            string name = ParseLine(line, out var stretches);
            if (ResolveOne(name, appDirectory) is { } path && seen.Add(path))
            {
                files.Add(new StartupFile(path, stretches));
            }
        }

        return files;
    }

    /// <summary>
    /// The list's entries as full paths inside <paramref name="appDirectory"/>; a blank line, a rooted path, one that
    /// leaves the folder or one that is no path is skipped.
    /// </summary>
    internal static List<string> Resolve(IEnumerable<string> lines, string appDirectory)
    {
        var files = new List<string>();
        foreach (string line in lines)
        {
            if (ResolveOne(line, appDirectory) is { } path)
            {
                files.Add(path);
            }
        }

        return files;
    }

    private static string? ResolveOne(string raw, string appDirectory)
    {
        string line = raw.Trim();
        if (line.Length == 0 || Path.IsPathRooted(line))
        {
            return null;
        }

        try
        {
            string path = Path.GetFullPath(Path.Combine(appDirectory, line));
            return RelativeToApp(appDirectory, path) != null ? path : null;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    /// <summary>
    /// A line of the start-up list: the file relative to the app's folder, then the stretches of it the start used,
    /// <c>offset+length</c> each, comma-separated. A file without them is read whole, as every file was in the lists
    /// written before 2026-11-05.
    /// </summary>
    internal static string FormatLine(string relativePath, IReadOnlyList<FileRange>? stretches)
    {
        if (stretches == null || stretches.Count == 0)
        {
            return relativePath;
        }

        var line = new StringBuilder(relativePath).Append(StretchesSeparator);
        for (int i = 0; i < stretches.Count; i++)
        {
            if (i > 0)
            {
                line.Append(',');
            }

            line.Append(stretches[i].Offset.ToString(CultureInfo.InvariantCulture))
                .Append('+')
                .Append(stretches[i].Length.ToString(CultureInfo.InvariantCulture));
        }

        return line.ToString();
    }

    /// <summary>
    /// The file a line names, and in <paramref name="stretches"/> what of it to read: null for all of it, as for a line
    /// without stretches or with any it cannot read.
    /// </summary>
    internal static string ParseLine(string line, out IReadOnlyList<FileRange>? stretches)
    {
        stretches = null;
        int separator = line.IndexOf(StretchesSeparator);
        if (separator < 0)
        {
            return line;
        }

        var parsed = new List<FileRange>();
        foreach (string part in line.Substring(separator + 1).Split(','))
        {
            string[] numbers = part.Split('+');
            if (numbers.Length != 2
                || !long.TryParse(numbers[0].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out long offset)
                || !long.TryParse(numbers[1].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out long length)
                || length <= 0)
            {
                return line.Substring(0, separator);
            }

            parsed.Add(new FileRange(offset, length));
        }

        stretches = parsed;
        return line.Substring(0, separator);
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

    /// <summary>Reads what <paramref name="file"/> gives of its file: its stretches, or all of it. The bytes read.</summary>
    internal static long Read(StartupFile file, byte[] buffer) =>
        file.Stretches == null ? ReadThrough(file.Path, buffer) : ReadStretches(file.Path, file.Stretches, buffer);

    /// <summary>Reads each stretch of a file once, in order; what was read before a failure counts.</summary>
    internal static long ReadStretches(string path, IReadOnlyList<FileRange> stretches, byte[] buffer)
    {
        long total = 0;
        try
        {
            // No read-ahead past what was asked: the stretches leave out what the start did not use.
            using (var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.RandomAccess))
            {
                foreach (var stretch in stretches)
                {
                    stream.Position = stretch.Offset;
                    long left = stretch.Length;
                    while (left > 0)
                    {
                        int read = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, left));
                        if (read <= 0)
                        {
                            break;
                        }

                        total += read;
                        left -= read;
                    }
                }
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        return total;
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

/// <summary>A stretch of a file: where it starts and how many bytes it runs.</summary>
internal readonly struct FileRange : IEquatable<FileRange>
{
    internal FileRange(long offset, long length)
    {
        Offset = offset;
        Length = length;
    }

    internal long Offset { get; }

    internal long Length { get; }

    internal long End => Offset + Length;

    public bool Equals(FileRange other) => Offset == other.Offset && Length == other.Length;

    public override bool Equals(object? obj) => obj is FileRange other && Equals(other);

    public override int GetHashCode() => (Offset.GetHashCode() * 397) ^ Length.GetHashCode();

    public override string ToString() => Offset.ToString(CultureInfo.InvariantCulture) + "+" + Length.ToString(CultureInfo.InvariantCulture);
}

/// <summary>A file to read ahead, and the stretches of it to read: all of it when <see cref="Stretches"/> is null.</summary>
internal sealed class StartupFile
{
    internal StartupFile(string path, IReadOnlyList<FileRange>? stretches)
    {
        Path = path;
        Stretches = stretches;
    }

    internal string Path { get; }

    internal IReadOnlyList<FileRange>? Stretches { get; }
}
