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
