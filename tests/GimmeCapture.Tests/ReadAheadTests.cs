using System;
using System.IO;
using GimmeCapture.Launcher;

namespace GimmeCapture.Tests;

// The sign-in launcher's shared half: what it reads before the app's Main took 18-37 s from a hard disk at sign-in,
// and what it hands the app so the start-up line can show the whole of a start.
public sealed class ReadAheadTests
{
    [Theory]
    [InlineData("\"D:\\GimmeCapture_win-x64\\GimmeCapture.Launcher.exe\" --startup", "--startup")]
    [InlineData("\"C:\\Program Files\\A B\\GimmeCapture.Launcher.exe\" --startup --other \"x y\"", "--startup --other \"x y\"")]
    [InlineData("D:\\Apps\\GimmeCapture.Launcher.exe --startup", "--startup")]
    [InlineData("D:\\Apps\\GimmeCapture.Launcher.exe\t--startup", "--startup")]
    [InlineData("  \"D:\\Apps\\GimmeCapture.Launcher.exe\"", "")]
    [InlineData("D:\\Apps\\GimmeCapture.Launcher.exe", "")]
    public void ArgumentsAfterProgram_PassesTheArgumentsOnAsTheyCame(string commandLine, string expected)
    {
        Assert.Equal(expected, ReadAhead.ArgumentsAfterProgram(commandLine));
    }

    [Fact]
    public void TheHandOff_RoundTrips()
    {
        var started = new DateTime(2026, 11, 3, 12, 0, 1, DateTimeKind.Utc);

        Assert.True(ReadAhead.TryParseHandOff(ReadAhead.FormatHandOff(started, 62_638_064), out var parsedStart, out long bytes));
        Assert.Equal(started, parsedStart);
        Assert.Equal(DateTimeKind.Utc, parsedStart.Kind);
        Assert.Equal(62_638_064, bytes);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("12345")]
    [InlineData("1;2;3")]
    [InlineData("-5;100")]
    [InlineData("abc;100")]
    [InlineData("99999999999999999999;100")]
    public void TheHandOff_IgnoresWhatItDidNotWrite(string? value)
    {
        Assert.False(ReadAhead.TryParseHandOff(value, out _, out _));
    }

    [Fact]
    public void WhatIsReadAhead_IsWhatDotNetsHostAndRuntimeLoadBeforeMain()
    {
        Assert.Equal(ReadAhead.AppFileName, ReadAhead.BeforeMain[0]);
        Assert.Contains("hostfxr.dll", ReadAhead.BeforeMain);
        Assert.Contains("hostpolicy.dll", ReadAhead.BeforeMain);
        Assert.Contains("coreclr.dll", ReadAhead.BeforeMain);
        Assert.Contains("System.Private.CoreLib.dll", ReadAhead.BeforeMain);
        Assert.Contains("GimmeCapture.dll", ReadAhead.BeforeMain);
    }

    [Theory]
    [InlineData("0.71.0")]
    [InlineData("v0.72.1")]
    [InlineData(" 1.0.0 beta ")]
    [InlineData("")]
    public void TheStartupList_IsWhereTheAppWritesIt(string version)
    {
        // The launcher works the path out without the app's code; the app writes the list beside its version's settings.
        string folder = Path.Combine(Path.GetTempPath(), "GimmeCapture_win-x64");
        foreach (string spelling in new[] { folder, folder + Path.DirectorySeparatorChar, folder.ToUpperInvariant() })
        {
            Assert.Equal(
                Path.Combine(AppStoragePaths.GetVersionedDataDirectory(spelling, version), ReadAhead.StartupListFileName),
                ReadAhead.StartupListPath(AppStoragePaths.GetRootDirectory(), spelling, version));
        }
    }

    [WindowsFact]
    public void AnInstallsKey_StaysWhatItWas()
    {
        // The folder under instances\ that an install's settings and AI modules have been in since it was installed.
        Assert.Equal("b0337ff88a000ffc", ReadAhead.InstallInstanceKey(@"D:\GimmeCapture_win-x64"));
        Assert.Equal("b0337ff88a000ffc", AppStoragePaths.GetInstallInstanceKey(@"D:\GimmeCapture_win-x64"));
    }

