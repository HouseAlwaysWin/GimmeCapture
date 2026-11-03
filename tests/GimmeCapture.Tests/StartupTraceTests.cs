using System;
using System.Diagnostics;
using GimmeCapture.Services.Core.Infrastructure;

namespace GimmeCapture.Tests;

// Startup.Timings: a launch at sign-in took 70-80 s, and only each stage's CPU time beside its length says whether it
// was working or waiting.
public sealed class StartupTraceTests
{
    [Fact]
    public void Format_GivesEachStageItsLengthAndCpuTime_SinceThePreviousOne()
    {
        var mainCpu = TimeSpan.FromMilliseconds(20);
        var timings = new StartupTrace.Timings(
            Launch: "tray",
            BeforeMainMs: 50,
            MainTimestamp: At(1000),
            MainCpu: mainCpu,
            Stages:
            [
                new("log", At(1010), mainCpu + TimeSpan.FromMilliseconds(5)),
                new("platform", At(1210), mainCpu + TimeSpan.FromMilliseconds(55)),
            ],
            SettingsAppliedTimestamp: At(1215),
            HotkeysInitializedTimestamp: At(1205),
            SystemCpuPercent: 37);

        // The hotkeys work once the settings are applied as well, so hotkeysAt is the later of the two.
        Assert.Equal(
            "launch=tray beforeMain=50/20 log=10/5 platform=200/50 total=210ms cpu=55ms settingsAppliedAt=215ms "
                + "hotkeysAt=215ms systemCpu=37%",
            StartupTrace.Format(timings));
    }

    [Fact]
    public void Format_LeavesOutWhatCouldNotBeRead()
    {
        var timings = new StartupTrace.Timings(
            Launch: "window",
            BeforeMainMs: null,
            MainTimestamp: At(0),
            MainCpu: TimeSpan.FromMilliseconds(30),
            Stages: [new("firstFrame", At(100), TimeSpan.FromMilliseconds(130))],
            SettingsAppliedTimestamp: At(40),
            HotkeysInitializedTimestamp: At(60),
            SystemCpuPercent: null);

        Assert.Equal(
            "launch=window beforeMain=n/a/30 firstFrame=100/100 total=100ms cpu=100ms settingsAppliedAt=40ms hotkeysAt=60ms",
            StartupTrace.Format(timings));
    }

    [Fact]
    public void FormatLauncher_SaysHowLongTheLauncherTookAndWhatItRead()
    {
        Assert.Equal(" launcher=4210ms/63MB", StartupTrace.FormatLauncher(TimeSpan.FromMilliseconds(4210), 63L << 20));
    }

    [Fact]
    public void FormatWaits_SaysWhatWasRead_AndAPriorityOnlyWhenLowered()
    {
        Assert.Equal(
            " hardFaults=9400/52000 disk=hdd priority=belowNormal io=veryLow",
            StartupTrace.FormatWaits(new(FromDisk: 9400, All: 52000), "hdd", ("belowNormal", "veryLow")));
        Assert.Equal(" disk=ssd", StartupTrace.FormatWaits(null, "ssd", (null, null)));
        Assert.Equal("", StartupTrace.FormatWaits(null, null, (null, null)));
    }

    private static long At(double ms) => (long)Math.Round(ms * Stopwatch.Frequency / 1000);
}
