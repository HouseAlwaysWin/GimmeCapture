using System;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using GimmeCapture.Models;
using GimmeCapture.Services.Translation;
using LLama;
using LLama.Common;
using LLama.Sampling;
using Xunit.Abstractions;

namespace GimmeCapture.Tests;

/// <summary>
/// <see cref="LlamaKeptContextGenerator"/> against StatelessExecutor, which generated every translation before it, on a
/// real GGUF model. From a fresh context the two must give the same text token for token: the generation is the same.
/// Kept from one prompt to the next, the context reads only what follows the instructions the prompts share; that the
/// translations are then still the same text, but for floating-point noise at a near tie, and how much sooner, is what
/// <see cref="LlamaSharpTranslationBenchmarkTests"/> shows. Opt-in: GIMMECAPTURE_LLAMA_MODEL names the model, as there.
/// </summary>
public sealed class LlamaKeptContextGeneratorTests(ITestOutputHelper output)
{
    private const string ModelVariable = "GIMMECAPTURE_LLAMA_MODEL";

    private static readonly (string Text, OCRLanguage Source)[] Texts =
    [
        ("Please save your work before closing the application.", OCRLanguage.English),
        ("このボタンを押すと設定が保存されます。", OCRLanguage.Japanese),
        ("錄影完成後，檔案會儲存在你選擇的資料夾。若要壓縮影片，請開啟壓縮分頁，加入檔案，然後按下開始。", OCRLanguage.TraditionalChinese),
        ("Low battery. Please connect your charger.", OCRLanguage.English),
    ];

    [OptInFact(ModelVariable)]
    public async Task FromAFreshContext_ItGeneratesWhatStatelessExecutorDoes()
    {
        var parameters = Parameters(2048);
        using var weights = await LLamaWeights.LoadFromFileAsync(parameters);
        foreach (bool greedy in new[] { true, false })
        {
            foreach (var (text, source) in Texts)
            {
                string prompt = LlamaSharpTranslationEngine.BuildTranslateGemmaPrompt(
                    text, source, TranslationLanguage.TraditionalChinese);

                using var generator = new LlamaKeptContextGenerator(weights, parameters);
                string? kept = await generator.TryGenerateAsync(prompt, Inference(text, greedy), default);

                var stateless = new StringBuilder();
                await foreach (string token in new StatelessExecutor(weights, parameters).InferAsync(prompt, Inference(text, greedy)))
                {
                    stateless.Append(token);
                }

                output.WriteLine($"greedy={greedy} {generator.LastTimings}: {kept?.Trim()}");
                Assert.Equal(0, generator.LastKept);
                Assert.Equal(stateless.ToString(), kept);
            }
        }
    }

    [OptInFact(ModelVariable)]
    public async Task TheNextPromptOfALanguagePair_KeepsTheInstructionsItShares()
    {
        var parameters = Parameters(2048);
        using var weights = await LLamaWeights.LoadFromFileAsync(parameters);
        using var generator = new LlamaKeptContextGenerator(weights, parameters);
        string first = LlamaSharpTranslationEngine.BuildTranslateGemmaPrompt(
            "Low battery.", OCRLanguage.English, TranslationLanguage.TraditionalChinese);
        string second = LlamaSharpTranslationEngine.BuildTranslateGemmaPrompt(
            "Update available. Restart now to install.", OCRLanguage.English, TranslationLanguage.TraditionalChinese);
        string otherPair = LlamaSharpTranslationEngine.BuildTranslateGemmaPrompt(
            "Update available.", OCRLanguage.English, TranslationLanguage.Japanese);

        await generator.TryGenerateAsync(first, Inference("Low battery.", greedy: true), default);
        await generator.TryGenerateAsync(second, Inference("Update available. Restart now to install.", greedy: true), default);
        int keptForTheSamePair = generator.LastKept;
        string timings = generator.LastTimings!;
        await generator.TryGenerateAsync(otherPair, Inference("Update available.", greedy: true), default);
        int keptForAnotherPair = generator.LastKept;

        int shared = SharedTokens(weights, parameters, first, second);
        output.WriteLine($"{timings}; the next pair kept {keptForAnotherPair}");
        Assert.True(shared > 60, $"The prompts share {shared} tokens.");
        Assert.Equal(shared, keptForTheSamePair);
        Assert.Contains("context=kept", timings);
        Assert.InRange(keptForAnotherPair, 1, shared - 1); // "You are a professional English (en) to ..."
    }

    [OptInFact(ModelVariable)]
    public async Task APromptThatCouldFillTheContext_IsLeftToStatelessExecutor()
    {
        var parameters = Parameters(256);
        using var weights = await LLamaWeights.LoadFromFileAsync(parameters);
        using var generator = new LlamaKeptContextGenerator(weights, parameters);
        string text = string.Concat(Enumerable.Repeat("Please save your work before closing the application. ", 6));
        string prompt = LlamaSharpTranslationEngine.BuildTranslateGemmaPrompt(
            text, OCRLanguage.English, TranslationLanguage.TraditionalChinese);

        Assert.Null(await generator.TryGenerateAsync(prompt, Inference(text, greedy: true), default));
    }

    private static ModelParams Parameters(uint contextSize) =>
        new(Environment.GetEnvironmentVariable(ModelVariable)!) { ContextSize = contextSize, GpuLayerCount = 0 };

    // As LlamaSharpTranslationEngine asks: greedy for TranslateGemma, otherwise its seeded sampling.
    private static InferenceParams Inference(string text, bool greedy) => new()
    {
        MaxTokens = Math.Clamp(text.Trim().Length * 4, 24, 192),
        AntiPrompts = ["<<END_TRANSLATION>>", "<end_of_turn>", "<start_of_turn>", "\nUser:", "\nAssistant:", "User:", "Assistant:"],
        SamplingPipeline = greedy
            ? new GreedySamplingPipeline()
            : new DefaultSamplingPipeline
            {
                Temperature = 0.10f, TopK = 12, TopP = 0.35f, RepeatPenalty = 1.10f, Seed = 1337, PenalizeNewline = false,
            },
    };

    private static int SharedTokens(LLamaWeights weights, ModelParams parameters, string a, string b)
    {
        using var context = weights.CreateContext(parameters);
        var first = context.Tokenize(a, addBos: true, special: true);
        var second = context.Tokenize(b, addBos: true, special: true);
        int shared = 0;
        while (shared < Math.Min(first.Length, second.Length) - 1 && (int)first[shared] == (int)second[shared])
        {
            shared++;
        }

        return shared;
    }
}
