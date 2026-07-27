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

/// <summary>
/// <paramref name="Language"/> is the language the text was actually recognised with — the probe's verdict when the
/// caller asked for <see cref="OCRLanguage.Auto"/>, so the UI can say which one it picked. Null when recognition
/// never got as far as resolving one (missing module, failure).
/// </summary>
public sealed record QuickOcrResult(QuickOcrStatus Status, string Text = "", OCRLanguage? Language = null);

public interface IQuickOcrService
{
    Task<QuickOcrResult> RecognizeAsync(
        SKBitmap bitmap,
        OCRLanguage language,
        OcrTextLayout layout,
        CancellationToken cancellationToken = default);
}
