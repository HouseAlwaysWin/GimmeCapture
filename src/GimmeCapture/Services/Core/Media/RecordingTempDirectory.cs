using System;
using System.IO;

namespace GimmeCapture.Services.Core.Media;

/// <summary>
/// The names the app gives what it records into its temp folder (<c>&lt;data folder&gt;\Temp</c>, or the TempDirectory
/// setting): a finished recording the user has not saved yet, <c>GimmeCapture_&lt;guid&gt;.&lt;ext&gt;</c>, and the raw
/// segments of one being recorded, <c>Recordings_&lt;guid&gt;</c>. Whatever deletes them checks the name first, so
/// nothing else in those folders is ever touched.
/// </summary>
public static class RecordingTempDirectory
{
    public const string FolderName = "Temp";

    private const string RecordingPrefix = "GimmeCapture_";
    private const string SessionPrefix = "Recordings_";

    // What a recording finalizes to, and what a finalize that changed container falls back to.
    private static readonly string[] RecordingExtensions = { ".mp4", ".mkv", ".mov", ".webm", ".gif", ".avi" };

    /// <summary>The temp folder of a data folder (<see cref="IAppSettingsService.BaseDataDirectory"/>).</summary>
    public static string For(string dataDirectory) => Path.Combine(dataDirectory, FolderName);

    public static string NewRecordingFileName(string format) => $"{RecordingPrefix}{Guid.NewGuid()}.{format}";

    public static string NewSessionDirectoryName() => $"{SessionPrefix}{Guid.NewGuid()}";

    /// <summary>Whether <paramref name="fileName"/> is named like <see cref="NewRecordingFileName"/>'s names.</summary>
    public static bool IsRecordingFileName(string? fileName)
    {
        if (string.IsNullOrEmpty(fileName)
            || Array.IndexOf(RecordingExtensions, Path.GetExtension(fileName).ToLowerInvariant()) < 0)
        {
            return false;
        }

        return IsPrefixAndGuid(Path.GetFileNameWithoutExtension(fileName), RecordingPrefix);
    }

    /// <summary>Whether <paramref name="directoryName"/> is named like <see cref="NewSessionDirectoryName"/>'s names.</summary>
    public static bool IsSessionDirectoryName(string? directoryName) => IsPrefixAndGuid(directoryName, SessionPrefix);

    public static bool TryDeleteSessionDirectory(string sessionDirectory, string expectedParentDirectory)
    {
        if (string.IsNullOrWhiteSpace(sessionDirectory)
            || string.IsNullOrWhiteSpace(expectedParentDirectory))
        {
            return false;
        }

        string fullSession = Path.GetFullPath(sessionDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string fullParent = Path.GetFullPath(expectedParentDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string? actualParent = Path.GetDirectoryName(fullSession)?.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
        string name = Path.GetFileName(fullSession);

        if (!string.Equals(actualParent, fullParent, StringComparison.OrdinalIgnoreCase)
            || !IsSessionDirectoryName(name))
        {
            return false;
        }

        if (Directory.Exists(fullSession))
        {
            Directory.Delete(fullSession, recursive: true);
        }

        return true;
    }

    private static bool IsPrefixAndGuid(string? name, string prefix) =>
        name != null
        && name.StartsWith(prefix, StringComparison.Ordinal)
        && Guid.TryParseExact(name.AsSpan(prefix.Length), "D", out _);
}
