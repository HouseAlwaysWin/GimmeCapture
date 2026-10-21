using System;
using System.IO;
using System.Linq;
using GimmeCapture.Services.Core.Infrastructure;
using Xunit;

namespace GimmeCapture.Tests;

// Start-up used to walk the whole data folder (every version's config, the history, AI models) for pending-update
// state, before any window showed. The state is only ever written at the root (older builds) and in an install's
// instance folder, so that is all start-up reads now.
public sealed class PendingUpdateLocatorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "GimmeCapture.Tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    private void Write(string relativePath)
    {
        string path = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{}");
    }

    [Fact]
    public void FindsTheRootStateAndEachInstanceState_AndNothingElse()
    {
        Write("pending-update.json");
        Write("instances/aaaa/pending-update.json");
        Write("instances/bbbb/pending-update.json");
        Write("instances/aaaa/versions/0.68.1/pending-update.json");
        Write("history/pending-update.json");

        string[] found = UpdateService.FindPendingUpdateStatePaths(_root)
            .Select(path => Path.GetRelativePath(_root, path).Replace('\\', '/'))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            new[] { "instances/aaaa/pending-update.json", "instances/bbbb/pending-update.json", "pending-update.json" },
            found);
    }

    [Fact]
    public void AMissingDataFolder_HoldsNothing()
    {
        Assert.Empty(UpdateService.FindPendingUpdateStatePaths(Path.Combine(_root, "missing")));
    }
}
