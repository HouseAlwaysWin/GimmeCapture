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
}
