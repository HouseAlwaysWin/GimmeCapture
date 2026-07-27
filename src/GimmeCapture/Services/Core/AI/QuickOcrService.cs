using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using GimmeCapture.Models;
using GimmeCapture.Services.Abstractions;
using GimmeCapture.Services.OCR;
using SkiaSharp;

namespace GimmeCapture.Services.Core.AI;

public sealed class QuickOcrService : IQuickOcrService
{
    private readonly IQuickOcrEngineProvider _engineProvider;
    private readonly IOcrScriptDetector _scriptDetector;

    public QuickOcrService(IQuickOcrEngineProvider engineProvider, IOcrScriptDetector scriptDetector)
    {
        _engineProvider = engineProvider ?? throw new ArgumentNullException(nameof(engineProvider));
        _scriptDetector = scriptDetector ?? throw new ArgumentNullException(nameof(scriptDetector));
    }

    public async Task<QuickOcrResult> RecognizeAsync(
        SKBitmap bitmap,
        OCRLanguage language,
        OcrTextLayout layout,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(bitmap);

        // Auto has no single model to check for: it needs at least one probe candidate installed, and picks the
        // language from the capture itself. Anything else must have exactly that language's model on disk.
        bool auto = language == OCRLanguage.Auto;
        bool ready = auto ? _scriptDetector.HasInstalledCandidate : _engineProvider.IsReady(language);
        if (!ready)
        {
            return new QuickOcrResult(QuickOcrStatus.ModuleMissing);
        }

        try
        {
            return await Task.Run(async () =>
            {
                using var engine = _engineProvider.Create();

                OCRLanguage resolvedLanguage;
                IReadOnlyList<SKRectI> boxes;
                if (auto)
                {
                    var detection = await _scriptDetector
                        .DetectAsync(bitmap, engine, cancellationToken)
                        .ConfigureAwait(false);
                    resolvedLanguage = detection.Language;
                    boxes = detection.Boxes;
                }
                else
                {
                    resolvedLanguage = language;
                    await engine.EnsureLoadedAsync(resolvedLanguage, cancellationToken).ConfigureAwait(false);
                    boxes = engine.DetectText(bitmap);
                }

                cancellationToken.ThrowIfCancellationRequested();

                var fragments = new List<OcrTextFragment>(boxes.Count);
                foreach (var box in boxes)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var (text, confidence) = engine.RecognizeText(bitmap, box, cancellationToken);
                    if (confidence >= 0.3f && !string.IsNullOrWhiteSpace(text))
                    {
                        fragments.Add(new OcrTextFragment(box, text));
                    }
                }

                string output = OcrTextFormatter.Format(fragments, layout);
                return string.IsNullOrWhiteSpace(output)
                    ? new QuickOcrResult(QuickOcrStatus.NoText, string.Empty, resolvedLanguage)
                    : new QuickOcrResult(QuickOcrStatus.Success, output, resolvedLanguage);
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return new QuickOcrResult(QuickOcrStatus.Failed);
        }
    }
}