    [Theory]
    [InlineData("0.71.0+bed41298be799d19c42b408b29f3022d74610ece", "0.71.0")]
    [InlineData("0.72.1", "0.72.1")]
    [InlineData(" 0.72.1 +abc", "0.72.1")]
    [InlineData("+abc", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void AppVersion_IsTheProductVersionUpToItsCommit(string? productVersion, string? expected)
    {
        Assert.Equal(expected, ReadAhead.AppVersion(productVersion));
    }

    [Fact]
    public void Plan_ReadsBeforeMainFirst_WithTheListsStretches_ThenTheListsOtherFiles_InItsOrder_EachOnce()
    {
        string app = Path.Combine(Path.GetTempPath(), "GimmeCapture-list", "App");
        var lines = new[]
        {
            "System.Private.CoreLib.dll|0+4096,65536+8192",
            "GimmeCapture.dll|0+1048576",
            "Serilog.dll",
            "",
            "Avalonia.Base.dll|4096+4096",
            "serilog.dll|0+4096",
            Path.Combine("runtimes", "win-x64", "native", "libSkiaSharp.dll"),
            Path.Combine("..", "Elsewhere.dll"),
            Path.Combine(Path.GetTempPath(), "rooted.dll"),
            "coreclr.dll",
        };

        var plan = ReadAhead.Plan(lines, app);

        // .NET's host and runtime first, in the order they load, then the rest of the list in its order.
        Assert.Equal(
            ReadAhead.BeforeMain.Select(name => Path.Combine(app, name))
                .Concat(
                [
                    Path.Combine(app, "Serilog.dll"),
                    Path.Combine(app, "Avalonia.Base.dll"),
                    Path.Combine(app, "runtimes", "win-x64", "native", "libSkiaSharp.dll"),
                ]),
            plan.Select(file => file.Path));
        Assert.Equal([new FileRange(0, 4096), new FileRange(65536, 8192)], Stretches(plan, "System.Private.CoreLib.dll"));
        Assert.Equal([new FileRange(0, 1 << 20)], Stretches(plan, "GimmeCapture.dll"));
        // Listed without stretches, or not listed at all: all of it.
        Assert.Null(Stretches(plan, "coreclr.dll"));
        Assert.Null(Stretches(plan, "GimmeCapture.deps.json"));
        // The first line for a file is the one read.
        Assert.Null(Stretches(plan, "Serilog.dll"));
        Assert.Equal([new FileRange(4096, 4096)], Stretches(plan, "Avalonia.Base.dll"));

        IReadOnlyList<FileRange>? Stretches(List<StartupFile> files, string name) =>
            files.Single(file => file.Path == Path.Combine(app, name)).Stretches;
    }

    [Fact]
    public void ALinesStretches_RoundTrip()
    {
        var stretches = new[] { new FileRange(0, 4096), new FileRange(1_310_720, 262_144) };
        string line = ReadAhead.FormatLine(Path.Combine("runtimes", "x.dll"), stretches);

        Assert.Equal(Path.Combine("runtimes", "x.dll") + "|0+4096,1310720+262144", line);
        Assert.Equal(Path.Combine("runtimes", "x.dll"), ReadAhead.ParseLine(line, out var parsed));
        Assert.Equal(stretches, parsed);
        Assert.Equal("a.dll", ReadAhead.FormatLine("a.dll", null));
        Assert.Equal("a.dll", ReadAhead.FormatLine("a.dll", []));
    }

    [Theory]
    [InlineData("a.dll")]
    [InlineData("a.dll|")]
    [InlineData("a.dll|0+4096,oops")]
    [InlineData("a.dll|0+0")]
    [InlineData("a.dll|-5+10")]
    [InlineData("a.dll|10")]
    public void ALineWithoutStretchesItCanRead_IsReadWhole(string line)
    {
        Assert.Equal("a.dll", ReadAhead.ParseLine(line, out var stretches));
        Assert.Null(stretches);
    }

    [Fact]
    public void ReadStretches_ReadsEachOnce_AndWhatAFileHas()
    {
        string folder = Path.Combine(Path.GetTempPath(), "GimmeCapture-readahead-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            string file = Path.Combine(folder, "Avalonia.Base.dll");
            File.WriteAllBytes(file, new byte[(3 << 20) + 100]);
            var buffer = new byte[1 << 20];

            // The second runs past the end of the file, which has only 100 bytes of it.
            Assert.Equal(
                4096 + 100,
                ReadAhead.ReadStretches(file, [new FileRange(8192, 4096), new FileRange(3 << 20, 4096)], buffer));
            Assert.Equal(
                (2 << 20) + 1,
                ReadAhead.Read(new StartupFile(file, [new FileRange(0, (2 << 20) + 1)]), buffer));
            Assert.Equal((3 << 20) + 100, ReadAhead.Read(new StartupFile(file, null), buffer));
            Assert.Equal(0, ReadAhead.ReadStretches(Path.Combine(folder, "gone.dll"), [new FileRange(0, 4096)], buffer));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void ReadThrough_ReadsAFileWhole_AndNothingOfOneThatIsMissing()
    {
        string folder = Path.Combine(Path.GetTempPath(), "GimmeCapture-readahead-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            string file = Path.Combine(folder, "coreclr.dll");
            File.WriteAllBytes(file, new byte[(2 << 20) + 7]);
            var buffer = new byte[1 << 20];

            Assert.Equal((2 << 20) + 7, ReadAhead.ReadThrough(file, buffer));
            Assert.Equal(0, ReadAhead.ReadThrough(Path.Combine(folder, "gone.dll"), buffer));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
