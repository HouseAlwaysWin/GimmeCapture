using GimmeCapture.Models;

namespace GimmeCapture.Services.Translation;

internal static class TranslationTextSanitizer
{
    public static string Sanitize(string original, string translated, TranslationLanguage target) =>
        TranslationService.SanitizeTranslationCandidateCore(original, translated, target);
}
