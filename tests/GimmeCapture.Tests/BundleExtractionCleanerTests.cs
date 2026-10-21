using System;
using System.IO;
using System.IO.MemoryMappedFiles;
using GimmeCapture.Services.Core.Infrastructure;
using Xunit;

namespace GimmeCapture.Tests;

// Single-file builds unpack 478 MB per version under .net/GimmeCapture and never remove it. The sweep deletes every
// other folder under that root, so these pin down that it only runs on a root the .NET host made, and that it keeps
// its own copy, one being unpacked and one another process has open.
public class BundleExtractionCleanerTests : IDisposable
{
    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "GimmeCapture.Tests", nameof(BundleExtractionCleanerTests), Guid.NewGuid().ToString("N"));
    private static readonly DateTime Now = DateTime.UtcNow;
    private static readonly string ExeName = OperatingSystem.IsWindows() ? "GimmeCapture.exe" : "GimmeCapture";

    public BundleExtractionCleanerTests() => Directory.CreateDirectory(_scratch);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_scratch, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private string At(params string[] parts) => Path.Combine([_scratch, .. parts]);

    [Fact]
    public void SingleFile_SweepsTheSiblingsOfItsOwnCopy()
    {
        string current = At(".net", "GimmeCapture", "Abc123");

        var layout = BundleExtractionCleaner.ResolveLayout(
            current + Path.DirectorySeparatorChar, At("install", ExeName), null, At("temp"), OperatingSystem.IsWindows());

        Assert.NotNull(layout);
        Assert.Equal(At(".net", "GimmeCapture"), layout.Root);
        Assert.Equal(current, layout.Current);
    }

    [Fact]
    public void SingleFile_HonorsTheExtractionBaseOverride()
    {
        string current = At("custom", "GimmeCapture", "Abc123");

        var layout = BundleExtractionCleaner.ResolveLayout(
            current, At("install", ExeName), At("custom"), At("temp"), OperatingSystem.IsWindows());

        Assert.NotNull(layout);
        Assert.Equal(At("custom", "GimmeCapture"), layout.Root);
    }

    [Fact]
    public void ALayoutTheHostDidNotMake_IsLeftAlone()
    {
        bool windows = OperatingSystem.IsWindows();

        // `dotnet GimmeCapture.dll`: the app runs from bin, the process is dotnet. Its siblings are other builds.
        Assert.Null(BundleExtractionCleaner.ResolveLayout(
            At("repo", "bin", "Release", "net10.0"), At("dotnet", "dotnet"), null, At("temp"), windows));
        // Named like the app, but not under .net and no override says otherwise.
        Assert.Null(BundleExtractionCleaner.ResolveLayout(
            At("elsewhere", "GimmeCapture", "Abc123"), At("install", ExeName), null, At("temp"), windows));
    }

    [Fact]
    public void AFolderBuild_OnWindows_SweepsWhatTheSingleFileVersionsLeft()
    {
        var layout = BundleExtractionCleaner.ResolveLayout(
            At("install"), At("install", ExeName), null, At("temp"), isWindows: true);

        Assert.NotNull(layout);
        Assert.Equal(At("temp", ".net", Path.GetFileNameWithoutExtension(ExeName)), layout.Root);
        Assert.Null(layout.Current);
    }

    [Fact]
    public void AFolderBuild_ElsewhereHasNothingToSweep()
    {
        Assert.Null(BundleExtractionCleaner.ResolveLayout(
            At("install"), At("install", ExeName), null, At("temp"), isWindows: false));
    }

    [Fact]
    public void Sweep_RemovesOldCopies_AndKeepsItsOwn_AFreshOne_AndOneInUse()
    {
        string root = At(".net", "GimmeCapture");
        string current = MakeCopy(root, "Current", ageDays: 30);
        string old = MakeCopy(root, "Old", ageDays: 30);
        string fresh = MakeCopy(root, "Fresh", ageDays: 0);
        string busy = MakeCopy(root, "Busy", ageDays: 30);

        BundleExtractionCleaner.SweepResult result;
        using (var mapping = MemoryMappedFile.CreateFromFile(Path.Combine(busy, "lib.dll"), FileMode.Open, null, 0, MemoryMappedFileAccess.Read))
        using (mapping.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read))
        {
            result = BundleExtractionCleaner.Sweep(new BundleExtractionCleaner.Layout(root, current), Now);
        }

        Assert.False(Directory.Exists(old));
        Assert.True(Directory.Exists(current));
        Assert.True(Directory.Exists(fresh));
        Assert.True(Directory.Exists(busy));
        Assert.Equal(1, result.Removed);
        Assert.Equal(1, result.InUse);
        Assert.Equal(2 * 4096, result.BytesFreed);
    }

    [Fact]
    public void Sweep_ForAFolderBuild_RemovesTheRootOnceEmpty()
    {
        string root = At("temp", ".net", "GimmeCapture");
        MakeCopy(root, "Old", ageDays: 30);

        var result = BundleExtractionCleaner.Sweep(new BundleExtractionCleaner.Layout(root, null), Now);

        Assert.Equal(1, result.Removed);
        Assert.False(Directory.Exists(root));
    }

    [Fact]
    public void MapsReference_MatchesFilesUnderTheFolder_NotASiblingWithTheSamePrefix()
    {
        const string maps =
            "7f3c2a000000-7f3c2a200000 r--p 00000000 08:01 1234 /home/u/.net/GimmeCapture/Abc123/libSkiaSharp.so\n" +
            "7f3c2b000000-7f3c2b001000 r--p 00000000 08:01 5678 /usr/lib/libc.so.6\n";

        Assert.True(BundleExtractionCleaner.MapsReference(maps, "/home/u/.net/GimmeCapture/Abc123/"));
        Assert.False(BundleExtractionCleaner.MapsReference(maps, "/home/u/.net/GimmeCapture/Abc12/"));
    }

    private static string MakeCopy(string root, string name, int ageDays)
    {
        string directory = Path.Combine(root, name);
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(Path.Combine(directory, "lib.dll"), new byte[4096]);
        File.WriteAllBytes(Path.Combine(directory, "app.dll"), new byte[4096]);
        Directory.SetLastWriteTimeUtc(directory, Now - TimeSpan.FromDays(ageDays));
        return directory;
    }
}
