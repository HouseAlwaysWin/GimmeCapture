using System.Threading;
using System.Threading.Tasks;
using GimmeCapture.Models;
using GimmeCapture.Services.Core.Interfaces;
using GimmeCapture.Services.Translation;

namespace GimmeCapture.Tests;

public class TranslationEngineRunnerTests
{
    [Fact]
    public async Task TranslateSelectedAsync_UsesRequestedEngineAndNormalizesNull()
    {
        var engine = new FakeEngine(null);
        var runner = new TranslationEngineRunner([engine]);

        string result = await runner.TranslateSelectedAsync(
            "hello",
            OCRLanguage.English,
            TranslationLanguage.Japanese,
            TranslationEngine.LlamaSharp,
            CancellationToken.None);

        Assert.Equal(string.Empty, result);
        Assert.Equal(1, engine.Calls);
    }

    [Fact]
    public async Task TranslateWithLlamaAsync_ForwardsLanguagesAndText()
    {
        var engine = new FakeEngine("\u3053\u3093\u306b\u3061\u306f");
        var runner = new TranslationEngineRunner([engine]);

        string result = await runner.TranslateWithLlamaAsync(
            "hello",
            OCRLanguage.English,
            TranslationLanguage.Japanese,
            CancellationToken.None);

        Assert.Equal("\u3053\u3093\u306b\u3061\u306f", result);
        Assert.Equal("hello", engine.LastText);
        Assert.Equal(OCRLanguage.English, engine.LastSource);
        Assert.Equal(TranslationLanguage.Japanese, engine.LastTarget);
    }

    private sealed class FakeEngine : ITranslationEngine
    {
        private readonly string? _result;

        public FakeEngine(string? result)
        {
            _result = result;
        }

        public TranslationEngine EngineType => TranslationEngine.LlamaSharp;
        public int Calls { get; private set; }
        public string LastText { get; private set; } = string.Empty;
        public OCRLanguage LastSource { get; private set; }
        public TranslationLanguage LastTarget { get; private set; }

        public Task<string> TranslateAsync(
            string text,
            OCRLanguage sourceLang,
            TranslationLanguage targetLang,
            CancellationToken ct = default)
        {
            Calls++;
            LastText = text;
            LastSource = sourceLang;
            LastTarget = targetLang;
            return Task.FromResult(_result!);
        }
    }
}
