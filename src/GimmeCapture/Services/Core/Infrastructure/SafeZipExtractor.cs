using System;
using System.IO;
using System.IO.Compression;

namespace GimmeCapture.Services.Core.Infrastructure;

public static class SafeZipExtractor
{
    public static void ExtractToDirectory(string zipPath, string destinationDirectory, bool overwriteFiles)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(zipPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDirectory);

        var destinationRoot = Path.GetFullPath(destinationDirectory);
        Directory.CreateDirectory(destinationRoot);
        var rootPrefix = destinationRoot.EndsWith(Path.DirectorySeparatorChar)
            ? destinationRoot
            : destinationRoot + Path.DirectorySeparatorChar;

        using var archive = ZipFile.OpenRead(zipPath);
        foreach (var entry in archive.Entries)
        {
            var destinationPath = Path.GetFullPath(Path.Combine(destinationRoot, entry.FullName));
            if (!destinationPath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(destinationPath, destinationRoot, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"Zip entry escapes the destination directory: {entry.FullName}");
            }

            if (string.IsNullOrEmpty(entry.Name))
            {
                Directory.CreateDirectory(destinationPath);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            entry.ExtractToFile(destinationPath, overwriteFiles);
        }
    }
}
