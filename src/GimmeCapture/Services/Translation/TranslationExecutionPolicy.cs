using System;

namespace GimmeCapture.Services.Translation;

public sealed class TranslationExecutionPolicy : ITranslationExecutionPolicy
{
    public TimeSpan OllamaGenerateTimeout { get; } = TimeSpan.FromSeconds(60);
    public TimeSpan OllamaTagsTimeout { get; } = TimeSpan.FromSeconds(15);
}
