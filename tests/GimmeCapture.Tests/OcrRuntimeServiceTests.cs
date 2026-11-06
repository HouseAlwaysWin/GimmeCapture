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
    private const string ModelsVariable = "AI_MODELS_DIR";
    private const long Mb = 1024 * 1024;

    private readonly ITestOutputHelper _output;
    private readonly string _baseDir;
    private readonly AIPathService _pathService;
    private readonly AIResourceService _aiResources;
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
        _aiResources = aiResourceService;

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
    public void DetectionsInput_IsOneBufferKeptWhileLoaded_AndGoesWithTheUnload()
    {
        // 10.8 MB a 1920x1080 scan: allocated for each, it went to the large object heap and brought full collections.
        float[] first;
        using (var use = _sut.BeginSessionUse())
        {
            first = use.DetectionInput(1000);
            Assert.True(first.Length >= 1000);
        }

        using (var use = _sut.BeginSessionUse())
        {
            Assert.Same(first, use.DetectionInput(1000));
            Assert.Same(first, use.DetectionInput(10));
            float[] larger = use.DetectionInput(5000);
            Assert.True(larger.Length >= 5000);
            Assert.Same(larger, use.DetectionInput(4000));
        }

        _sut.Unload();

        using (var use = _sut.BeginSessionUse())
        {
            Assert.Equal(1000, use.DetectionInput(1000).Length);
        }
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

    [Fact]
    public async Task TheIdleUnload_SavesOptimizedCopies_WhichTheNextLoadBuildsFrom_AndTheModuleTakesAlong()
    {
        // Building a session optimizes its model's graph each time: about 50 ms of each OCR load, saved once a copy of
        // the optimized graph is there to build from.
        InstallModels(OCRLanguage.TraditionalChinese);
        var paths = _pathService.GetOCRPaths(OCRLanguage.TraditionalChinese);
        using var runtime = new OcrRuntimeService(_aiResources, useOptimizedCopies: true);

        await runtime.EnsureLoadedAsync(OCRLanguage.TraditionalChinese);
        Assert.Equal(0, runtime.SessionsFromCopies);
        runtime.UnloadWhenIdle();

        Assert.False(runtime.IsDetectionLoaded);
        Assert.True(File.Exists(OnnxOptimizedModels.CopyPathFor(paths.Det, OnnxProviderConfigurator.CpuProvider)));
        Assert.True(File.Exists(OnnxOptimizedModels.CopyPathFor(paths.Rec, OnnxProviderConfigurator.CpuProvider)));

        await runtime.EnsureLoadedAsync(OCRLanguage.TraditionalChinese);
        Assert.Equal(2, runtime.SessionsFromCopies);
        Assert.True(runtime.IsLoaded);

        // Removing the module deletes its folder, the copies in it too.
        Assert.True(_aiResources.RemoveOCRResources());
        Assert.False(Directory.Exists(Path.GetDirectoryName(paths.Det)));
    }

    [Fact]
    public async Task TheDefaultRuntime_LeavesTheModelsFolderAlone()
    {
        // Tests point the plain constructor at an install's real models: it must write nothing beside them.
        InstallModels(OCRLanguage.TraditionalChinese);
        var paths = _pathService.GetOCRPaths(OCRLanguage.TraditionalChinese);

        await _sut.EnsureLoadedAsync(OCRLanguage.TraditionalChinese);
        _sut.UnloadWhenIdle();

        Assert.False(Directory.Exists(Path.Combine(Path.GetDirectoryName(paths.Det)!, OnnxOptimizedModels.FolderName)));
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
    /// Chinese OCR module (<c>AI_MODELS_DIR</c>, usually %LOCALAPPDATA%\GimmeCapture\AI), and best run on its own: it
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

    /// <summary>
    /// How long the real models take to load from the files as shipped and from their optimized copies, alternately in
    /// one process, and whether the boxes found on a 1920x1080 screen of text are the same. Opt-in, naming the AI folder
    /// of an install with the Traditional Chinese OCR module (<c>AI_MODELS_DIR</c>) and OPTIMIZED_COPY_PROBE=1; the
    /// models are copied to a folder of the test's own first, so nothing is written beside the install's.
    /// </summary>
    [OptInFact(ModelsVariable, "OPTIMIZED_COPY_PROBE=1")]
    public async Task TheRealModels_LoadFasterFromTheirOptimizedCopies_AndFindTheSameBoxes()
    {
        string source = Path.Combine(Environment.GetEnvironmentVariable(ModelsVariable)!, "ocr");
        string ocr = Path.Combine(_baseDir, "AI", "ocr");
        Directory.CreateDirectory(ocr);
        foreach (string name in new[] { "ocr_det.onnx", "ocr_rec_cht.onnx", "ocr_dict_cht.txt" })
        {
            File.Copy(Path.Combine(source, name), Path.Combine(ocr, name));
        }

        var settings = new AppSettingsService(_baseDir);
        settings.Settings.AIResourcesDirectory = Path.Combine(_baseDir, "AI");
        var aiResources = new AIResourceService(settings, new AIPathService(settings), new AIModelDownloader());
        using var shipped = new OcrRuntimeService(aiResources);
        using var copies = new OcrRuntimeService(aiResources, useOptimizedCopies: true);
        using var screen = new SKBitmap(1920, 1080);
        using (var canvas = new SKCanvas(screen))
        using (var paint = new SKPaint { Color = SKColors.Black })
        using (var font = new SKFont { Size = 18 })
        {
            canvas.Clear(SKColors.White);
            for (int row = 0; row < 24; row++)
            {
                canvas.DrawText(new string('設', 3 + row % 11) + " 定已儲存 OK " + row, 40 + (row % 2) * 900, 40 + row * 42, font, paint);
            }
        }

        // The app's first load of a run builds from the files and its idle unload saves the copies.
        await copies.EnsureLoadedAsync(OCRLanguage.TraditionalChinese);
        copies.UnloadWhenIdle();
        await shipped.EnsureLoadedAsync(OCRLanguage.TraditionalChinese);
        shipped.Unload();

        var detection = new Dictionary<bool, List<double>> { [false] = new(), [true] = new() };
        var both = new Dictionary<bool, List<double>> { [false] = new(), [true] = new() };
        var boxes = new Dictionary<bool, List<SKRectI>>();
        for (int round = 0; round < 8; round++)
        {
            foreach (bool fromCopies in round % 2 == 0 ? new[] { false, true } : new[] { true, false })
            {
                // An engine holds a lease on the runtime, which no unload goes past: each is let go before its unload.
                var runtime = fromCopies ? copies : shipped;
                var stopwatch = Stopwatch.StartNew();
                using (var engine = new PaddleOCREngine(aiResources, settings, runtime))
                {
                    await engine.EnsureDetectionLoadedAsync(OCRLanguage.TraditionalChinese);
                    detection[fromCopies].Add(stopwatch.Elapsed.TotalMilliseconds);
                    boxes[fromCopies] = engine.DetectText(screen);
                }

                runtime.Unload();
                Assert.False(runtime.IsDetectionLoaded);
                stopwatch.Restart();
                using (var engine = new PaddleOCREngine(aiResources, settings, runtime))
                {
                    await engine.EnsureLoadedAsync(OCRLanguage.TraditionalChinese);
                    both[fromCopies].Add(stopwatch.Elapsed.TotalMilliseconds);
                }

                runtime.Unload();
                Assert.False(runtime.IsDetectionLoaded);
            }
        }

        static string Median(List<double> values) => $"{values.OrderBy(v => v).ElementAt(values.Count / 2):0} ms ({values.Min():0}-{values.Max():0})";
        _output.WriteLine($"Detection loads: from the files {Median(detection[false])}, from the copies {Median(detection[true])}.");
        _output.WriteLine($"Detection and recognition: from the files {Median(both[false])}, from the copies {Median(both[true])}.");
        _output.WriteLine($"{boxes[false].Count} boxes from the files, {boxes[true].Count} from the copies; {copies.SessionsFromCopies} sessions from copies.");

        Assert.Equal(boxes[false], boxes[true]);
        Assert.NotEmpty(boxes[true]);
        Assert.Equal(8 * 3, copies.SessionsFromCopies);
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
