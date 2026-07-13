using System;
using System.IO;
using System.Linq;

namespace GimmeCapture.Services.Core.Media;

// Batch output-path composition + sanitization for the Compress pipeline. Extracted verbatim from
// MainWindowViewModel so the path-safety rules (no drive escape, no "..") can be asserted in isolation.
internal static class CompressOutputPath
{
    /// <summary>
    /// Composes a batch output path: base = <paramref name="rootFolder"/> when set, else the source's own
    /// folder. The per-item <paramref name="outputName"/> may carry a relative subfolder path plus a file name
    /// (e.g. <c>\sub\clip</c>); it is sanitized so it can't escape the base (no drive, no <c>..</c>). The name
    /// falls back to the source name when blank, a <c>_yyyyMMdd_HHmmss</c> stamp is appended when
    /// <paramref name="appendDate"/> is set, and " (n)" is added until the name is free.
    /// </summary>
    internal static string BuildBatchOutputPath(
        string sourcePath, string? outputName, string? rootFolder, string ext, bool appendDate, DateTime timestamp)
    {
        string baseDir = !string.IsNullOrWhiteSpace(rootFolder)
            ? rootFolder!
            : (Path.GetDirectoryName(sourcePath) ?? Path.GetTempPath());

        string relDir = string.Empty;
        string namePart = string.Empty;
        string raw = (outputName ?? string.Empty).Trim();
        if (raw.Length > 0)
        {
            string normalized = raw.Replace('/', Path.DirectorySeparatorChar);
            relDir = SanitizeRelativeDir(Path.GetDirectoryName(normalized));
            namePart = SanitizeFileName(Path.GetFileNameWithoutExtension(normalized));
        }
        if (namePart.Length == 0)
        {
            namePart = SanitizeFileName(Path.GetFileNameWithoutExtension(sourcePath));
        }
        if (namePart.Length == 0)
        {
            namePart = "output";
        }

        string targetDir = relDir.Length > 0 ? Path.Combine(baseDir, relDir) : baseDir;
        string fileBase = appendDate ? $"{namePart}_{timestamp:yyyyMMdd_HHmmss}" : namePart;

        string candidate = Path.Combine(targetDir, fileBase + ext);
        for (int n = 1; File.Exists(candidate); n++)
        {
            candidate = Path.Combine(targetDir, $"{fileBase} ({n}){ext}");
        }

        return candidate;
    }

    // Strips path-invalid characters (and trailing dots) from a single file-name segment.
    internal static string SanitizeFileName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return string.Empty;
        }

        foreach (char c in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(c, '_');
        }

        return name.Trim().Trim('.');
    }

    // Turns a user-typed relative path into safe folder segments: drops ".."/"."/empties + any drive or
    // invalid characters, so the result can never escape the base directory.
    internal static string SanitizeRelativeDir(string? dir)
    {
        if (string.IsNullOrWhiteSpace(dir))
        {
            return string.Empty;
        }

        string[] safe = dir
            .Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Trim())
            .Where(p => p != "." && p != "..")
            .Select(SanitizeFileName)
            .Where(p => p.Length > 0)
            .ToArray();

        return safe.Length > 0 ? Path.Combine(safe) : string.Empty;
    }
}
