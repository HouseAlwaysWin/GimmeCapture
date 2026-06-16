using GimmeCapture.Models;

namespace GimmeCapture.Services.Translation;

internal sealed class TranslationPipeline
{
    public string SanitizeTranslation(string original, string translated, TranslationLanguage target) =>
        TranslationTextSanitizer.Sanitize(original, translated, target);

    public bool IsAcceptable(string original, string translated, TranslationLanguage target) =>
        TranslationFallbackPolicy.IsAcceptable(original, translated, target);

    public string PromoteBestEffort(string original, string translated, TranslationLanguage target) =>
        TranslationFallbackPolicy.PromoteBestEffort(original, translated, target);

    public string BuildFailureFallback(string original, TranslationLanguage target) =>
        TranslationFallbackPolicy.BuildFailureFallbackText(original, target);
}
