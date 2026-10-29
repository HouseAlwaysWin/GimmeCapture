using System;
using System.IO;
using System.Threading.Tasks;
using GimmeCapture.Models;
using GimmeCapture.Services.Core.AI;
using Microsoft.ML.OnnxRuntime;

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

    private readonly string _baseDir;
    private readonly AIPathService _pathService;
    private readonly OcrRuntimeService _sut;

    public OcrRuntimeServiceTests()
    {
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

    public void Dispose()
    {
        _sut.Dispose();
        try { Directory.Delete(_baseDir, recursive: true); } catch { /* best effort temp cleanup */ }
    }
}
