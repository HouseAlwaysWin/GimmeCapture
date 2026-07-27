using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using GimmeCapture.Models;
using GimmeCapture.Services.Core.AI;
using GimmeCapture.Services.Core.Interfaces;
using GimmeCapture.Services.OCR;
using SkiaSharp;

namespace GimmeCapture.Tests;

/// <summary>
/// Drives the probe with a fake engine and real (empty) model files on disk, so the orchestration is testable
/// without ONNX: which candidates get probed, that detection runs once, that the winner is left loaded, and that
/// the verdict is cached for the session.
/// </summary>
public sealed class OcrScriptDetectorTests : IDisposable
{
    private const string Kana = "こんにちは";
    private const string ChineseText = "測試文字";

    private readonly string _baseDir;
    private readonly AIResourceService _aiResourceService;
    private readonly AIPathService _pathService;

    public OcrScriptDetectorTests()
    {
        _baseDir = Path.Combine(Path.GetTempPath(), "GimmeCapture.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_baseDir);

        var settingsService = new AppSettingsService(_baseDir);
        settingsService.Settings.AIResourcesDirectory = Path.Combine(_baseDir, "AI");
        _pathService = new AIPathService(settingsService);
        _aiResourceService = new AIResourceService(
            settingsService,
            _pathService,
            new NativeResolverService(_pathService),
            new AIModelDownloader());
    }

    [Fact]
    public async Task DetectAsync_ProbesEveryInstalledCandidate_AndLeavesTheWinnerLoaded()
    {
        Install(OCRLanguage.TraditionalChinese, OCRLanguage.Japanese);
        var engine = new FakeEngine
        {
            Boxes = [Box(0), Box(1)],
            TextByLanguage =
            {
                // What actually happens on Japanese input: the Chinese model cannot emit kana, so it returns
                // mangled kanji, while the Japanese model reads it cleanly.
                [OCRLanguage.TraditionalChinese] = (ChineseText, 0.45f),
                [OCRLanguage.Japanese] = (Kana, 0.9f)
            }
        };
        var detector = new OcrScriptDetector(_aiResourceService);
        using var bitmap = new SKBitmap(64, 32);

        var result = await detector.DetectAsync(bitmap, engine);

        Assert.Equal(OCRLanguage.Japanese, result.Language);
        Assert.True(result.Probed);
        Assert.Equal(OCRLanguage.Japanese, engine.LoadedLanguage);
        Assert.Equal([OCRLanguage.TraditionalChinese, OCRLanguage.Japanese], engine.ProbedLanguages);
        // Detection is language-independent, so running it per candidate would be pure waste.
        Assert.Equal(1, engine.DetectCalls);
    }

    [Fact]
    public async Task DetectAsync_ReturnsTheWinnersSamplesAlignedToTheBoxes()
    {
        Install(OCRLanguage.TraditionalChinese, OCRLanguage.Japanese);
        var engine = new FakeEngine
        {
            Boxes = [Box(0), Box(1)],
            TextByLanguage =
            {
                [OCRLanguage.TraditionalChinese] = (ChineseText, 0.4f),
                [OCRLanguage.Japanese] = (Kana, 0.9f)
            }
        };
        var detector = new OcrScriptDetector(_aiResourceService);
        using var bitmap = new SKBitmap(64, 32);

        var result = await detector.DetectAsync(bitmap, engine);

        // The caller re-uses these instead of recognising Boxes[0..N) a second time with the same model.
        Assert.Equal(2, result.SampledRecognitions.Count);
        Assert.All(result.SampledRecognitions, sample => Assert.Equal(Kana, sample.Text));
    }

    [Fact]
    public async Task DetectAsync_CachesTheVerdictForTheSession()
    {
        Install(OCRLanguage.TraditionalChinese, OCRLanguage.Japanese);
        var engine = new FakeEngine
        {
            Boxes = [Box(0)],
            TextByLanguage =
            {
                [OCRLanguage.TraditionalChinese] = (ChineseText, 0.4f),
                [OCRLanguage.Japanese] = (Kana, 0.9f)
            }
        };
        var detector = new OcrScriptDetector(_aiResourceService);
        using var bitmap = new SKBitmap(64, 32);

        await detector.DetectAsync(bitmap, engine);
        int recognitionsAfterFirst = engine.RecognizeCalls;
        var second = await detector.DetectAsync(bitmap, engine);

        Assert.Equal(OCRLanguage.Japanese, second.Language);
        Assert.False(second.Probed);
        // Swapping recogniser models rebuilds ONNX sessions, so a second capture must not pay for the probe again.
        Assert.Equal(recognitionsAfterFirst, engine.RecognizeCalls);
        Assert.Equal(2, engine.DetectCalls);
    }

    [Fact]
    public async Task DetectAsync_WithOneInstalledLanguage_SkipsProbingEntirely()
    {
        Install(OCRLanguage.TraditionalChinese);
        var engine = new FakeEngine { Boxes = [Box(0)] };
        var detector = new OcrScriptDetector(_aiResourceService);
        using var bitmap = new SKBitmap(64, 32);

        var result = await detector.DetectAsync(bitmap, engine);

        Assert.Equal(OCRLanguage.TraditionalChinese, result.Language);
        Assert.False(result.Probed);
        Assert.Equal(0, engine.RecognizeCalls);
    }

    [Fact]
    public async Task DetectAsync_WithNothingInstalled_ReportsNoCandidate()
    {
        var detector = new OcrScriptDetector(_aiResourceService);
        var engine = new FakeEngine();
        using var bitmap = new SKBitmap(64, 32);

        Assert.False(detector.HasInstalledCandidate);

        var result = await detector.DetectAsync(bitmap, engine);

        Assert.Empty(result.Boxes);
        Assert.False(result.Probed);
        Assert.Null(engine.LoadedLanguage);
    }

    [Fact]
    public async Task DetectAsync_WhenNoTextWasDetected_DoesNotCacheAVerdict()
    {
        Install(OCRLanguage.TraditionalChinese, OCRLanguage.Japanese);
        var engine = new FakeEngine
        {
            Boxes = [],
            TextByLanguage =
            {
                [OCRLanguage.TraditionalChinese] = (ChineseText, 0.4f),
                [OCRLanguage.Japanese] = (Kana, 0.9f)
            }
        };
        var detector = new OcrScriptDetector(_aiResourceService);
        using var bitmap = new SKBitmap(64, 32);

        var empty = await detector.DetectAsync(bitmap, engine);
        Assert.False(empty.Probed);

        // A blank first capture must not lock the session into the fallback language.
        engine.Boxes = [Box(0)];
        var second = await detector.DetectAsync(bitmap, engine);

        Assert.True(second.Probed);
        Assert.Equal(OCRLanguage.Japanese, second.Language);
    }

    private void Install(params OCRLanguage[] languages)
    {
        foreach (var language in languages)
        {
            var paths = _pathService.GetOCRPaths(language);
            Directory.CreateDirectory(Path.GetDirectoryName(paths.Det)!);
            File.WriteAllText(paths.Det, "det");
            File.WriteAllText(paths.Rec, "rec");
            File.WriteAllText(paths.Dict, "dict");
        }
    }

    private static SKRectI Box(int index) => new(0, index * 20, 40, (index * 20) + 18);

    public void Dispose()
    {
        try { Directory.Delete(_baseDir, recursive: true); } catch { /* best effort temp cleanup */ }
    }

    private sealed class FakeEngine : IOCREngine
    {
        public List<SKRectI> Boxes { get; set; } = [];
        public Dictionary<OCRLanguage, (string Text, float Confidence)> TextByLanguage { get; } = [];
        public List<OCRLanguage> ProbedLanguages { get; } = [];
        public OCRLanguage? LoadedLanguage { get; private set; }
        public int DetectCalls { get; private set; }
        public int RecognizeCalls { get; private set; }

        public Task EnsureLoadedAsync(OCRLanguage lang, CancellationToken ct = default)
        {
            if (LoadedLanguage != lang)
            {
                ProbedLanguages.Add(lang);
            }

            LoadedLanguage = lang;
            return Task.CompletedTask;
        }

        public List<SKRectI> DetectText(SKBitmap bitmap)
        {
            DetectCalls++;
            return Boxes;
        }

        public (string text, float confidence) RecognizeText(SKBitmap bitmap, SKRectI box, CancellationToken ct = default)
        {
            RecognizeCalls++;
            return LoadedLanguage is { } language && TextByLanguage.TryGetValue(language, out var result)
                ? result
                : (string.Empty, 0f);
        }

        public void Dispose() { }
    }
}
