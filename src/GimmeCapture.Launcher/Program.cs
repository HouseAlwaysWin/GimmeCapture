using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace GimmeCapture.Launcher;

/// <summary>
/// What the HKCU Run key starts at sign-in, in the app's place. Before the app's Main ran, .NET's host and runtime took
/// 18-37 s at sign-in to fault their files in from the hard disk the app was installed on, one small random read at a
/// time while other programs read the same disk. This program runs on .NET Framework 4.8, which Windows keeps on its own
/// drive, so it starts at once. It reads those files through in one pass each, then the rest of the files the app's last
/// start mapped, as the app wrote them down, several at a time (<see cref="ReadAhead.ReadThreads"/>), and then starts the
/// app with its own arguments, whose start finds them in memory. Both run above normal priority
/// (<see cref="ReadAhead.StartPriority"/>) until the app's start is done. Whatever goes wrong with the reading, the app is
/// still started.
/// </summary>
internal static class Program
{
    [STAThread]
    private static int Main()
    {
        string folder = AppDomain.CurrentDomain.BaseDirectory;
        DateTime startedUtc = DateTime.UtcNow;
        long bytesRead = 0;
        try
        {
            using (var self = Process.GetCurrentProcess())
            {
                startedUtc = self.StartTime.ToUniversalTime();
                // Ahead of the sign-in's other programs, so that this one is not the one waiting for a processor
                // when it raises the app's priority below.
                self.PriorityClass = ReadAhead.StartPriority;
            }

            bytesRead = ReadAhead.ReadAll(ReadAhead.Plan(StartupList(folder), folder), ReadAhead.ReadThreads);
        }
        catch (Exception)
        {
            // The app's start matters more than the read-ahead.
        }

        var start = new ProcessStartInfo(
            Path.Combine(folder, ReadAhead.AppFileName), ReadAhead.ArgumentsAfterProgram(Environment.CommandLine))
        {
            UseShellExecute = false,
            WorkingDirectory = folder,
        };
        start.EnvironmentVariables[ReadAhead.HandOffVariable] = ReadAhead.FormatHandOff(startedUtc, bytesRead);
        try
        {
            using (var app = Process.Start(start))
            {
                try
                {
                    // Not inherited: Windows starts a child at normal priority. The app lowers itself back once its
                    // start is done (StartupPriority).
                    if (app != null)
                    {
                        app.PriorityClass = ReadAhead.StartPriority;
                    }
                }
                catch (Exception)
                {
                    // Started is what matters.
                }
            }

            return 0;
        }
        catch (Exception)
        {
            return 1;
        }
    }

    // What the app's last start mapped from its folder, which the app writes down in its version's data folder; nothing
    // before a version's first start. The version comes from the app's assembly, a few pages of it.
    private static IEnumerable<string> StartupList(string folder)
    {
        string? version = ReadAhead.AppVersion(
            FileVersionInfo.GetVersionInfo(Path.Combine(folder, ReadAhead.AppAssemblyFileName)).ProductVersion);
        if (version == null)
        {
            return Array.Empty<string>();
        }

        string list = ReadAhead.StartupListPath(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GimmeCapture"),
            folder,
            version);
        return File.Exists(list) ? File.ReadAllLines(list) : Array.Empty<string>();
    }
}
