using System;
using System.Threading;

namespace GimmeCapture.Services.Translation;

public sealed class TranslationExecutionPolicy : ITranslationExecutionPolicy
{
    public TimeSpan OllamaGenerateTimeout { get; } = Timeout.InfiniteTimeSpan;
    public TimeSpan OllamaTagsTimeout { get; } = TimeSpan.FromSeconds(15);
}
