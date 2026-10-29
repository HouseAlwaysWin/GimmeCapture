using Avalonia;
using SkiaSharp;

namespace GimmeCapture.Tests;

public sealed class TranslationMemoryDiagnosticsTests
{
    [Fact]
    public void WithDebugLoggingOff_TheMemoryFiguresAreNeverRead()
    {
        // Reading them snapshots every process on the system, about a dozen times per translate press.
        Assert.False(DebugLoggingIsOn(), "nothing in the test run turns debug logging on");
        int reads = 0;

        using var bitmap = new SKBitmap(4, 3);
        TranslationMemoryDiagnostics.Log(
            () =>
            {
                reads++;
                return new TranslationMemoryDiagnostics.MemoryUse(1, 2, 3);
            },
            "translate-all-finish",
            ocrLoaded: true,
            ocrLanguage: "Japanese",
            llamaLoaded: true,
            selectionCount: 2,
            bitmap: bitmap,
            logicalBounds: new Rect(1, 2, 30, 40));

        Assert.Equal(0, reads);
    }

    [Fact]
    public void FormatLine_ReportsTheFiguresAndEveryStatePassed()
    {
        const long mb = 1024 * 1024;
        using var bitmap = new SKBitmap(4, 3);

        string line = TranslationMemoryDiagnostics.FormatLine(
            new TranslationMemoryDiagnostics.MemoryUse(512 * mb, 768 * mb, 64 * mb),
            "translation-llama-after",
            ocrLoaded: true,
            ocrLanguage: "Japanese",
            llamaLoaded: false,
            selectionCount: 2,
            bitmap: bitmap,
            logicalBounds: new Rect(1, 2, 30, 40));

        Assert.Equal(
            "[TranslationMemory] phase=translation-llama-after "
            + $"workingSet={512d:F1}MB privateBytes={768d:F1}MB managedHeap={64d:F1}MB "
            + "ocrLoaded=True ocrLanguage=Japanese llamaLoaded=False selectionCount=2 bitmap=4x3 "
            + $"bounds={30d:F0}x{40d:F0}@({1d:F0},{2d:F0})",
            line);
    }

    [Fact]
    public void FormatLine_LeavesOutWhatWasNotPassed()
    {
        string line = TranslationMemoryDiagnostics.FormatLine(new TranslationMemoryDiagnostics.MemoryUse(0, 0, 0), "app-startup");

        Assert.Equal($"[TranslationMemory] phase=app-startup workingSet={0d:F1}MB privateBytes={0d:F1}MB managedHeap={0d:F1}MB", line);
    }

    // The test AppLog itself applies before building a debug message.
    private static bool DebugLoggingIsOn()
    {
        _ = new AppLog.DebugMessageHandler(0, 0, out bool enabled);
        return enabled;
    }
}
