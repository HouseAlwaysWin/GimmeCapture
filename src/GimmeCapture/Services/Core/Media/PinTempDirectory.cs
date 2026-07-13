using System;
using System.IO;
using GimmeCapture.Services.Core.Infrastructure;

namespace GimmeCapture.Services.Core.Media;

/// <summary>
/// The persistent per-pin temp folder (<c>%TEMP%/GimmeCapture_Pins</c>) holding files a floating pin owns for its
/// whole lifetime — cropped-clip copies (<c>crop_*</c>) and GIF-recording audio sidecars (<c>gifrec_*</c>). Unlike
/// the per-operation export/compress dirs (deleted in a <c>finally</c>), these outlive the operation, so each owning
/// pin deletes its file on dispose via <see cref="TryDeleteOwnedFile"/>, and a startup sweep
/// (<see cref="SweepFilesOlderThan"/>) mops up anything a crash left behind.
/// </summary>
public static class PinTempDirectory
{
    public const string FolderName = "GimmeCapture_Pins";

    public static string RootDirectory => Path.Combine(Path.GetTempPath(), FolderName);

    /// <summary>Create the pin temp folder if needed and return its path.</summary>
    public static string EnsureDirectory()
    {
        string dir = RootDirectory;
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>True if <paramref name="path"/> is a file directly inside the pin temp folder (i.e. one we own).</summary>
    public static bool IsOwnedTempFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        string? parent = Path.GetDirectoryName(Path.GetFullPath(path))
            ?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string root = Path.GetFullPath(RootDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return string.Equals(parent, root, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Best-effort delete of a file this app placed in the pin temp folder. No-op for anything else.</summary>
    public static void TryDeleteOwnedFile(string? path)
    {
        if (!IsOwnedTempFile(path))
        {
            return;
        }

        try
        {
            if (File.Exists(path))
            {
                File.Delete(path!);
            }
        }
        catch (Exception ex)
        {
            AppLog.Warning("PinTempDirectory.DeleteOwnedFile", ex);
        }
    }

    /// <summary>Best-effort sweep of pin temp files older than <paramref name="maxAge"/> (crash orphans). Never throws.</summary>
    public static int SweepFilesOlderThan(TimeSpan maxAge)
    {
        string dir = RootDirectory;
        if (!Directory.Exists(dir))
        {
            return 0;
        }

        int deleted = 0;
        DateTime cutoffUtc = DateTime.UtcNow - maxAge;
        foreach (string file in Directory.EnumerateFiles(dir))
        {
            try
            {
                if (File.GetLastWriteTimeUtc(file) < cutoffUtc)
                {
                    File.Delete(file);
                    deleted++;
                }
            }
            catch
            {
                // In use by a running instance / locked — skip.
            }
        }

        return deleted;
    }
}
