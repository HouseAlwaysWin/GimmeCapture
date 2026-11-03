using System;
using System.Diagnostics;
using System.IO;

namespace GimmeCapture.Launcher;

/// <summary>
/// What the HKCU Run key starts at sign-in, in the app's place. Before the app's Main ran, .NET's host and runtime took
/// 18-37 s at sign-in to fault their files in from the hard disk the app was installed on, one small random read at a
/// time while other programs read the same disk. This program runs on .NET Framework 4.8, which Windows keeps on its own
/// drive, so it starts at once; it reads those files through in one pass each and then starts the app with its own
/// arguments, whose start then finds them in memory. Whatever goes wrong with the reading, the app is still started.
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
            }

            var buffer = new byte[1 << 20];
            foreach (string name in ReadAhead.BeforeMain)
            {
                bytesRead += ReadAhead.ReadThrough(Path.Combine(folder, name), buffer);
            }
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
            using (Process.Start(start))
            {
            }

            return 0;
        }
        catch (Exception)
        {
            return 1;
        }
    }
}
