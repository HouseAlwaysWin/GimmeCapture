using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using GimmeCapture.Models;
using GimmeCapture.Services.Core.AI;
using GimmeCapture.Services.OCR;
using Microsoft.ML.OnnxRuntime;
using SkiaSharp;
using Xunit.Abstractions;

namespace GimmeCapture.Tests;

/// <summary>
/// Session lifetime rules for the shared OCR runtime. Most do not need models loaded — the point is the gating around
/// the sessions, which applies whether or not any are present. What gets loaded when uses tiny real models
/// (<see cref="TinyOnnxModel"/>) installed where the app keeps OCR's.
/// </summary>
[Collection(AIGlobalUnloadCollection.Name)]
public sealed class OcrRuntimeServiceTests : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(150);
    private const string ModelsVariable = "OCR_MODELS_DIR";
    private const long Mb = 1024 * 1024;

    private readonly ITestOutputHelper _output;
    private readonly string _baseDir;
    private readonly AIPathService _pathService;
    private readonly OcrRuntimeService _sut;

    public OcrRuntimeServiceTests(ITestOutputHelper output)
    {
        _output = output;
        _baseDir = Path.Combine(Path.GetTempPath(), "GimmeCapture.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_baseDir);

        var settingsService = new AppSettingsService(_baseDir);
        settingsService.Settings.AIResourcesDirectory = Path.Combine(_baseDir, "AI");
        _pathService = new AIPathService(settingsService);
        var aiResourceService = new AIResourceService(
            settingsService,
            _pathService,
            new AIModelDownloader());

        _sut = new OcrRuntimeService(aiResourceService);
    }

    [Fact]
    public async Task DetectionAlone_LeavesTheRecognizerForLater()
    {
        // The overlay's scan only finds boxes, and building the recognizer as well took 135 ms of every cold load.
        InstallModels(OCRLanguage.TraditionalChinese);

        await _sut.EnsureDetectionLoadedAsync(OCRLanguage.TraditionalChinese);

        Assert.True(_sut.IsDetectionLoaded);
        Assert.False(_sut.IsLoaded);
        var (detection, recognition) = Sessions();
        Assert.NotNull(detection);
        Assert.Null(recognition);
    }

    [Fact]
    public async Task TheRecognizer_JoinsTheLoadedDetector_WithoutRebuildingIt()
    {
        InstallModels(OCRLanguage.TraditionalChinese);
        await _sut.EnsureDetectionLoadedAsync(OCRLanguage.TraditionalChinese);
        var (detection, _) = Sessions();

        await _sut.EnsureLoadedAsync(OCRLanguage.TraditionalChinese);

        using var use = _sut.BeginSessionUse();
        Assert.Same(detection, use.Detection);
        Assert.NotNull(use.Recognition);
        Assert.Equal(["", "a", "b", "TraditionalChinese"], use.Dictionary);
        Assert.Equal(OCRLanguage.TraditionalChinese, _sut.LoadedLanguage);
    }

    [Fact]
    public async Task ALanguageChange_ReplacesOnlyTheRecognizer()
    {
        // Every language shares the detection model, and the auto-detect changes language once per installed one.
        InstallModels(OCRLanguage.TraditionalChinese, OCRLanguage.Japanese);
        await _sut.EnsureLoadedAsync(OCRLanguage.TraditionalChinese);
        var (detection, recognition) = Sessions();

        await _sut.EnsureLoadedAsync(OCRLanguage.Japanese);

        using var use = _sut.BeginSessionUse();
        Assert.Same(detection, use.Detection);
        Assert.NotNull(use.Recognition);
        Assert.NotSame(recognition, use.Recognition);
        Assert.Equal(OCRLanguage.Japanese, _sut.LoadedLanguage);
        Assert.Equal("Japanese", use.Dictionary[^1]);
    }

    // Real, tiny models where the app keeps OCR's: the shared detector, and a recognizer and dictionary per language.
    private void InstallModels(params OCRLanguage[] languages)
    {
        foreach (var language in languages)
        {
            var paths = _pathService.GetOCRPaths(language);
            TinyOnnxModel.WriteIdentity(paths.Det);
            TinyOnnxModel.WriteIdentity(paths.Rec);
            File.WriteAllLines(paths.Dict, ["a", "b", language.ToString()]);
        }
    }

    private (InferenceSession? Detection, InferenceSession? Recognition) Sessions()
    {
        using var use = _sut.BeginSessionUse();
        return (use.Detection, use.Recognition);
    }

    [Fact]
    public async Task BeginSessionUse_SerialisesInference()
    {
        // Two threads calling Run on one ONNX session killed the process with an access violation. The app
        // produces overlapping callers routinely: DetectText cannot be cancelled once started, so cancelling a
        // scan (Esc) leaves its inference running while the replacement scan begins.
        var first = _sut.BeginSessionUse();

        var second = OwnThread.Run(() =>
        {
            using var use = _sut.BeginSessionUse();
            return true;
        });

        var finished = await Task.WhenAny(second, OwnThread.Delay(Settle));
        Assert.False(finished == second, "a second inference started while the first was still running");

        first.Dispose();

        Assert.True(await second.WaitAsync(Timeout));
    }

    [Fact]
    public async Task DisposingASessionUseTwice_DoesNotAdmitTwoInferences()
    {
        var use = _sut.BeginSessionUse();
        use.Dispose();
        use.Dispose();

        // A double release would have raised the semaphore's count, letting two callers in at once from then on.
        var first = _sut.BeginSessionUse();
        var second = OwnThread.Run(() =>
        {
            using var scope = _sut.BeginSessionUse();
            return true;
        });

        var finished = await Task.WhenAny(second, OwnThread.Delay(Settle));
        Assert.False(finished == second);

        first.Dispose();
        Assert.True(await second.WaitAsync(Timeout));
    }

    [Fact]
    public void BeginSessionUse_WithNothingLoaded_ReportsNoSessions()
    {
        using var use = _sut.BeginSessionUse();

        Assert.Null(use.Detection);
        Assert.Null(use.Recognition);
        Assert.Empty(use.Dictionary);
    }

    [Fact]
    public void ReleasingTheLastLease_DoesNotUnloadImmediately()
    {
        // The unload is deferred so a follow-up capture reuses the live sessions instead of forcing a
        // teardown-and-rebuild, which is the sequence that preceded every observed crash.
        var lease = _sut.AcquireLease();
        Assert.True(_sut.HasActiveLeases);

        _sut.ReleaseLease(lease);

        Assert.False(_sut.HasActiveLeases);
    }

    /// <summary>
    /// How much of the process the real OCR models keep: loaded for an overlay's scan, loaded to read the text as well,
    /// and once unloaded. On DirectML that was 500, 550 and, with the device held for the next scan, 530 MB, and about
    /// 270 MB stayed once the device closed. On the CPU, with ONNX Runtime keeping what each detection allocated, 338,
    /// 350 and 52 MB; now about 40, 50 and 6-12. Opt-in, naming the AI folder of an install with the Traditional
    /// Chinese OCR module (<c>OCR_MODELS_DIR</c>, usually %LOCALAPPDATA%\GimmeCapture\AI), and best run on its own: it
    /// reads the private bytes of the whole test process.
    /// </summary>
    [OptInFact(ModelsVariable)]
    public async Task TheRealModels_KeepLittleOfTheProcess_LoadedOrNot()
    {
        string folder = Environment.GetEnvironmentVariable(ModelsVariable)!;
        var settings = new AppSettingsService(_baseDir);
        settings.Settings.AIResourcesDirectory = folder;
        var aiResources = new AIResourceService(settings, new AIPathService(settings), new AIModelDownloader());
        Assert.True(
            aiResources.IsOCRReady(OCRLanguage.TraditionalChinese), $"No Traditional Chinese OCR module in {folder}.");
        using var screen = TwoScreensOfText();
        long baseline = PrivateBytes();

        using var runtime = new OcrRuntimeService(aiResources);
        List<SKRectI> boxes;
        using (var engine = new PaddleOCREngine(aiResources, settings, runtime))
        {
            await engine.EnsureDetectionLoadedAsync(OCRLanguage.TraditionalChinese);
            engine.DetectText(screen);
            boxes = engine.DetectText(screen);
        }

        long detection = PrivateBytes() - baseline;
        using (var engine = new PaddleOCREngine(aiResources, settings, runtime))
        {
            await engine.EnsureLoadedAsync(OCRLanguage.TraditionalChinese);
            foreach (var box in boxes)
            {
                engine.RecognizeText(screen, box);
            }
        }

        long both = PrivateBytes() - baseline;
        runtime.Unload();
        long unloaded = PrivateBytes() - baseline;
        _output.WriteLine(
            $"{boxes.Count} lines found. Over the baseline: {detection / Mb} MB with detection loaded, " +
            $"{both / Mb} MB with recognition too, {unloaded / Mb} MB unloaded.");

        Assert.NotEmpty(boxes);
        Assert.InRange(detection, long.MinValue, 100 * Mb);
        Assert.InRange(both, long.MinValue, 120 * Mb);
        Assert.InRange(unloaded, long.MinValue, 40 * Mb);
    }

    // Two screens side by side, 3968x1160, with 30 lines of UI text of different lengths.
    private static SKBitmap TwoScreensOfText()
    {
        var screen = new SKBitmap(3968, 1160);
        using var canvas = new SKCanvas(screen);
        using var paint = new SKPaint { Color = SKColors.Black };
        using var font = new SKFont { Size = 20 };
        canvas.Clear(SKColors.White);
        for (int row = 0; row < 30; row++)
        {
            string text = new string('設', 3 + row % 17) + " 定已儲存 OK " + row;
            canvas.DrawText(text, 40 + (row % 3) * 1300, 40 + row * 36, font, paint);
        }

        return screen;
    }

    private static long PrivateBytes()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        using var process = Process.GetCurrentProcess();
        return process.PrivateMemorySize64;
    }

    public void Dispose()
    {
        _sut.Dispose();
        try { Directory.Delete(_baseDir, recursive: true); } catch { /* best effort temp cleanup */ }
    }
}
