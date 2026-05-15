using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using GimmeCapture.Models;
using GimmeCapture.Services.Abstractions;

namespace GimmeCapture.Services.Translation;

public sealed record TranslationSelectionMonitorRequest(
    IReadOnlyList<UserSelectionRect> ActiveSelections,
    PixelPoint ScreenOffset,
    double VisualScaling,
    OCRLanguage SourceLanguage,
    TranslationLanguage TargetLanguage);

public enum TranslationSelectionUpdateKind
{
    Cleared,
    Updated
}

public sealed record TranslationSelectionUpdate(
    UserSelectionRect Selection,
    TranslationSelectionUpdateKind Kind,
    string OriginalText,
    string TranslatedText,
    double InferredFontSize);

public sealed class TranslationSelectionMonitor : ITranslationSelectionMonitor
{
    private readonly IScreenCaptureService _captureService;
    private readonly ITranslationSessionService _translationSession;

    public TranslationSelectionMonitor(
        IScreenCaptureService captureService,
        ITranslationSessionService translationSession)
    {
        _captureService = captureService ?? throw new ArgumentNullException(nameof(captureService));
        _translationSession = translationSession ?? throw new ArgumentNullException(nameof(translationSession));
    }

    public async Task<IReadOnlyList<TranslationSelectionUpdate>> ProcessAsync(
        TranslationSelectionMonitorRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.ActiveSelections.Count == 0)
        {
            return Array.Empty<TranslationSelectionUpdate>();
        }

        bool ready = await _translationSession.EnsureOcrReadyAsync(request.SourceLanguage, ct);
        if (!ready)
        {
            return Array.Empty<TranslationSelectionUpdate>();
        }

        var updates = new List<TranslationSelectionUpdate>();
        foreach (var selection in request.ActiveSelections)
        {
            ct.ThrowIfCancellationRequested();

            var rect = selection.Bounds;
            if (rect.Width <= 10 || rect.Height <= 10)
            {
                continue;
            }

            using var bitmap = await _captureService.CaptureScreenAsync(
                rect,
                request.ScreenOffset,
                request.VisualScaling,
                false);

            if (!HasSignificantChange(selection, bitmap))
            {
                continue;
            }

            var (blocks, _) = await _translationSession.AnalyzeAndTranslateAsync(
                bitmap,
                request.VisualScaling,
                request.SourceLanguage,
                request.TargetLanguage,
                ct);

            if (blocks.Count == 0)
            {
                if (selection.IsTranslated
                    || !string.IsNullOrWhiteSpace(selection.TranslatedText)
                    || !string.IsNullOrWhiteSpace(selection.OriginalText))
                {
                    updates.Add(new TranslationSelectionUpdate(
                        selection,
                        TranslationSelectionUpdateKind.Cleared,
                        string.Empty,
                        string.Empty,
                        0));
                }

                continue;
            }

            var combinedTranslatedText = string.Join(
                "\n",
                blocks.AsValueEnumerable()
                    .Select(block => block.TranslatedText)
                    .Where(text => !string.IsNullOrWhiteSpace(text))
                    .ToArray());

            if (combinedTranslatedText == selection.TranslatedText)
            {
                continue;
            }

            var combinedOriginalText = string.Join(
                "\n",
                blocks.AsValueEnumerable()
                    .Select(block => block.OriginalText)
                    .Where(text => !string.IsNullOrWhiteSpace(text))
                    .ToArray());

            updates.Add(new TranslationSelectionUpdate(
                selection,
                TranslationSelectionUpdateKind.Updated,
                combinedOriginalText,
                combinedTranslatedText,
                blocks[0].InferredFontSize));
        }

        return updates;
    }

    private static bool HasSignificantChange(UserSelectionRect selection, SkiaSharp.SKBitmap bitmap)
    {
        var currentPixels = bitmap.Bytes;
        int width = bitmap.Width;
        int height = bitmap.Height;
        bool hasSignificantChange = true;

        if (selection.LastPixels != null
            && selection.LastPixelWidth == width
            && selection.LastPixelHeight == height
            && currentPixels.Length == selection.LastPixels.Length)
        {
            int diffCount = 0;
            int totalPixels = width * height;
            int step = 8;
            int sampledPixelCount = Math.Max(1, totalPixels / step);

            for (int i = 0; i < currentPixels.Length; i += step * 4)
            {
                int rDiff = currentPixels[i] - selection.LastPixels[i];
                int gDiff = currentPixels[i + 1] - selection.LastPixels[i + 1];
                int bDiff = currentPixels[i + 2] - selection.LastPixels[i + 2];

                int sad = Math.Abs(rDiff) + Math.Abs(gDiff) + Math.Abs(bDiff);
                if (sad > 45)
                {
                    diffCount++;
                }
            }

            double diffPercentage = (double)diffCount / sampledPixelCount;
            if (diffPercentage < 0.05)
            {
                hasSignificantChange = false;
            }
        }

        selection.LastPixels = currentPixels;
        selection.LastPixelWidth = width;
        selection.LastPixelHeight = height;
        return hasSignificantChange;
    }
}
