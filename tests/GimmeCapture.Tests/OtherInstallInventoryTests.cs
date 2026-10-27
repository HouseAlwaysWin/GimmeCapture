using System;
using System.IO;
using System.Linq;

namespace GimmeCapture.Tests;

// Every folder the app runs from keeps its own settings and AI modules under instances\, so a copy that was moved,
// reinstalled elsewhere or deleted leaves them behind: 1.9 GB from one old build folder on the owner's machine.
public sealed class OtherInstallInventoryTests : IDisposable
{
    private const string CurrentKey = "0123456789abcdef";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "GimmeCapture.Tests", Guid.NewGuid().ToString("N"));

    private string Instances => Path.Combine(_root, "instances");

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

    private string Write(string relativePath, int length = 4)
    {
        string path = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[length]);
        return path;
    }

    [Fact]
    public void ListsOtherCopiesThatKeepFiles_LargestFirst()
    {
        Write(Path.Combine("instances", "aaaaaaaaaaaaaaaa", "versions", "0.68.1", "config.json"), 10);
        Write(Path.Combine("instances", "bbbbbbbbbbbbbbbb", "shared", "AI", "models", "u2net.onnx"), 30);
        Write(Path.Combine("instances", CurrentKey, "versions", "0.69.0", "config.json"), 99);
        Directory.CreateDirectory(Path.Combine(Instances, "cccccccccccccccc"));
        Write(Path.Combine("instances", "notes", "readme.txt"));

        var installs = OtherInstallInventory.Scan(Instances, CurrentKey, _ => false);

        Assert.Equal(new[] { "bbbbbbbbbbbbbbbb", "aaaaaaaaaaaaaaaa" }, installs.Select(install => install.Key));
        Assert.Equal(new long[] { 30, 10 }, installs.Select(install => install.SizeBytes));
    }

    [Fact]
    public void ACopyThatRecordedItself_SaysWhereItRuns_AndWhetherItIsStillThere()
    {
        string presentExe = Write(Path.Combine("present", "GimmeCapture.exe"));
        string goneExe = Path.Combine(_root, "gone", "GimmeCapture.exe");
        var lastRun = new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);
        foreach (var (key, exe) in new[] { ("aaaaaaaaaaaaaaaa", presentExe), ("bbbbbbbbbbbbbbbb", goneExe) })
        {
            string instance = Path.Combine(Instances, key);
            Directory.CreateDirectory(instance);
            InstallInstanceRecord.Write(instance, new InstallInstanceRecord(exe, "0.69.0", lastRun));
        }

        var installs = OtherInstallInventory.Scan(Instances, CurrentKey, key => key == "aaaaaaaaaaaaaaaa")
            .ToDictionary(install => install.Key);

        Assert.Equal(presentExe, installs["aaaaaaaaaaaaaaaa"].ExecutablePath);
        Assert.False(installs["aaaaaaaaaaaaaaaa"].IsGone);
        Assert.True(installs["aaaaaaaaaaaaaaaa"].IsRunning);
        Assert.True(installs["bbbbbbbbbbbbbbbb"].IsGone);
        Assert.False(installs["bbbbbbbbbbbbbbbb"].IsRunning);
        Assert.Equal("0.69.0", installs["bbbbbbbbbbbbbbbb"].Version);
        Assert.Equal(lastRun, installs["bbbbbbbbbbbbbbbb"].LastUsedUtc);
    }

    [Fact]
    public void ACopyFromBeforeRecords_IsDatedByItsNewestFile_AndVersionedByItsNewestConfigFolder()
    {
        var newest = new DateTime(2026, 7, 2, 8, 0, 0, DateTimeKind.Utc);
        string older = Write(Path.Combine("instances", "aaaaaaaaaaaaaaaa", "versions", "0.9.0", "config.json"));
        string newer = Write(Path.Combine("instances", "aaaaaaaaaaaaaaaa", "versions", "0.48.0", "config.json"));
        File.SetLastWriteTimeUtc(older, newest.AddDays(-30));
        File.SetLastWriteTimeUtc(newer, newest);

        var install = Assert.Single(OtherInstallInventory.Scan(Instances, CurrentKey, _ => false));

        Assert.Null(install.ExecutablePath);
        Assert.False(install.IsGone);
        Assert.Equal("0.48.0", install.Version);
        Assert.Equal(newest, install.LastUsedUtc);
    }

    [Fact]
    public void Remove_DeletesTheWholeInstanceFolder()
    {
        Write(Path.Combine("instances", "aaaaaaaaaaaaaaaa", "shared", "AI", "models", "u2net.onnx"));
        var install = Assert.Single(OtherInstallInventory.Scan(Instances, CurrentKey, _ => false));

        Assert.Null(OtherInstallInventory.Remove(install));

        Assert.False(Directory.Exists(install.Directory));
    }
}
