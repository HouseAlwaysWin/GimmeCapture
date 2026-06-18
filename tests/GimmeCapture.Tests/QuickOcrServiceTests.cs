using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using GimmeCapture.Models;
using GimmeCapture.Services.Core.AI;
using GimmeCapture.Services.Core.Interfaces;
using SkiaSharp;

namespace GimmeCapture.Tests;

public class QuickOcrServiceTests
{
    [Fact]
    public void Formatter_PreservesReadingOrderAndLines()
    {
        OcrTextFragment[] fragments =
        [
            new(new SKRectI(40, 40, 80, 60), "world"),
            new(new SKRectI(5, 5, 35, 20), "hello"),
            new(new SKRectI(40, 5, 75, 20), "there"),
            new(new SKRectI(5, 40, 35, 60), "new")
        ];

        string output = OcrTextFormatter.Format(fragments, OcrTextLayout.PreserveLines);

        Assert.Equal($"hello there{System.Environment.NewLine}new world", output);
        Assert.Equal("hello there new world", OcrTextFormatter.Format(fragments, OcrTextLayout.SingleLine));
    }

    [Fact]
    public async Task RecognizeAsync_ReturnsModuleMissingWithoutCreatingEngine()
    {
        var provider = new FakeProvider(isReady: false, new FakeEngine());
        var service = new QuickOcrService(provider);
        using var bitmap = new SKBitmap(8, 8);

        var result = await service.RecognizeAsync(
            bitmap,
            OCRLanguage.English,
            OcrTextLayout.PreserveLines);

        Assert.Equal(GimmeCapture.Services.Abstractions.QuickOcrStatus.ModuleMissing, result.Status);
        Assert.Equal(0, provider.CreateCalls);
    }

    [Fact]
    public async Task RecognizeAsync_FiltersLowConfidenceAndFormatsText()
    {
        var engine = new FakeEngine
        {
            Boxes =
            [
                new SKRectI(50, 0, 90, 20),
                new SKRectI(0, 0, 40, 20),
                new SKRectI(0, 30, 40, 50)
            ],
            Results =
            {
                [50] = ("ignored", 0.1f),
                [0] = ("first", 0.9f),
                [30] = ("second", 0.8f)
            }
        };
        var service = new QuickOcrService(new FakeProvider(isReady: true, engine));
        using var bitmap = new SKBitmap(100, 60);

        var result = await service.RecognizeAsync(
            bitmap,
            OCRLanguage.English,
            OcrTextLayout.PreserveLines);

        Assert.Equal(GimmeCapture.Services.Abstractions.QuickOcrStatus.Success, result.Status);
        Assert.Equal($"first{System.Environment.NewLine}second", result.Text);
        Assert.True(engine.IsDisposed);
    }

    private sealed class FakeProvider : IQuickOcrEngineProvider
    {
        private readonly bool _isReady;
        private readonly IOCREngine _engine;

        public FakeProvider(bool isReady, IOCREngine engine)
        {
            _isReady = isReady;
            _engine = engine;
        }

        public int CreateCalls { get; private set; }
        public bool IsReady(OCRLanguage language) => _isReady;
        public IOCREngine Create()
        {
            CreateCalls++;
            return _engine;
        }
    }

    private sealed class FakeEngine : IOCREngine
    {
        public List<SKRectI> Boxes { get; init; } = [];
        public Dictionary<int, (string Text, float Confidence)> Results { get; } = [];
        public bool IsDisposed { get; private set; }

        public Task EnsureLoadedAsync(OCRLanguage lang, CancellationToken ct = default) => Task.CompletedTask;
        public List<SKRectI> DetectText(SKBitmap bitmap) => Boxes;

        public (string text, float confidence) RecognizeText(
            SKBitmap bitmap,
            SKRectI box,
            CancellationToken ct = default) =>
            Results.TryGetValue(box.Left + box.Top, out var result) ? result : (string.Empty, 0f);

        public void Dispose() => IsDisposed = true;
    }
}
