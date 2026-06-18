using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using GimmeCapture.Models;
using GimmeCapture.Services.Abstractions;
using SkiaSharp;

namespace GimmeCapture.Services.Core.AI;

public sealed class QuickOcrService : IQuickOcrService
{
    private readonly IQuickOcrEngineProvider _engineProvider;

    public QuickOcrService(IQuickOcrEngineProvider engineProvider)
    {
        _engineProvider = engineProvider ?? throw new ArgumentNullException(nameof(engineProvider));
    }

    public async Task<QuickOcrResult> RecognizeAsync(
        SKBitmap bitmap,
        OCRLanguage language,
        OcrTextLayout layout,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(bitmap);
        if (!_engineProvider.IsReady(language))
        {
            return new QuickOcrResult(QuickOcrStatus.ModuleMissing);
        }

        try
        {
            return await Task.Run(async () =>
            {
                using var engine = _engineProvider.Create();
                await engine.EnsureLoadedAsync(language, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();

                var boxes = engine.DetectText(bitmap);
                var fragments = new List<OcrTextFragment>(boxes.Count);

                foreach (var box in boxes.AsValueEnumerable())
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
                    ? new QuickOcrResult(QuickOcrStatus.NoText)
                    : new QuickOcrResult(QuickOcrStatus.Success, output);
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
