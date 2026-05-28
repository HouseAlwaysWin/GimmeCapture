using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using GimmeCapture.Models;
using GimmeCapture.Services.Abstractions;
using GimmeCapture.Services.Core.AI;
using GimmeCapture.Services.Core.Infrastructure;
using SkiaSharp;

namespace GimmeCapture.Services.Translation;

public sealed class TranslationSessionService : ITranslationSessionService
{
    private readonly AppSettingsService _settingsService;
    private readonly AIResourceService _aiResourceService;
    private readonly TranslationService _translationService;
    private CancellationTokenSource? _warmupCts;
    private Task? _warmupTask;

    public TranslationSessionService(
        AIResourceService aiResourceService,
        AppSettingsService settingsService,
        OcrRuntimeService ocrRuntimeService)
    {
        _aiResourceService = aiResourceService ?? throw new ArgumentNullException(nameof(aiResourceService));
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
        _translationService = new TranslationService(aiResourceService, settingsService, ocrRuntimeService);
    }

    public Task<ResourceReadyResult> CheckEngineReadyAsync(
        OCRLanguage sourceLanguage,
        TranslationLanguage targetLanguage)
    {
        ApplyLanguages(sourceLanguage, targetLanguage);
        return _translationService.CheckEngineReadyAsync();
    }

    public void StartWarmup(
        OCRLanguage sourceLanguage,
        TranslationLanguage targetLanguage)
    {
        if (_warmupTask is { IsCompleted: false })
        {
            return;
        }

        CancelWarmup();
        _warmupCts = new CancellationTokenSource();
        var token = _warmupCts.Token;
        ApplyLanguages(sourceLanguage, targetLanguage);

        _warmupTask = Task.Run(async () =>
        {
            try
            {
                await _translationService.WarmUpAsync(token);
            }
            catch (OperationCanceledException)
            {
                // Expected when leaving translation mode.
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[TranslationWarmup] Failed: {ex.Message}");
            }
        }, token);
    }

    public void CancelWarmup()
    {
        _warmupCts?.Cancel();
        _warmupCts?.Dispose();
        _warmupCts = null;
        _warmupTask = null;
        _translationService.ReleaseOcrResources();
    }

    public async Task AwaitWarmupAsync(CancellationToken ct = default)
    {
        var warmupTask = _warmupTask;
        if (warmupTask == null)
        {
            return;
        }

        try
        {
            await warmupTask.WaitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[TranslationWarmup] Await failed: {ex.Message}");
        }
    }

    public Task<bool> EnsureOcrReadyAsync(
        OCRLanguage sourceLanguage,
        CancellationToken ct = default)
    {
        var effectiveSourceLanguage = sourceLanguage == OCRLanguage.Auto
            ? OCRLanguage.TraditionalChinese
            : sourceLanguage;

        return _aiResourceService.EnsureOCRAsync(effectiveSourceLanguage, ct);
    }

    public Task<(List<TranslatedBlock> Blocks, string ErrorKey)> AnalyzeAndTranslateAsync(
        SKBitmap bitmap,
        double scale,
        OCRLanguage sourceLanguage,
        TranslationLanguage targetLanguage,
        CancellationToken ct = default)
    {
        ApplyLanguages(sourceLanguage, targetLanguage);
        return _translationService.AnalyzeAndTranslateAsync(bitmap, scale, ct);
    }

    public void Dispose()
    {
        CancelWarmup();
        _translationService.Dispose();
    }

    private void ApplyLanguages(
        OCRLanguage sourceLanguage,
        TranslationLanguage targetLanguage)
    {
        _settingsService.Settings.SourceLanguage = sourceLanguage;
        _settingsService.Settings.TargetLanguage = targetLanguage;
    }
}
