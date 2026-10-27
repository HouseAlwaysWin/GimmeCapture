using System;
using System.IO;
using System.Runtime.CompilerServices;
using GimmeCapture.Services.Core.Infrastructure;

namespace GimmeCapture.Tests;

/// <summary>
/// The app's data folder (%LOCALAPPDATA%\GimmeCapture) for this test run: a temporary folder, removed when the run ends.
/// Code under test that reaches for the real one, an UpdateService reading its update state or a service built with
/// its default storage, used to leave folders in the developer's own data. Each test run hashed to a new instance key
/// (verify.ps1 builds into a fresh folder every time), so the empty instances\&lt;key&gt; folders reached 203.
/// </summary>
internal static class TestStorageRoot
{
    private static readonly string Parent = Path.Combine(Path.GetTempPath(), "GimmeCapture.Tests");

    public static string RootDirectory { get; } = Path.Combine(Parent, "storage-" + Guid.NewGuid().ToString("N"));

    [ModuleInitializer]
    internal static void Initialize()
    {
        AppStoragePaths.UseRootDirectoryForTests(RootDirectory);
        AppDomain.CurrentDomain.ProcessExit += (_, _) => TryDelete(RootDirectory);
        DeleteRootsOfRunsThatDidNotFinish();
    }

    // A run that was killed never got to its own clean-up. One day is far longer than any run.
    private static void DeleteRootsOfRunsThatDidNotFinish()
    {
        if (!Directory.Exists(Parent))
        {
            return;
        }

        foreach (string directory in Directory.EnumerateDirectories(Parent, "storage-*"))
        {
            if (DateTime.UtcNow - Directory.GetLastWriteTimeUtc(directory) > TimeSpan.FromDays(1))
            {
                TryDelete(directory);
            }
        }
    }

    private static void TryDelete(string directory)
    {
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Gone already, or still in use by a file a test left open: %TEMP% clean-up gets it later.
        }
    }
}
