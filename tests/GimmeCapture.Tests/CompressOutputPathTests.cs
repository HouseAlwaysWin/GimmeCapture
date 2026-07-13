using System;
using System.IO;
using GimmeCapture.Services.Core.Media;
using GimmeCapture.ViewModels.Main;
using Xunit;

namespace GimmeCapture.Tests;

public class CompressOutputPathTests
{
    private static readonly DateTime Stamp = new(2026, 6, 29, 14, 30, 52);
    private const string Date = "20260629_143052";

    [Fact]
    public void UsesRootFolder_AppendsDate_FromCustomName()
    {
        string root = Path.Combine(Path.GetTempPath(), "gimme_out_root_" + Guid.NewGuid().ToString("N"));
        string p = CompressOutputPath.BuildBatchOutputPath(@"D:\src\clipA.mp4", "myname", root, ".mp4", true, Stamp);
        Assert.Equal(Path.Combine(root, $"myname_{Date}.mp4"), p);
    }

    [Fact]
    public void OutputName_SubfolderGoesUnderRoot()
    {
        string root = Path.Combine(Path.GetTempPath(), "gimme_out_sub_" + Guid.NewGuid().ToString("N"));
        string p = CompressOutputPath.BuildBatchOutputPath(@"D:\src\clipA.mp4", @"\sub\clip", root, ".mp4", true, Stamp);
        Assert.Equal(Path.Combine(root, "sub", $"clip_{Date}.mp4"), p);
    }

    [Fact]
    public void BlankName_FallsBackToSourceName()
    {
        string root = Path.Combine(Path.GetTempPath(), "gimme_out_blank_" + Guid.NewGuid().ToString("N"));
        string p = CompressOutputPath.BuildBatchOutputPath(@"D:\src\clipA.mp4", "   ", root, ".mkv", false, Stamp);
        Assert.Equal(Path.Combine(root, "clipA.mkv"), p);
    }

    [Fact]
    public void NoRoot_UsesSourceFolder()
    {
        string p = CompressOutputPath.BuildBatchOutputPath(@"D:\src\clipA.mp4", "out", null, ".mp4", false, Stamp);
        Assert.Equal(Path.Combine(@"D:\src", "out.mp4"), p);
    }

    [Fact]
    public void TraversalAndDrive_AreStripped_CannotEscapeRoot()
    {
        string root = Path.Combine(Path.GetTempPath(), "gimme_out_safe_" + Guid.NewGuid().ToString("N"));
        string p = CompressOutputPath.BuildBatchOutputPath(@"D:\src\clipA.mp4", @"..\..\C:\evil", root, ".mp4", false, Stamp);
        // ".." segments dropped and the "C:" drive sanitized to a plain segment — the result stays under root.
        Assert.StartsWith(root + Path.DirectorySeparatorChar, p);
        Assert.DoesNotContain("..", p);
    }

    [Fact]
    public void AppendDateOff_OmitsStamp()
    {
        string p = CompressOutputPath.BuildBatchOutputPath(@"D:\src\clipA.mp4", "name", null, ".mp4", false, Stamp);
        Assert.EndsWith(Path.DirectorySeparatorChar + "name.mp4", p);
    }
}
