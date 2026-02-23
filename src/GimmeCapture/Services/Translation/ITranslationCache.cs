using GimmeCapture.Models;

namespace GimmeCapture.Services.Translation;

public interface ITranslationCache
{
    bool TryGet(string key, out string value);
    void Set(string key, string value);
    string BuildKey(TranslationEngine engine, OCRLanguage sourceLang, TranslationLanguage targetLang, string text);
}
