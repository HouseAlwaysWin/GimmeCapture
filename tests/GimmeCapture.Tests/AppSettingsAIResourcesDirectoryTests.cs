using System;
using System.IO;

namespace GimmeCapture.Tests;

// Which AI resources folder a loaded config uses, and what moves into it. Older versions kept AI resources in the
// executable's own AI folder and copied that into the install's shared folder on every load, even when the config kept
// using the executable's folder: the installed copy on one machine had an unused 3.4 GB second copy of every model.
public sealed class AppSettingsAIResourcesDirectoryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "GimmeCapture.Tests", Guid.NewGuid().ToString("N"));

    private string Shared => Path.Combine(_root, "instances", "0123456789abcdef", "shared", "AI");
    private string Legacy => Path.Combine(_root, "app", "AI");

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

    private static string Write(string path, int length = 4)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[length]);
        return path;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WithTheSharedFolderInUse_TheExecutablesAIFolderMovesIntoIt(bool namedInConfig)
    {
        Write(Path.Combine(Legacy, "models", "u2net.onnx"));
        Write(Path.Combine(Legacy, "ocr", "ocr_det.onnx"));

        string directory = AppSettingsService.ResolveAIResourcesDirectory(namedInConfig ? Shared : "", Shared, Legacy);

        Assert.Equal(Shared, directory);
        Assert.True(File.Exists(Path.Combine(Shared, "models", "u2net.onnx")));
        Assert.True(File.Exists(Path.Combine(Shared, "ocr", "ocr_det.onnx")));
        Assert.False(Directory.Exists(Legacy));
    }

    [Fact]
    public void AFolderTheConfigNames_StaysInUse_AndIsNotCopiedIntoTheSharedOne()
    {
        string model = Write(Path.Combine(Legacy, "models", "u2net.onnx"));

        string directory = AppSettingsService.ResolveAIResourcesDirectory(Legacy, Shared, Legacy);

        Assert.Equal(Legacy, directory);
        Assert.True(File.Exists(model));
        Assert.False(Directory.Exists(Shared));
    }

    [Fact]
    public void CopiesAnOlderVersionMade_AreDroppedFromTheExecutablesFolder_AndADifferentFileStays()
    {
        Write(Path.Combine(Legacy, "models", "u2net.onnx"), 10);
        Write(Path.Combine(Shared, "models", "u2net.onnx"), 10);
        string conflicting = Write(Path.Combine(Legacy, "models", "sam2_hiera_tiny_encoder.onnx"), 5);
        Write(Path.Combine(Shared, "models", "sam2_hiera_tiny_encoder.onnx"), 7);
        Write(Path.Combine(Legacy, "ocr", "ocr_det.onnx"));

        AppSettingsService.ResolveAIResourcesDirectory(Shared, Shared, Legacy);

        Assert.False(File.Exists(Path.Combine(Legacy, "models", "u2net.onnx")));
        Assert.True(File.Exists(conflicting));
        Assert.Equal(7, new FileInfo(Path.Combine(Shared, "models", "sam2_hiera_tiny_encoder.onnx")).Length);
        Assert.True(File.Exists(Path.Combine(Shared, "ocr", "ocr_det.onnx")));
        Assert.False(Directory.Exists(Path.Combine(Legacy, "ocr")));
    }

    [Fact]
    public void AVersionScopedFolder_MovesIntoTheSharedOne()
    {
        string versionScoped = Path.Combine(_root, "instances", "0123456789abcdef", "versions", "0.30.0", "AI");
        Write(Path.Combine(versionScoped, "models", "u2net.onnx"));

        string directory = AppSettingsService.ResolveAIResourcesDirectory(versionScoped, Shared, Legacy);

        Assert.Equal(Shared, directory);
        Assert.True(File.Exists(Path.Combine(Shared, "models", "u2net.onnx")));
        Assert.False(Directory.Exists(versionScoped));
    }
}
