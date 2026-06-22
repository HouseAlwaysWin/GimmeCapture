using System;
using System.Diagnostics;
using System.IO;

namespace GimmeCapture.Services.Core.Infrastructure;

public static class FileLocationService
{
    public static void RevealInFileExplorer(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return;

        try
        {
            if (!File.Exists(filePath)) return;

            var psi = new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{filePath}\"",
                UseShellExecute = true
            };
            Process.Start(psi);
        }
        catch (Exception ex)
        {
            AppLog.Warning("FileLocation.Reveal", ex);
        }
    }

    /// <summary>
    /// Ensures the directory at <paramref name="path"/> exists. Returns true on success.
    /// Failures are logged (instead of being silently swallowed) and reported via the return value.
    /// </summary>
    public static bool EnsureDirectory(string path, string operation = "FileLocation.EnsureDirectory")
    {
        if (string.IsNullOrWhiteSpace(path)) return false;

        try
        {
            Directory.CreateDirectory(path);
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Warning(operation, ex);
            return false;
        }
    }
}
