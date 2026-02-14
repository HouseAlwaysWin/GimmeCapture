using System.Threading;
using System.Threading.Tasks;
using GimmeCapture.Models;

namespace GimmeCapture.Services.Core.Interfaces;

public interface ITranslationEngine
{
    TranslationEngine EngineType { get; }
    Task<string> TranslateAsync(string text, OCRLanguage sourceLang, TranslationLanguage targetLang, CancellationToken ct = default);
}
