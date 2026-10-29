using System;
using System.Diagnostics;
using Avalonia;
using SkiaSharp;

namespace GimmeCapture.Services.Translation;

internal static class TranslationMemoryDiagnostics
{
    /// <summary>The memory figures one diagnostics line reports.</summary>
    internal readonly record struct MemoryUse(long WorkingSet, long PrivateBytes, long ManagedHeap);

    /// <summary>
    /// Writes the process's memory use at <paramref name="phase"/> to the debug log, and does nothing at all unless
    /// debug logging is on (<see cref="AppLog.DebugLogVariable"/>). Reading the figures takes a snapshot of every
    /// process on the system, and translation calls this about a dozen times per press, several of them on the UI
    /// thread.
    /// </summary>
    public static void Log(
        string phase,
        bool? ocrLoaded = null,
        string? ocrLanguage = null,
        bool? llamaLoaded = null,
        int? selectionCount = null,
        SKBitmap? bitmap = null,
        Rect? logicalBounds = null)
    {
        Log(ReadMemoryUse, phase, ocrLoaded, ocrLanguage, llamaLoaded, selectionCount, bitmap, logicalBounds);
    }

    /// <summary>
    /// <see cref="Log(string, bool?, string?, bool?, int?, SKBitmap?, Rect?)"/> with the figures read by
    /// <paramref name="readMemoryUse"/>, so a test can see whether they were read.
    /// </summary>
    internal static void Log(
        Func<MemoryUse> readMemoryUse,
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
            // The whole line is the one hole of AppLog's interpolated message, which evaluates nothing while debug
            // logging is off. Handed over as a finished string instead, it was built, process snapshot and all, on
            // every call and then thrown away.
            AppLog.Debug(
                "TranslationMemoryDiagnostics",
                $"{FormatLine(readMemoryUse(), phase, ocrLoaded, ocrLanguage, llamaLoaded, selectionCount, bitmap, logicalBounds)}");
        }
        catch
        {
            // Diagnostics only.
        }
    }

    internal static string FormatLine(
        MemoryUse memory,
        string phase,
        bool? ocrLoaded = null,
        string? ocrLanguage = null,
        bool? llamaLoaded = null,
        int? selectionCount = null,
        SKBitmap? bitmap = null,
        Rect? logicalBounds = null)
    {
        string message =
            $"[TranslationMemory] phase={phase} " +
            $"workingSet={FormatBytes(memory.WorkingSet)} " +
            $"privateBytes={FormatBytes(memory.PrivateBytes)} " +
            $"managedHeap={FormatBytes(memory.ManagedHeap)}";

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

        return message;
    }

    private static MemoryUse ReadMemoryUse()
    {
        using var process = Process.GetCurrentProcess();
        return new MemoryUse(process.WorkingSet64, process.PrivateMemorySize64, GC.GetTotalMemory(false));
    }

    private static string FormatBytes(long bytes)
    {
        const double mb = 1024d * 1024d;
        return $"{bytes / mb:F1}MB";
    }
}
