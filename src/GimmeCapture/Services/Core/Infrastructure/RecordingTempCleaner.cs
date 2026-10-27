using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace GimmeCapture.Services.Core.Infrastructure;

/// <summary>
/// Removes the recordings that earlier runs left in the app's temp folders. A recording the user has not saved waits
/// there while it is pinned, and <see cref="UnsavedRecording"/> deletes it when the pin closes. Nothing removed the
/// rest: a recording copied to the clipboard, one a crash or a kill left, and the raw segments a failed save keeps for
/// recovery. Each update also moved the folder (versions\&lt;version&gt;\Temp) and left the previous one behind whole:
/// about 100 MB of recordings on one machine, the oldest from June.
/// <para>Only what is named the way the app names it (<see cref="RecordingTempDirectory"/>) is touched, so a temp folder
/// the user picked keeps everything else. A recording goes once it is a day old, raw segments once they are a week
/// old.</para>
/// </summary>
public static class RecordingTempCleaner
{
    // A recording on the clipboard can still be pasted from there. One untouched for a day has done its job.
    private static readonly TimeSpan RecordingMinimumAge = TimeSpan.FromDays(1);

    // The raw segments of a recording that failed to save, which the error message sends the user to for recovery.
    private static readonly TimeSpan SessionMinimumAge = TimeSpan.FromDays(7);

    internal sealed record Layout(
        // instances\<key>\versions: each version of this install has its Temp folder in there.
        string VersionsDirectory,
        // Where this version records. Kept when the sweep empties it; an older version's Temp is not.
        string CurrentTempDirectory,
        // The Temp folder beside the executable, where the first versions recorded.
        string AppTempDirectory,
        // The TempDirectory setting, if set: a folder the user picked, so recordings only, never a session folder.
        string? UserTempDirectory);

    internal sealed record SweepResult(IReadOnlyList<string> RecordingsRemoved, int SessionsRemoved, long BytesFreed);

    /// <summary>Run the sweep. Never throws. Call off the UI thread, once the settings have loaded: they name the data
    /// folder and the temp folder the user picked. Returns the recordings it deleted, for the history to forget.</summary>
    public static IReadOnlyList<string> Sweep(string dataDirectory, string? userTempDirectory)
    {
        try
        {
            string appDirectory = RuntimePathProvider.GetExecutableDirectory();
            var result = Sweep(
                new Layout(
                    Path.Combine(AppStoragePaths.GetInstallInstanceDirectory(appDirectory), "versions"),
                    RecordingTempDirectory.For(dataDirectory),
                    RecordingTempDirectory.For(appDirectory),
                    string.IsNullOrWhiteSpace(userTempDirectory) ? null : userTempDirectory),
                DateTime.UtcNow);
            if (result.RecordingsRemoved.Count > 0 || result.SessionsRemoved > 0)
            {
                AppLog.Information(
                    "RecordingTemp.Sweep",
                    $"removed {result.RecordingsRemoved.Count} unsaved recordings and {result.SessionsRemoved} recording " +
                    $"session folders ({result.BytesFreed / (1024 * 1024)} MB)");
            }

            return result.RecordingsRemoved;
        }
        catch (Exception ex)
        {
            AppLog.Warning("RecordingTemp.Sweep", ex);
            return Array.Empty<string>();
        }
    }

    internal static SweepResult Sweep(Layout layout, DateTime nowUtc)
    {
        var removed = new Tally();
        var swept = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string temp in VersionTempDirectories(layout.VersionsDirectory))
        {
            swept.Add(Normalize(temp));
            RemoveRecordingsAndSessions(temp, nowUtc, removed);
            if (!SamePath(temp, layout.CurrentTempDirectory))
            {
                RemoveIfEmpty(temp); // an older version's: nothing records there any more
            }
        }

        foreach (string temp in new[] { layout.CurrentTempDirectory, layout.AppTempDirectory })
        {
            if (swept.Add(Normalize(temp)))
            {
                RemoveRecordingsAndSessions(temp, nowUtc, removed);
            }
        }

        if (layout.UserTempDirectory != null && swept.Add(Normalize(layout.UserTempDirectory)))
        {
            RemoveRecordings(layout.UserTempDirectory, nowUtc, removed);
        }

        return new SweepResult(removed.Recordings, removed.Sessions, removed.Bytes);
    }

    private static IEnumerable<string> VersionTempDirectories(string versionsDirectory) =>
        Directory.Exists(versionsDirectory)
            ? Directory.EnumerateDirectories(versionsDirectory)
                .Select(RecordingTempDirectory.For)
                .Where(Directory.Exists)
                .ToList()
            : Enumerable.Empty<string>();

    // A folder the app owns: its recordings and the session folders of recordings in progress, nothing else.
    private static void RemoveRecordingsAndSessions(string directory, DateTime nowUtc, Tally removed)
    {
        RemoveRecordings(directory, nowUtc, removed);
        if (!Directory.Exists(directory))
        {
            return;
        }

        foreach (DirectoryInfo session in new DirectoryInfo(directory).EnumerateDirectories().ToList())
        {
            if (!RecordingTempDirectory.IsSessionDirectoryName(session.Name))
            {
                continue;
            }

            try
            {
                // A recording in progress keeps writing its segments, which leaves the folder itself untouched.
                if (nowUtc - NewestWrite(session) < SessionMinimumAge)
                {
                    continue;
                }

                long bytes = session.EnumerateFiles("*", SearchOption.AllDirectories).Sum(file => file.Length);
                session.Delete(recursive: true);
                removed.AddSession(bytes);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A segment still open, or a scanner holding one: the next launch looks again.
            }
        }
    }

    private static void RemoveRecordings(string directory, DateTime nowUtc, Tally removed)
    {
        if (!Directory.Exists(directory))
        {
            return;
        }

        foreach (FileInfo file in new DirectoryInfo(directory).EnumerateFiles().ToList())
        {
            if (!RecordingTempDirectory.IsRecordingFileName(file.Name) || nowUtc - file.LastWriteTimeUtc < RecordingMinimumAge)
            {
                continue;
            }

            try
            {
                long length = file.Length;
                file.Delete();
                removed.AddRecording(file.FullName, length);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Open in a player, say: the next launch tries again.
            }
        }
    }

    private static DateTime NewestWrite(DirectoryInfo directory) =>
        directory.EnumerateFileSystemInfos("*", SearchOption.AllDirectories)
            .Select(entry => entry.LastWriteTimeUtc)
            .Append(directory.LastWriteTimeUtc)
            .Max();

    private static void RemoveIfEmpty(string directory)
    {
        try
        {
            if (!Directory.EnumerateFileSystemEntries(directory).Any())
            {
                Directory.Delete(directory, recursive: false);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static bool SamePath(string a, string b) =>
        Normalize(a).Equals(Normalize(b), StringComparison.OrdinalIgnoreCase);

    private static string Normalize(string path) =>
        Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private sealed class Tally
    {
        private readonly List<string> _recordings = new();

        public IReadOnlyList<string> Recordings => _recordings;

        public int Sessions { get; private set; }

        public long Bytes { get; private set; }

        public void AddRecording(string path, long bytes)
        {
            _recordings.Add(path);
            Bytes += bytes;
        }

        public void AddSession(long bytes)
        {
            Sessions++;
            Bytes += bytes;
        }
    }
}
