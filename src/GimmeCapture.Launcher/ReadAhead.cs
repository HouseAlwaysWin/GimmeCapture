using System;
using System.Globalization;
using System.IO;

namespace GimmeCapture.Launcher;

/// <summary>
/// What the sign-in launcher reads ahead and what it hands the app. Compiled into the launcher (.NET Framework 4.8) and
/// into the app, so both read the same names and the same hand-off.
/// </summary>
internal static class ReadAhead
{
    /// <summary>The launcher, beside the app's exe in a published build.</summary>
    internal const string LauncherFileName = "GimmeCapture.Launcher.exe";

    internal const string AppFileName = "GimmeCapture.exe";

    /// <summary>Set for the app by the launcher: when the launcher started and what it read.</summary>
    internal const string HandOffVariable = "GIMMECAPTURE_LAUNCHER";

    /// <summary>
    /// The files .NET's host and runtime read before the app's Main, in the order they are needed. At a sign-in from a
    /// hard disk they took 18-37 s to fault in, a few kilobytes at a time; read through once each, a few seconds.
    /// </summary>
    internal static readonly string[] BeforeMain =
    {
        AppFileName,
        "hostfxr.dll",
        "GimmeCapture.runtimeconfig.json",
        "GimmeCapture.deps.json",
        "hostpolicy.dll",
        "coreclr.dll",
        "System.Private.CoreLib.dll",
        "clrjit.dll",
        "GimmeCapture.dll",
        "System.Runtime.dll",
    };

    /// <summary>Reads a file through once, so its pages are in memory when they are mapped; 0 when it cannot be read.</summary>
    internal static long ReadThrough(string path, byte[] buffer)
    {
        try
        {
            using (var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.SequentialScan))
            {
                long total = 0;
                int read;
                while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                {
                    total += read;
                }

                return total;
            }
        }
        catch (IOException)
        {
            return 0;
        }
        catch (UnauthorizedAccessException)
        {
            return 0;
        }
    }

    /// <summary>
    /// A command line without its program: what the launcher passes on to the app. The program ends at its closing
    /// quote, or at the first space when it is not quoted.
    /// </summary>
    internal static string ArgumentsAfterProgram(string commandLine)
    {
        string line = commandLine.TrimStart();
        int end;
        if (line.StartsWith("\"", StringComparison.Ordinal))
        {
            int closing = line.IndexOf('"', 1);
            end = closing < 0 ? line.Length : closing + 1;
        }
        else
        {
            end = line.IndexOfAny(new[] { ' ', '\t' });
            if (end < 0)
            {
                end = line.Length;
            }
        }

        return line.Substring(end).TrimStart();
    }

    internal static string FormatHandOff(DateTime launcherStartedUtc, long bytesRead) =>
        launcherStartedUtc.Ticks.ToString(CultureInfo.InvariantCulture) + ";" + bytesRead.ToString(CultureInfo.InvariantCulture);

    internal static bool TryParseHandOff(string? value, out DateTime launcherStartedUtc, out long bytesRead)
    {
        launcherStartedUtc = default;
        bytesRead = 0;
        if (value == null)
        {
            return false;
        }

        string[] parts = value.Split(';');
        if (parts.Length != 2
            || !long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out long ticks)
            || !long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out long bytes)
            || ticks > DateTime.MaxValue.Ticks)
        {
            return false;
        }

        launcherStartedUtc = new DateTime(ticks, DateTimeKind.Utc);
        bytesRead = bytes;
        return true;
    }
}
