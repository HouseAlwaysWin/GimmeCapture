using System.Linq;
using GimmeCapture.Models;
using GimmeCapture.Services.Core.Media.NativeFFmpeg;
using GimmeCapture.ViewModels.Main;
using Xunit;

namespace GimmeCapture.Tests;

// The HandBrake-style compress filters: BuildVideoFilterChain maps the options to an ordered libavfilter node
// list (pure/static, no libav needed), and the per-row "open output folder" gate (HasOutput). No UI thread.
public class CompressVideoFilterTests
{
    [Fact]
    public void NoFilters_ReturnsEmptyChain()
    {
        Assert.Empty(LibavClipExporter.BuildVideoFilterChain(new LibavExportOptions()));
    }

    [Theory]
    [InlineData(DenoiseMode.Light, "hqdn3d", "2:1.5:3:3")]
    [InlineData(DenoiseMode.Medium, "hqdn3d", "4:3:6:4.5")]
    [InlineData(DenoiseMode.Strong, "hqdn3d", "8:6:12:9")]
    [InlineData(DenoiseMode.NLMeans, "nlmeans", "")]
    public void Denoise_MapsToExpectedNode(DenoiseMode mode, string name, string args)
    {
        var chain = LibavClipExporter.BuildVideoFilterChain(new LibavExportOptions { Denoise = mode });
        (string Name, string Args) node = Assert.Single(chain);
        Assert.Equal(name, node.Name);
        Assert.Equal(args, node.Args);
    }

    [Theory]
    [InlineData(SharpenMode.Light, "3:3:0.5:3:3:0.0")]
    [InlineData(SharpenMode.Medium, "5:5:1.0:5:5:0.0")]
    [InlineData(SharpenMode.Strong, "7:7:1.6:7:7:0.0")]
    public void Sharpen_MapsToUnsharp(SharpenMode mode, string args)
    {
        var chain = LibavClipExporter.BuildVideoFilterChain(new LibavExportOptions { Sharpen = mode });
        (string Name, string Args) node = Assert.Single(chain);
        Assert.Equal("unsharp", node.Name);
        Assert.Equal(args, node.Args);
    }

    [Fact]
    public void Deblock_On_AddsDeblockNode()
    {
        var chain = LibavClipExporter.BuildVideoFilterChain(new LibavExportOptions { Deblock = true });
        Assert.Equal("deblock", Assert.Single(chain).Name);
    }

    [Fact]
    public void Grayscale_On_DesaturatesViaHue()
    {
        var chain = LibavClipExporter.BuildVideoFilterChain(new LibavExportOptions { Grayscale = true });
        (string Name, string Args) node = Assert.Single(chain);
        Assert.Equal("hue", node.Name);
        Assert.Equal("s=0", node.Args); // keeps the yuv pixel format (no format= tail)
    }

    [Fact]
    public void AllFilters_AreOrdered_DenoiseDeblockSharpenGrayscale()
    {
        var chain = LibavClipExporter.BuildVideoFilterChain(new LibavExportOptions
        {
            Denoise = DenoiseMode.Medium,
            Deblock = true,
            Sharpen = SharpenMode.Medium,
            Grayscale = true,
        });
        Assert.Equal(new[] { "hqdn3d", "deblock", "unsharp", "hue" }, chain.Select(n => n.Name).ToArray());
    }

    [Fact]
    public void HasOutput_TrueOnlyWhenDoneWithAProducedPath()
    {
        var item = new MainWindowViewModel.CompressQueueItem(@"D:\Videos\clip.mp4");
        Assert.False(item.HasOutput);                 // fresh: Queued, no output path

        item.OutputPath = @"D:\out\clip_20260707.mp4";
        Assert.False(item.HasOutput);                 // has a path but not finished yet

        item.Status = MainWindowViewModel.CompressQueueStatus.Done;
        Assert.True(item.HasOutput);                  // Done + path → reveal button enabled

        item.OutputPath = null;
        Assert.False(item.HasOutput);                 // Done but the path was cleared
    }
}
