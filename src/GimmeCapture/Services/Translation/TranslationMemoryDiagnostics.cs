using System;
using System.Diagnostics;
using Avalonia;
using SkiaSharp;

namespace GimmeCapture.Services.Translation;

internal static class TranslationMemoryDiagnostics
{
    public static void Log(
        string phase,
        bool? ocrLoaded = null,
        string? ocrLanguage = null,
        bool? llamaLoaded = null,
        int? selectionCount = null,
        SKBitmap? bitmap = null,
        Rect? logicalBounds = null)
    {
        try
        {
            using var process = Process.GetCurrentProcess();
            string message =
                $"[TranslationMemory] phase={phase} " +
                $"workingSet={FormatBytes(process.WorkingSet64)} " +
                $"privateBytes={FormatBytes(process.PrivateMemorySize64)} " +
                $"managedHeap={FormatBytes(GC.GetTotalMemory(false))}";

            if (ocrLoaded.HasValue)
            {
                message += $" ocrLoaded={ocrLoaded.Value}";
            }

            if (!string.IsNullOrWhiteSpace(ocrLanguage))
            {
                message += $" ocrLanguage={ocrLanguage}";
            }

            if (llamaLoaded.HasValue)
            {
                message += $" llamaLoaded={llamaLoaded.Value}";
            }

            if (selectionCount.HasValue)
            {
                message += $" selectionCount={selectionCount.Value}";
            }

            if (bitmap != null)
            {
                message += $" bitmap={bitmap.Width}x{bitmap.Height}";
            }

            if (logicalBounds.HasValue)
            {
                var bounds = logicalBounds.Value;
                message += $" bounds={bounds.Width:F0}x{bounds.Height:F0}@({bounds.X:F0},{bounds.Y:F0})";
            }

            Debug.WriteLine(message);
        }
        catch
        {
            // Diagnostics only.
        }
    }

    private static string FormatBytes(long bytes)
    {
        const double mb = 1024d * 1024d;
        return $"{bytes / mb:F1}MB";
    }
}
