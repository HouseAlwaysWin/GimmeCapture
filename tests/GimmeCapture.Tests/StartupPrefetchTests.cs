using System;
using System.IO;
using GimmeCapture.Launcher;

namespace GimmeCapture.Tests;

// Started at sign-in from a hard disk, the app waited 46 s on thousands of small random reads of its own files; each
// start now writes down the files it mapped and the next one reads them through first.
public sealed class StartupPrefetchTests : IDisposable
{
    private readonly string _app = Path.Combine(Path.GetTempPath(), "GimmeCapture-prefetch-" + Guid.NewGuid().ToString("N"), "App");

    public StartupPrefetchTests() => Directory.CreateDirectory(_app);

    public void Dispose() => Directory.Delete(Path.GetDirectoryName(_app)!, recursive: true);

    [Fact]
    public void Order_PutsWhatLoadedBeforeMainFirst_ThenTheLoadersModules_ThenTheRest_EachOnce()
    {
        var order = StartupPrefetch.Order(
            ["GimmeCapture.dll", "System.Private.CoreLib.dll"],
            ["coreclr.dll", "libSkiaSharp.dll"],
            ["System.Private.CoreLib.dll", "Avalonia.Base.dll", "gimmecapture.dll"]);

        Assert.Equal(["GimmeCapture.dll", "System.Private.CoreLib.dll", "coreclr.dll", "libSkiaSharp.dll", "Avalonia.Base.dll"], order);
    }

    [Fact]
    public void RelativeToApp_KeepsOnlyFilesInsideTheAppsFolder()
    {
        Assert.Equal("Avalonia.Base.dll", ReadAhead.RelativeToApp(_app, Path.Combine(_app, "Avalonia.Base.dll")));
        Assert.Equal(Path.Combine("runtimes", "native", "x.dll"), ReadAhead.RelativeToApp(_app, Path.Combine(_app, "runtimes", "native", "x.dll")));
        // A trailing separator on the folder changes nothing.
        Assert.Equal("a.dll", ReadAhead.RelativeToApp(_app + Path.DirectorySeparatorChar, Path.Combine(_app, "a.dll")));

        Assert.Null(ReadAhead.RelativeToApp(_app, Path.Combine(Path.GetTempPath(), "elsewhere.dll")));
        // A sibling whose name starts like the app's folder is not inside it.
        Assert.Null(ReadAhead.RelativeToApp(_app, _app + "2" + Path.DirectorySeparatorChar + "x.dll"));
        Assert.Null(ReadAhead.RelativeToApp(_app, _app));
        Assert.Null(ReadAhead.RelativeToApp(_app, ""));
        Assert.Null(ReadAhead.RelativeToApp(_app, null));
    }

    [Fact]
    public void Resolve_SkipsBlankLines_RootedPaths_AndAnythingThatLeavesTheAppsFolder()
    {
        var files = ReadAhead.Resolve(
            [
                "Avalonia.Base.dll",
                "   ",
                Path.Combine("runtimes", "x.dll"),
                Path.Combine("..", "outside.dll"),
                Path.Combine(Path.GetTempPath(), "rooted.dll"),
                " coreclr.dll ",
            ],
            _app);

        Assert.Equal(
            [Path.Combine(_app, "Avalonia.Base.dll"), Path.Combine(_app, "runtimes", "x.dll"), Path.Combine(_app, "coreclr.dll")],
            files);
    }

    [Fact]
    public void WithWarmUp_AddsWhatTheWarmUpLoaded_AfterTheList_InItsOrder()
    {
        // The warm-up 11 s after a sign-in waited 637 ms on the hard disk for files the start-up list did not have.
        var lines = StartupPrefetch.WithWarmUp(
            ["coreclr.dll", " GimmeCapture.dll ", ""],
            ["coreclr.dll", "libHarfBuzzSharp.DLL", "gimmecapture.dll", "Avalonia.Markup.dll"],
            _ => 1 << 20,
            StartupPrefetch.WarmUpLimit);

        Assert.Equal(["coreclr.dll", "GimmeCapture.dll", "libHarfBuzzSharp.DLL", "Avalonia.Markup.dll"], lines);
    }

    [Fact]
    public void WithWarmUp_ChangesNothing_WhenNothingNewWasLoaded()
    {
        Assert.Null(StartupPrefetch.WithWarmUp(["coreclr.dll", "GimmeCapture.dll"], ["CORECLR.dll"], _ => 1, StartupPrefetch.WarmUpLimit));
    }

    [Fact]
    public void WithWarmUp_LeavesItAll_WhenSomethingElseLoadedMore()
    {
        // A capture's OCR or a video's FFmpeg, loaded meanwhile, would cost every later sign-in its read.
        var sizes = new Dictionary<string, long>
        {
            ["libHarfBuzzSharp.DLL"] = 1_800_000,
            [Path.Combine("ffmpeg-lib", "avcodec-62.dll")] = 118_000_000,
        };

        Assert.Null(StartupPrefetch.WithWarmUp(["coreclr.dll"], sizes.Keys, name => sizes[name], StartupPrefetch.WarmUpLimit));
        Assert.NotNull(StartupPrefetch.WithWarmUp(["coreclr.dll"], ["libHarfBuzzSharp.DLL"], name => sizes[name], StartupPrefetch.WarmUpLimit));
    }

    [Fact]
    public void WithWarmUp_NeitherAddsNorCountsTheFilesReadAfterTheStart()
    {
        // The warm-up draws with the CJK fonts, 29.7 MB, which would leave everything else it loaded out of the list.
        var sizes = new Dictionary<string, long>
        {
            ["GimmeCapture.Fonts.dll"] = 31_200_000,
            ["libHarfBuzzSharp.DLL"] = 1_800_000,
        };

        var lines = StartupPrefetch.WithWarmUp(["coreclr.dll"], sizes.Keys, name => sizes[name], StartupPrefetch.WarmUpLimit);

        Assert.Equal(["coreclr.dll", "libHarfBuzzSharp.DLL"], lines);
        Assert.Null(StartupPrefetch.WithWarmUp(
            ["coreclr.dll"], ["GimmeCapture.Fonts.dll"], name => sizes[name], StartupPrefetch.WarmUpLimit));
    }

    [Fact]
    public void Format_SaysWhatTheStartRead()
    {
        Assert.Equal(" prefetch=1840ms/142MB", StartupPrefetch.Format(false, true, TimeSpan.FromMilliseconds(1840), 142L << 20));
        // The first start of a version has no list yet; it writes one.
        Assert.Equal(" prefetch=none", StartupPrefetch.Format(false, false, TimeSpan.FromMilliseconds(3), 0));
        Assert.Equal(" prefetch=running", StartupPrefetch.Format(false, true, null, 50L << 20));
        Assert.Equal(" prefetch=failed", StartupPrefetch.Format(true, true, TimeSpan.FromMilliseconds(3), 0));
    }
}
