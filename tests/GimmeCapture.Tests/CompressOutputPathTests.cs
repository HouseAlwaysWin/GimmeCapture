using System;
using System.IO;
using GimmeCapture.Services.Core.Media;
using GimmeCapture.ViewModels.Main;
using Xunit;

namespace GimmeCapture.Tests;

// Paths are built with Path.Combine (not Windows literals) so these run on both the
// net10.0-windows and net10.0 (Linux CI) heads — GetDirectoryName can't split "D:\..."
// strings on Unix, which is a test-input concern, not an app bug.
public class CompressOutputPathTests
{
    private static readonly DateTime Stamp = new(2026, 6, 29, 14, 30, 52);
    private const string Date = "20260629_143052";

    private static readonly string SourceDir = Path.Combine(Path.GetTempPath(), "gimme_src");
    private static readonly string SourcePath = Path.Combine(SourceDir, "clipA.mp4");

    [Fact]
    public void UsesRootFolder_AppendsDate_FromCustomName()
    {
        string root = Path.Combine(Path.GetTempPath(), "gimme_out_root_" + Guid.NewGuid().ToString("N"));
        string p = CompressOutputPath.BuildBatchOutputPath(SourcePath, "myname", root, ".mp4", true, Stamp);
        Assert.Equal(Path.Combine(root, $"myname_{Date}.mp4"), p);
    }

    [Fact]
    public void OutputName_SubfolderGoesUnderRoot()
    {
        string root = Path.Combine(Path.GetTempPath(), "gimme_out_sub_" + Guid.NewGuid().ToString("N"));
        // "/sub/clip" is normalized on every platform; the Windows-typed "\sub\clip" form is
        // covered by the Windows-only fact below.
        string p = CompressOutputPath.BuildBatchOutputPath(SourcePath, "/sub/clip", root, ".mp4", true, Stamp);
        Assert.Equal(Path.Combine(root, "sub", $"clip_{Date}.mp4"), p);
    }

    [Fact]
    public void OutputName_BackslashSubfolder_GoesUnderRoot_OnWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            // On Unix a backslash is a legal file-name character, so "\sub\clip" is a single
            // (odd) name there by design; the backslash-split behaviour is Windows-specific.
            return;
        }

        string root = Path.Combine(Path.GetTempPath(), "gimme_out_bsub_" + Guid.NewGuid().ToString("N"));
        string p = CompressOutputPath.BuildBatchOutputPath(SourcePath, @"\sub\clip", root, ".mp4", true, Stamp);
        Assert.Equal(Path.Combine(root, "sub", $"clip_{Date}.mp4"), p);
    }

    [Fact]
    public void BlankName_FallsBackToSourceName()
    {
        string root = Path.Combine(Path.GetTempPath(), "gimme_out_blank_" + Guid.NewGuid().ToString("N"));
        string p = CompressOutputPath.BuildBatchOutputPath(SourcePath, "   ", root, ".mkv", false, Stamp);
        Assert.Equal(Path.Combine(root, "clipA.mkv"), p);
    }

    [Fact]
    public void NoRoot_UsesSourceFolder()
    {
        string p = CompressOutputPath.BuildBatchOutputPath(SourcePath, "out", null, ".mp4", false, Stamp);
        Assert.Equal(Path.Combine(SourceDir, "out.mp4"), p);
    }

    [Fact]
    public void TraversalAndDrive_AreStripped_CannotEscapeRoot()
    {
        string root = Path.Combine(Path.GetTempPath(), "gimme_out_safe_" + Guid.NewGuid().ToString("N"));
        string p = CompressOutputPath.BuildBatchOutputPath(SourcePath, "../../C:/evil", root, ".mp4", false, Stamp);
        // ".." segments dropped and the "C:" drive sanitized to a plain segment — the result stays under root.
        Assert.StartsWith(root + Path.DirectorySeparatorChar, p);
        Assert.DoesNotContain("..", p);
    }

    [Fact]
    public void AppendDateOff_OmitsStamp()
    {
        string p = CompressOutputPath.BuildBatchOutputPath(SourcePath, "name", null, ".mp4", false, Stamp);
        Assert.EndsWith(Path.DirectorySeparatorChar + "name.mp4", p);
    }
}
