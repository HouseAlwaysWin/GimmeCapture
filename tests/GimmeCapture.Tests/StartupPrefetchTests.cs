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
    public void Format_SaysWhatTheStartRead()
    {
        Assert.Equal(" prefetch=1840ms/142MB", StartupPrefetch.Format(false, true, TimeSpan.FromMilliseconds(1840), 142L << 20));
        // The first start of a version has no list yet; it writes one.
        Assert.Equal(" prefetch=none", StartupPrefetch.Format(false, false, TimeSpan.FromMilliseconds(3), 0));
        Assert.Equal(" prefetch=running", StartupPrefetch.Format(false, true, null, 50L << 20));
        Assert.Equal(" prefetch=failed", StartupPrefetch.Format(true, true, TimeSpan.FromMilliseconds(3), 0));
    }
}
