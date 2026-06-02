using System;
using System.Diagnostics;
using System.IO;

namespace GimmeCapture.Services.Core.Infrastructure;

internal static class RuntimePathProvider
{
    public static string GetExecutablePath()
    {
        return Environment.ProcessPath
               ?? Process.GetCurrentProcess().MainModule?.FileName
               ?? AppContext.BaseDirectory
               ?? AppDomain.CurrentDomain.BaseDirectory;
    }

    public static string GetExecutableDirectory()
    {
        var executablePath = GetExecutablePath();
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return AppContext.BaseDirectory;
        }

        var directory = Path.GetDirectoryName(executablePath);
        return string.IsNullOrWhiteSpace(directory)
            ? AppContext.BaseDirectory
            : directory;
    }
}
