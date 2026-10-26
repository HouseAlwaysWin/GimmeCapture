using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using GimmeCapture.Models;
using GimmeCapture.Services.Core.AI;
using GimmeCapture.Services.Translation;
using Xunit;
using Xunit.Abstractions;

namespace GimmeCapture.Tests;

// Translates a fixed set of sentences with a real GGUF model through LlamaSharpTranslationEngine, using the settings
// the app ships with (CPU only, context 2048), and reports how long each translation takes and what it produced.
// Written to compare LLamaSharp versions on one machine: run it before and after an upgrade and diff the timings and
// the text. Opt-in: it needs a multi-GB model and takes minutes on a CPU.
public sealed class LlamaSharpTranslationBenchmarkTests : IDisposable
{
    private const string ModelVariable = "GIMMECAPTURE_LLAMA_MODEL";

    // The kind of text the app translates: short UI and status strings read off the screen by OCR. The first six are
    // the original set (kept first so older timings stay comparable); the rest widen it enough to judge a prompt
    // change, including Japanese questions, which tempt a chat model to answer instead of translate.
    private static readonly (string Text, OCRLanguage Source)[] Sentences =
    [
        ("Please save your work before closing the application.", OCRLanguage.English),
        ("An error occurred while connecting to the server. Please try again later.", OCRLanguage.English),
        ("Your subscription will renew automatically on the first day of next month.", OCRLanguage.English),
        ("このボタンを押すと設定が保存されます。", OCRLanguage.Japanese),
        ("ダウンロードが完了しました。", OCRLanguage.Japanese),
        ("会議は明日の午後三時に変更されました。", OCRLanguage.Japanese),
        ("Click here to reset your password.", OCRLanguage.English),
        ("This file is too large to upload.", OCRLanguage.English),
        ("Do you want to delete the selected items?", OCRLanguage.English),
        ("Update available. Restart now to install.", OCRLanguage.English),
        ("Low battery. Please connect your charger.", OCRLanguage.English),
        ("パスワードが正しくありません。", OCRLanguage.Japanese),
        ("ファイルを保存しますか？", OCRLanguage.Japanese),
        ("ネットワークに接続できません。", OCRLanguage.Japanese),
        ("変更内容は保存されていません。", OCRLanguage.Japanese),
        ("次へ進む前に利用規約に同意してください。", OCRLanguage.Japanese),
    ];

    private readonly string _baseDir = Path.Combine(Path.GetTempPath(), "GimmeCapture.Tests", Guid.NewGuid().ToString("N"));
    private readonly ITestOutputHelper _output;

    public LlamaSharpTranslationBenchmarkTests(ITestOutputHelper output)
    {
        _output = output;
        Directory.CreateDirectory(_baseDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_baseDir, recursive: true); } catch { /* best effort */ }
    }

    [OptInFact(ModelVariable)]
    public async Task TranslatesWithARealModel_AndReportsTimings()
    {
        var settings = new AppSettingsService(_baseDir);
        settings.Settings.AIResourcesDirectory = Path.Combine(_baseDir, "AI");
        settings.Settings.LlamaModelId = string.Empty; // so the custom path below is what loads
        settings.Settings.LlamaCustomModelPath = Environment.GetEnvironmentVariable(ModelVariable)!;
        settings.Settings.LlamaContextSize = 2048;
        settings.Settings.LlamaGpuLayers = 0;

        var paths = new AIPathService(settings);
        var resources = new AIResourceService(settings, paths, new NativeResolverService(paths), new AIModelDownloader());
        using var engine = new LlamaSharpTranslationEngine(resources, settings, new InMemoryTranslationCache());

        // The first call loads the weights: keep that out of the per-sentence figures.
        var sw = Stopwatch.StartNew();
        string warmUp = await engine.TranslateAsync("Hello.", OCRLanguage.English, TranslationLanguage.TraditionalChinese);
        _output.WriteLine($"load + warm-up: {sw.Elapsed.TotalMilliseconds,7:F0} ms  -> {warmUp}");

        var times = new List<double>();
        var results = new List<(string Text, OCRLanguage Source, string Result)>();
        foreach (var (text, source) in Sentences)
        {
            sw.Restart();
            string result = await engine.TranslateAsync(text, source, TranslationLanguage.TraditionalChinese);
            double ms = sw.Elapsed.TotalMilliseconds;
            times.Add(ms);
            results.Add((text, source, result));
            _output.WriteLine($"{ms,7:F0} ms  {text}  ->  {result}");
        }

        times.Sort();
        _output.WriteLine($"median {times[times.Count / 2]:F0} ms, total {times.Sum():F0} ms over {times.Count} sentences");

        // Checked after every sentence has run, so one bad translation does not hide the others' timings.
        foreach (var (text, source, result) in results)
        {
            Assert.False(string.IsNullOrWhiteSpace(result), $"No translation for: {text}");
            // Traditional Chinese has no kana: kana in the output means the text came back still in Japanese.
            Assert.False(
                source == OCRLanguage.Japanese && result.Any(static c => c is >= '぀' and <= 'ヿ'),
                $"Came back in Japanese: {text} -> {result}");
        }

        // The sentence that once came back as the unrelated button label "保存して閉じる" (save and close): it is about
        // the settings being saved, so a faithful translation has to mention them.
        var button = results.Single(r => r.Text.StartsWith("このボタン", StringComparison.Ordinal));
        Assert.Contains("設定", button.Result);
    }
}
