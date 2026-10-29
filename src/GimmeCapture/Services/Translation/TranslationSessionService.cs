using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using GimmeCapture.Models;
using GimmeCapture.Services.Abstractions;
using GimmeCapture.Services.Core.AI;
using GimmeCapture.Services.Core.Infrastructure;
using GimmeCapture.Services.OCR;
using SkiaSharp;

namespace GimmeCapture.Services.Translation;

public sealed class TranslationSessionService : ITranslationSessionService
{
    private readonly IAppSettingsService _settingsService;
    private readonly AIResourceService _aiResourceService;
    private readonly TranslationService _translationService;
    private CancellationTokenSource? _warmupCts;
    private Task? _warmupTask;
    private bool _ocrWarmHoldActive;

    /// <param name="translationEngine">
    /// The app's engine (<see cref="TranslationRuntimeService"/>), shared with every other overlay: this session
    /// translates through it but neither frees its model nor disposes it.
    /// </param>
    public TranslationSessionService(
        AIResourceService aiResourceService,
        IAppSettingsService settingsService,
        OcrRuntimeService ocrRuntimeService,
        IOcrScriptDetector scriptDetector,
        ITranslationModelEngine translationEngine)
    {
        _aiResourceService = aiResourceService ?? throw new ArgumentNullException(nameof(aiResourceService));
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
        _translationService = new TranslationService(
            aiResourceService,
            settingsService,
            ocrRuntimeService,
            scriptDetector,
            translationEngine);
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
                AppLog.Warning("Translation.Warmup", ex);
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
            AppLog.Warning("Translation.WarmupAwait", ex);
        }
    }

    public Task<bool> EnsureOcrReadyAsync(
        OCRLanguage sourceLanguage,
        CancellationToken ct = default)
    {
        // Auto needs the whole probe candidate set on disk, not just one language — that is what lets it answer
        // anything other than Chinese. Explicit languages still install only what they need.
        return sourceLanguage == OCRLanguage.Auto
            ? _aiResourceService.EnsureAllOcrAsync(ct)
            : _aiResourceService.EnsureOCRAsync(sourceLanguage, ct);
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

    /// <summary>
    /// Lets go of OCR at the end of a translate press or on leaving translate mode. The translation model stays
    /// loaded: it is shared by every overlay and freed by its own idle countdown, so the next press, here or in the
    /// next overlay, uses it instead of loading it again.
    /// </summary>
    internal void ReleaseHeavyResources()
    {
        // Ending the warm-up drops its hold on OCR, which lets go of the OCR engine.
        CancelWarmup();
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
