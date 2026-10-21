using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace GimmeCapture.Services.Core.Infrastructure;

/// <summary>
/// Stage timings from launch to the main window's first frame, logged once as <c>Startup.Timings</c>. The window's
/// first show held the UI thread for 0.8–1 s and the log never said where the time went. Each stage reports how long
/// it took after the previous one; <c>beforeMain</c> is the runtime's own start-up and <c>settingsAppliedAt</c>
/// (since Main) is when the saved settings were applied, which runs alongside the rest.
/// </summary>
internal static class StartupTrace
{
    private const string FirstFrame = "firstFrame";
    private const string SettingsApplied = "settingsApplied";

    private static readonly object Gate = new();
    private static readonly List<(string Stage, long Timestamp)> Stages = new();
    private static long _mainTimestamp;
    private static double? _beforeMainMs;
    private static long _settingsAppliedTimestamp;
    private static bool _expectFirstFrame = true;
    private static string _launch = "window";
    private static bool _written;

    /// <summary>The first call in Main: the clock for every stage starts here.</summary>
    internal static void MainStarted()
    {
        _mainTimestamp = Stopwatch.GetTimestamp();
        try
        {
            using var process = Process.GetCurrentProcess();
            _beforeMainMs = (DateTime.Now - process.StartTime).TotalMilliseconds;
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or System.ComponentModel.Win32Exception)
        {
            _beforeMainMs = null;
        }
    }

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
        string? line;
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
            else
            {
                Stages.Add((stage, now));
            }

            line = TryBuildLine();
        }

        if (line != null)
        {
            AppLog.Information("Startup.Timings", line);
        }
    }

    internal static void MarkFirstFrame() => Mark(FirstFrame);

    internal static void MarkSettingsApplied() => Mark(SettingsApplied);

    // Under Gate. Null until the stages the line reports have all happened.
    private static string? TryBuildLine()
    {
        bool hasFrame = Stages.Count > 0 && Stages[^1].Stage == FirstFrame;
        if (_mainTimestamp == 0 || _settingsAppliedTimestamp == 0 || (_expectFirstFrame && !hasFrame))
        {
            return null;
        }

        _written = true;
        var line = new StringBuilder("launch=").Append(_launch)
            .Append(" beforeMain=").Append(_beforeMainMs is { } before ? Format(before) : "n/a");

        long previous = _mainTimestamp;
        foreach (var (stage, timestamp) in Stages)
        {
            line.Append(' ').Append(stage).Append('=').Append(Format(Stopwatch.GetElapsedTime(previous, timestamp).TotalMilliseconds));
            previous = timestamp;
        }

        line.Append(" total=").Append(Format(Stopwatch.GetElapsedTime(_mainTimestamp, previous).TotalMilliseconds)).Append("ms")
            .Append(" settingsAppliedAt=")
            .Append(Format(Stopwatch.GetElapsedTime(_mainTimestamp, _settingsAppliedTimestamp).TotalMilliseconds)).Append("ms");
        return line.ToString();
    }

    private static string Format(double ms) => ms.ToString("F0", CultureInfo.InvariantCulture);
}
