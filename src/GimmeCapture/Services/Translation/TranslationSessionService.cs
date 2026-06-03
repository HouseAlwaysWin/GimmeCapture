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
    private bool _ocrWarmHoldActive;

    public TranslationSessionService(
        AIResourceService aiResourceService,
        AppSettingsService settingsService,
        OcrRuntimeService ocrRuntimeService)
    {
        _aiResourceService = aiResourceService ?? throw new ArgumentNullException(nameof(aiResourceService));
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
        _translationService = new TranslationService(aiResourceService, settingsService, ocrRuntimeService);
    }

    internal bool IsOcrLoaded => _translationService.IsOcrLoaded;
    internal string? LoadedOcrLanguage => _translationService.LoadedOcrLanguage?.ToString();
    internal bool IsLlamaLoaded => _translationService.IsLlamaLoaded;

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
        _ocrWarmHoldActive = true;
        _translationService.SetOcrWarmHold(true);
        TranslationMemoryDiagnostics.Log(
            "translation-session-warmup-start",
            ocrLoaded: IsOcrLoaded,
            ocrLanguage: LoadedOcrLanguage,
            llamaLoaded: IsLlamaLoaded);

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
        _ocrWarmHoldActive = false;
        _warmupCts?.Cancel();
        _warmupCts?.Dispose();
        _warmupCts = null;
        _warmupTask = null;
        _translationService.SetOcrWarmHold(false);
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
        _translationService.SetOcrWarmHold(_ocrWarmHoldActive);
        return _translationService.AnalyzeAndTranslateAsync(bitmap, scale, ct);
    }

    public void Dispose()
    {
        CancelWarmup();
        _translationService.Dispose();
    }

    internal void ReleaseHeavyResources(bool trimProcessWorkingSet)
    {
        CancelWarmup();
        _translationService.ReleaseHeavyResources(trimProcessWorkingSet);
        TranslationMemoryDiagnostics.Log(
            "translation-session-release",
            ocrLoaded: IsOcrLoaded,
            ocrLanguage: LoadedOcrLanguage,
            llamaLoaded: IsLlamaLoaded);
    }

    private void ApplyLanguages(
        OCRLanguage sourceLanguage,
        TranslationLanguage targetLanguage)
    {
        _settingsService.Settings.SourceLanguage = sourceLanguage;
        _settingsService.Settings.TargetLanguage = targetLanguage;
    }
}
