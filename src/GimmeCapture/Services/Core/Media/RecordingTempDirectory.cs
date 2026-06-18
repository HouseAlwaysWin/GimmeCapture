using System;
using System.IO;

namespace GimmeCapture.Services.Core.Media;

public static class RecordingTempDirectory
{
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
            || !name.StartsWith("Recordings_", StringComparison.Ordinal))
        {
            return false;
        }

        if (Directory.Exists(fullSession))
        {
            Directory.Delete(fullSession, recursive: true);
        }

        return true;
    }
}
