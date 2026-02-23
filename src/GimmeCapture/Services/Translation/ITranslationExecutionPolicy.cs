using System;

namespace GimmeCapture.Services.Translation;

public interface ITranslationExecutionPolicy
{
    TimeSpan OllamaGenerateTimeout { get; }
    TimeSpan OllamaTagsTimeout { get; }
}
