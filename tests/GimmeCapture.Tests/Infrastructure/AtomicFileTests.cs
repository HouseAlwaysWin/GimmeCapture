using System;
using System.IO;
using System.Threading.Tasks;
using GimmeCapture.Services.Core.Infrastructure;

namespace GimmeCapture.Tests;

public class AtomicFileTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "GimmeCapture.Tests", "AtomicFile", Guid.NewGuid().ToString("N"));

    public AtomicFileTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void WriteAllText_CreatesFileWithContent()
    {
        var path = Path.Combine(_dir, "create.json");
        AtomicFile.WriteAllText(path, "hello");
        Assert.Equal("hello", File.ReadAllText(path));
    }

    [Fact]
    public void WriteAllText_OverwritesExisting()
    {
        var path = Path.Combine(_dir, "overwrite.json");
        File.WriteAllText(path, "old-and-longer");
        AtomicFile.WriteAllText(path, "new");
        Assert.Equal("new", File.ReadAllText(path));
    }

    [Fact]
    public void WriteAllText_LeavesNoTempArtifacts()
    {
        var path = Path.Combine(_dir, "clean.json");
        AtomicFile.WriteAllText(path, "x");
        AtomicFile.WriteAllText(path, "y"); // replace path
        Assert.Equal(new[] { path }, Directory.GetFiles(_dir));
    }

    [Fact]
    public async Task WriteAllTextAsync_CreatesFileWithContent()
    {
        var path = Path.Combine(_dir, "async.json");
        await AtomicFile.WriteAllTextAsync(path, "async-body");
        Assert.Equal("async-body", File.ReadAllText(path));
    }
}
