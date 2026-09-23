using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace GimmeCapture.Tests;

/// <summary>Locates the repository's source files, for tests that check the sources themselves.</summary>
internal static class RepositoryFiles
{
    private static readonly Lazy<string> RootDirectory = new(FindRoot);

    internal static string Root => RootDirectory.Value;

    internal static string AppSource => Path.Combine(Root, "src", "GimmeCapture");

    /// <summary>Files under src/GimmeCapture matching <paramref name="searchPattern"/>, excluding build output.</summary>
    internal static IEnumerable<string> AppFiles(string searchPattern) =>
        Directory.EnumerateFiles(AppSource, searchPattern, SearchOption.AllDirectories)
            .Where(path => !IsBuildOutput(path));

    /// <summary>Path relative to src/GimmeCapture with forward slashes, for stable messages and allow-lists.</summary>
    internal static string Relative(string path) =>
        Path.GetRelativePath(AppSource, path).Replace('\\', '/');

    private static bool IsBuildOutput(string path)
    {
        var segments = Relative(path).Split('/');
        return segments.Contains("obj") || segments.Contains("bin");
    }

    private static string FindRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null)
        {
            if (File.Exists(Path.Combine(current.FullName, "GimmeCapture.slnx")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Repository root not found.");
    }
}
