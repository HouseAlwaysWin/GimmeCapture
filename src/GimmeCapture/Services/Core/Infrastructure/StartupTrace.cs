using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Threading.Tasks;
using GimmeCapture.Launcher;
using GimmeCapture.Services.Interop;

namespace GimmeCapture.Services.Core.Infrastructure;

/// <summary>
/// Stage timings from launch to the main window's first frame, logged once as <c>Startup.Timings</c>. The window's
/// first show held the UI thread for 0.8–1 s and the log never said where the time went. Each stage reports how long
/// it took after the previous one and the process's CPU time in it, as <c>wall/cpu</c> in ms: a stage that took far
/// longer than its CPU time was waiting. <c>beforeMain</c> is the runtime's own start-up, <c>log</c> the log's and
/// <c>guard</c> the single-instance check; Avalonia's is split into <c>platform</c> (text, rendering and windowing),
/// <c>reactiveUI</c>, <c>fonts</c>, <c>xaml</c> (App.axaml) and <c>afterSetup</c>. <c>settingsAppliedAt</c> (since Main)
/// is when the saved settings were applied, which runs alongside the rest. <c>hotkeysAt</c> (since Main) is when the
/// global hotkeys started working: the later of that and the hotkey service starting. A press before then is lost, not
/// delayed.
/// A launch at sign-in took 67-77 s where others took 0.5-7 s, so on Windows the line also says what a start waited on:
/// <c>systemCpu</c>, how busy the machine's processors were from Main on; <c>hardFaults</c>, the page faults that read
/// from disk since the process started, of all its page faults; <c>disk</c>, hdd or ssd for the app's drive; and
/// <c>priority</c> or <c>io</c>, only when the process runs at a lowered priority. <c>prefetch</c> is what
/// <see cref="StartupPrefetch"/> read ahead and how long it took. A fault on a file it has read still counts as a hard
/// fault, though the cache serves it in microseconds, so with it the stage times tell more than <c>hardFaults</c>.
/// <c>launcher</c>, for a start through the sign-in launcher (src/GimmeCapture.Launcher), is the time from its start to
/// this process's and what it read ahead: a start then took <c>launcher</c> + <c>beforeMain</c> + <c>hotkeysAt</c>.
/// </summary>
internal static class StartupTrace
{
    private const string FirstFrame = "firstFrame";
    private const string SettingsApplied = "settingsApplied";
    private const string HotkeysInitialized = "hotkeysInitialized";

    private static readonly object Gate = new();
    private static readonly List<Stage> Stages = new();
    private static long _mainTimestamp;
    private static TimeSpan _mainCpu;
    private static double? _beforeMainMs;
    private static StartupWaitCounters.SystemCpuTimes? _mainSystemCpu;
    private static long _settingsAppliedTimestamp;
    private static long _hotkeysInitializedTimestamp;
    private static bool _expectFirstFrame = true;
    private static string _launch = "window";
    private static string _launcher = "";
    private static bool _written;

    /// <summary>The end of a stage, and the process's CPU time by then.</summary>
    internal readonly record struct Stage(string Name, long Timestamp, TimeSpan Cpu);

    /// <summary>What the line reports, read once its last stage is in.</summary>
    internal readonly record struct Timings(
        string Launch,
        double? BeforeMainMs,
        long MainTimestamp,
        TimeSpan MainCpu,
        IReadOnlyList<Stage> Stages,
        long SettingsAppliedTimestamp,
        long HotkeysInitializedTimestamp,
        int? SystemCpuPercent);

    /// <summary>The first call in Main: the clock for every stage starts here.</summary>
    internal static void MainStarted()
    {
        _mainTimestamp = Stopwatch.GetTimestamp();
        _mainCpu = Environment.CpuUsage.TotalTime;
        _mainSystemCpu = StartupWaitCounters.ReadSystemCpuTimes();
        try
        {
            using var process = Process.GetCurrentProcess();
            _beforeMainMs = (DateTime.Now - process.StartTime).TotalMilliseconds;
            ReadLauncherHandOff(process.StartTime.ToUniversalTime());
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or System.ComponentModel.Win32Exception)
        {
            _beforeMainMs = null;
        }
    }

    // The sign-in launcher's hand-off, read once and then cleared, so that neither the elevated copy nor the updater's
    // script inherits it.
    private static void ReadLauncherHandOff(DateTime processStartedUtc)
    {
        string? handOff = Environment.GetEnvironmentVariable(ReadAhead.HandOffVariable);
        if (handOff == null)
        {
            return;
        }

        Environment.SetEnvironmentVariable(ReadAhead.HandOffVariable, null);
        if (ReadAhead.TryParseHandOff(handOff, out DateTime launcherStartedUtc, out long bytesRead))
        {
            _launcher = FormatLauncher(processStartedUtc - launcherStartedUtc, bytesRead);
        }
    }

    /// <summary>The sign-in launcher's part: from its start to the app's, and what it read meanwhile.</summary>
    internal static string FormatLauncher(TimeSpan took, long bytesRead) =>
        string.Create(CultureInfo.InvariantCulture, $" launcher={took.TotalMilliseconds:F0}ms/{bytesRead / (1024.0 * 1024.0):F0}MB");

    /// <summary>A launch to the tray has no main window, so the line is written once the settings are applied.</summary>
    internal static void LaunchedToTray()
    {
        lock (Gate)
        {
            _launch = "tray";
            _expectFirstFrame = false;
        }
    }

