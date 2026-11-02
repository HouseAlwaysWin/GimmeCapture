using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using GimmeCapture.Models;
using GimmeCapture.Services.Core.AI;
using GimmeCapture.Services.OCR;
using SkiaSharp;
using Xunit.Abstractions;

namespace GimmeCapture.Tests;

/// <summary>
/// What <see cref="PaddleOCREngine.RecognizeText"/> reads in real captures, and how long it takes: every box's text and
/// confidence, written to a file so that a run before a change to the recognition passes can be held against one
/// after it, box by box. That is how the passes were cut to those a confident reading leaves out
/// (<see cref="PaddleOCREngine.IsConfidentReading"/>, with the numbers). Opt-in: OCR_RECOGNITION_PROBE names a folder
/// of PNG captures (the history's own will do), AI_MODELS_DIR the AI folder of an install with the Traditional Chinese
/// OCR module, OCR_RECOGNITION_OUT the file to write.
/// </summary>
[Collection(AIGlobalUnloadCollection.Name)]
public sealed class PaddleOcrRecognitionProbeTests : IDisposable
{
    private readonly string _baseDir = Path.Combine(Path.GetTempPath(), "GimmeCapture.Tests", Guid.NewGuid().ToString("N"));
    private readonly ITestOutputHelper _output;

    public PaddleOcrRecognitionProbeTests(ITestOutputHelper output)
    {
        _output = output;
        Directory.CreateDirectory(_baseDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_baseDir))
        {
            Directory.Delete(_baseDir, recursive: true);
        }
    }

    [OptInFact("OCR_RECOGNITION_PROBE", "AI_MODELS_DIR", "OCR_RECOGNITION_OUT")]
    public async Task RealCaptures_TheirTextAndTime()
    {
        var settings = new AppSettingsService(_baseDir);
        settings.Settings.AIResourcesDirectory = Environment.GetEnvironmentVariable("AI_MODELS_DIR")!;
        var aiResources = new AIResourceService(settings, new AIPathService(settings), new AIModelDownloader());
        Assert.True(aiResources.IsOCRReady(OCRLanguage.TraditionalChinese), "No Traditional Chinese OCR module.");
        string[] captures = Directory.GetFiles(Environment.GetEnvironmentVariable("OCR_RECOGNITION_PROBE")!, "*.png")
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        Assert.NotEmpty(captures);

        using var runtime = new OcrRuntimeService(aiResources);
        using var engine = new PaddleOCREngine(aiResources, settings, runtime);
        await engine.EnsureLoadedAsync(OCRLanguage.TraditionalChinese);

        var lines = new List<object>();
        double recognitionMs = 0;
        int boxCount = 0;
        bool warm = false;
        foreach (string capture in captures)
        {
            using SKBitmap? bitmap = SKBitmap.Decode(capture);
            if (bitmap == null)
            {
                continue;
            }

            List<SKRectI> boxes = engine.DetectText(bitmap);
            if (!warm)
            {
                // The first recognition loads what the process loads once.
                foreach (SKRectI box in boxes.Take(3))
                {
                    engine.RecognizeText(bitmap, box);
                }

                warm = true;
            }

            var watch = Stopwatch.StartNew();
            foreach (SKRectI box in boxes)
            {
                var (text, confidence) = engine.RecognizeText(bitmap, box);
                lines.Add(new
                {
                    Capture = Path.GetFileName(capture),
                    Box = new[] { box.Left, box.Top, box.Right, box.Bottom },
                    Text = text,
                    Confidence = confidence
                });
            }

            recognitionMs += watch.Elapsed.TotalMilliseconds;
            boxCount += boxes.Count;
        }

        File.WriteAllText(
            Environment.GetEnvironmentVariable("OCR_RECOGNITION_OUT")!,
            JsonSerializer.Serialize(new { RecognitionMs = recognitionMs, Boxes = boxCount, Lines = lines }));
        _output.WriteLine(
            $"{captures.Length} captures, {boxCount} boxes: recognition {recognitionMs:F0} ms, " +
            $"{recognitionMs / Math.Max(1, boxCount):F1} ms a box");
    }
}
