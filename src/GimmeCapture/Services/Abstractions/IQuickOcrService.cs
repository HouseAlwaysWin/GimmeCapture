using System.Threading;
using System.Threading.Tasks;
using GimmeCapture.Models;
using SkiaSharp;

namespace GimmeCapture.Services.Abstractions;

public enum QuickOcrStatus
{
    Success,
    ModuleMissing,
    NoText,
    Failed
}

public sealed record QuickOcrResult(QuickOcrStatus Status, string Text = "");

public interface IQuickOcrService
{
    Task<QuickOcrResult> RecognizeAsync(
        SKBitmap bitmap,
        OCRLanguage language,
        OcrTextLayout layout,
        CancellationToken cancellationToken = default);
}
