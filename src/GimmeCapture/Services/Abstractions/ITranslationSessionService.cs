using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using GimmeCapture.Models;
using SkiaSharp;

namespace GimmeCapture.Services.Abstractions;

public interface ITranslationSessionService : IDisposable
{
    Task<ResourceReadyResult> CheckEngineReadyAsync(
        OCRLanguage sourceLanguage,
        TranslationLanguage targetLanguage);

    void StartWarmup(
        OCRLanguage sourceLanguage,
        TranslationLanguage targetLanguage);

    void CancelWarmup();

    Task AwaitWarmupAsync(CancellationToken ct = default);

    Task<bool> EnsureOcrReadyAsync(
        OCRLanguage sourceLanguage,
        CancellationToken ct = default);

    Task<(List<TranslatedBlock> Blocks, string ErrorKey)> AnalyzeAndTranslateAsync(
        SKBitmap bitmap,
        double scale,
        OCRLanguage sourceLanguage,
        TranslationLanguage targetLanguage,
        CancellationToken ct = default);
}
