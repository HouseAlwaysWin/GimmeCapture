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
    public void FromList_ReadsTheListsOtherFiles_InItsOrder_EachOnce()
    {
        string app = Path.Combine(Path.GetTempPath(), "GimmeCapture-list", "App");
        var lines = new[]
        {
            "System.Private.CoreLib.dll",
            "GimmeCapture.dll",
            "Serilog.dll",
            "",
            "Avalonia.Base.dll",
            "serilog.dll",
            Path.Combine("runtimes", "win-x64", "native", "libSkiaSharp.dll"),
            Path.Combine("..", "Elsewhere.dll"),
            Path.Combine(Path.GetTempPath(), "rooted.dll"),
            "coreclr.dll",
        };

        Assert.Equal(
            new[]
            {
                Path.Combine(app, "Serilog.dll"),
                Path.Combine(app, "Avalonia.Base.dll"),
                Path.Combine(app, "runtimes", "win-x64", "native", "libSkiaSharp.dll"),
            },
            ReadAhead.FromList(lines, app));
    }

    [Fact]
    public void Plan_ReadsBeforeMainFirst_ThenTheListsOtherFiles_EachOnce()
    {
        string app = Path.Combine(Path.GetTempPath(), "GimmeCapture-list", "App");

        var plan = ReadAhead.Plan(["Serilog.dll", "coreclr.dll", "Avalonia.Base.dll", "serilog.dll"], app);

        Assert.Equal(
            ReadAhead.BeforeMain.Select(name => Path.Combine(app, name))
                .Concat([Path.Combine(app, "Serilog.dll"), Path.Combine(app, "Avalonia.Base.dll")]),
            plan);
    }

    [Theory]
    [InlineData("Avalonia.Base.dll|0+4096,1310720+262144")]
    [InlineData("Avalonia.Base.dll|")]
    [InlineData(" Avalonia.Base.dll | x ")]
    public void ALineV0740Wrote_WithTheStretchesItsStartUsed_IsReadWhole(string line)
    {
        string app = Path.Combine(Path.GetTempPath(), "GimmeCapture-list", "App");

        Assert.Equal([Path.Combine(app, "Avalonia.Base.dll")], ReadAhead.Resolve([line, "|0+4096"], app));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(16)]
    public void ReadAll_ReadsEveryFileWhole_AndSkipsOneThatIsMissing(int threads)
    {
        string folder = Path.Combine(Path.GetTempPath(), "GimmeCapture-readahead-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var files = Enumerable.Range(0, 6).Select(i => Path.Combine(folder, $"f{i}.dll")).ToList();
            for (int i = 0; i < files.Count; i++)
            {
                File.WriteAllBytes(files[i], new byte[(i << 20) + 3]);
            }

            files.Insert(2, Path.Combine(folder, "gone.dll"));

            Assert.Equal(Enumerable.Range(0, 6).Sum(i => (i << 20) + 3L), ReadAhead.ReadAll(files, threads));
            Assert.Equal(0, ReadAhead.ReadAll([], threads));
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