    internal static void Mark(string stage)
    {
        long now = Stopwatch.GetTimestamp();
        TimeSpan cpu = Environment.CpuUsage.TotalTime;
        Timings? timings;
        lock (Gate)
        {
            if (_written)
            {
                return;
            }

            if (stage == SettingsApplied)
            {
                _settingsAppliedTimestamp = now;
            }
            else if (stage == HotkeysInitialized)
            {
                _hotkeysInitializedTimestamp = now;
            }
            else
            {
                Stages.Add(new Stage(stage, now, cpu));
            }

            timings = TryCollect();
        }

        if (timings is { } complete)
        {
            // The page faults come from a snapshot of every process on the machine, 5-7 ms: not on the thread that
            // marked the last stage, usually the UI thread.
            _ = Task.Run(() =>
            {
                AppLog.Information("Startup.Timings", Format(complete) + FormatWaits(
                    StartupWaitCounters.ReadPageFaults(),
                    StartupWaitCounters.DriveKind(AppContext.BaseDirectory),
                    StartupWaitCounters.LoweredPriority()) + StartupPrefetch.Describe() + _launcher);
                StartupPrefetch.Record();
            });
        }
    }

    internal static void MarkFirstFrame() => Mark(FirstFrame);

    internal static void MarkSettingsApplied() => Mark(SettingsApplied);

    internal static void MarkHotkeysInitialized() => Mark(HotkeysInitialized);

    /// <summary>The line, but for <see cref="FormatWaits"/>.</summary>
    internal static string Format(in Timings timings)
    {
        var line = new StringBuilder("launch=").Append(timings.Launch)
            .Append(" beforeMain=").Append(timings.BeforeMainMs is { } before ? Ms(before) : "n/a")
            .Append('/').Append(Ms(timings.MainCpu.TotalMilliseconds));

        long previous = timings.MainTimestamp;
        TimeSpan previousCpu = timings.MainCpu;
        foreach (var stage in timings.Stages)
        {
            line.Append(' ').Append(stage.Name).Append('=')
                .Append(Ms(Stopwatch.GetElapsedTime(previous, stage.Timestamp).TotalMilliseconds))
                .Append('/').Append(Ms((stage.Cpu - previousCpu).TotalMilliseconds));
            previous = stage.Timestamp;
            previousCpu = stage.Cpu;
        }

        long hotkeysWork = Math.Max(timings.HotkeysInitializedTimestamp, timings.SettingsAppliedTimestamp);
        line.Append(" total=").Append(Ms(Stopwatch.GetElapsedTime(timings.MainTimestamp, previous).TotalMilliseconds)).Append("ms")
            .Append(" cpu=").Append(Ms((previousCpu - timings.MainCpu).TotalMilliseconds)).Append("ms")
            .Append(" settingsAppliedAt=")
            .Append(Ms(Stopwatch.GetElapsedTime(timings.MainTimestamp, timings.SettingsAppliedTimestamp).TotalMilliseconds)).Append("ms")
            .Append(" hotkeysAt=")
            .Append(Ms(Stopwatch.GetElapsedTime(timings.MainTimestamp, hotkeysWork).TotalMilliseconds)).Append("ms");
        if (timings.SystemCpuPercent is { } busy)
        {
            line.Append(" systemCpu=").Append(busy.ToString(CultureInfo.InvariantCulture)).Append('%');
        }

        return line.ToString();
    }

    /// <summary>
    /// The readings taken once the line is complete, each left out when unknown or, for a priority, normal.
    /// </summary>
    internal static string FormatWaits(
        StartupWaitCounters.PageFaults? faults, string? drive, (string? Cpu, string? Io) loweredPriority)
    {
        var text = new StringBuilder();
        if (faults is { } read)
        {
            text.Append(" hardFaults=").Append(read.FromDisk.ToString(CultureInfo.InvariantCulture))
                .Append('/').Append(read.All.ToString(CultureInfo.InvariantCulture));
        }

        if (drive != null)
        {
            text.Append(" disk=").Append(drive);
        }

        if (loweredPriority.Cpu != null)
        {
            text.Append(" priority=").Append(loweredPriority.Cpu);
        }

        if (loweredPriority.Io != null)
        {
            text.Append(" io=").Append(loweredPriority.Io);
        }

        return text.ToString();
    }

    // Under Gate. Null until the stages the line reports have all happened.
    private static Timings? TryCollect()
    {
        bool hasFrame = Stages.Count > 0 && Stages[^1].Name == FirstFrame;
        if (_mainTimestamp == 0 || _settingsAppliedTimestamp == 0 || _hotkeysInitializedTimestamp == 0
            || (_expectFirstFrame && !hasFrame))
        {
            return null;
        }

        _written = true;
        int? systemCpu = _mainSystemCpu is { } atMain && StartupWaitCounters.ReadSystemCpuTimes() is { } atEnd
            ? StartupWaitCounters.SystemCpuTimes.BusyPercent(atMain, atEnd)
            : null;
        return new Timings(_launch, _beforeMainMs, _mainTimestamp, _mainCpu, Stages.ToArray(),
            _settingsAppliedTimestamp, _hotkeysInitializedTimestamp, systemCpu);
    }

    private static string Ms(double ms) => ms.ToString("F0", CultureInfo.InvariantCulture);
}
