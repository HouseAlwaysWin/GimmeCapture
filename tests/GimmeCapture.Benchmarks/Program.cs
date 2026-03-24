using System.Threading;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using System.Text.Json;
using GimmeCapture.Models;
using GimmeCapture.Services.Core.Infrastructure;
using GimmeCapture.Services.Core.AI;
using GimmeCapture.Services.Translation;

namespace GimmeCapture.Benchmarks;

[MemoryDiagnoser]
public class FileSizeBenchmarks
{
    private string[] _files = Array.Empty<string>();

    [GlobalSetup]
    public void Setup()
    {
        // 為了模擬真實的暫存資料夾檔案，我們在系統暫存資料夾產生 100 個 1KB 的假檔案
        string tempDir = Path.Combine(Path.GetTempPath(), "BenchmarkTestFiles");
        if (!Directory.Exists(tempDir))
            Directory.CreateDirectory(tempDir);

        for (int i = 0; i < 100; i++)
        {
            File.WriteAllBytes(Path.Combine(tempDir, $"segment_{i}.mkv"), new byte[1024]);
        }

        _files = Directory.GetFiles(tempDir);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "BenchmarkTestFiles");
        if (Directory.Exists(tempDir))
            Directory.Delete(tempDir, true);
    }

    [Benchmark(Baseline = true)]
    public long TryCatchInsideLoop()
    {
        long totalSize = 0;
        foreach (var file in _files)
        {
            try 
            { 
                if (File.Exists(file)) 
                    totalSize += new FileInfo(file).Length; 
            } 
            catch 
            { 
            }
        }
        return totalSize;
    }

    [Benchmark]
    public long TryCatchOutsideLoop()
    {
        long totalSize = 0;
        try
        {
            foreach (var file in _files)
            {
                if (File.Exists(file)) 
                    totalSize += new FileInfo(file).Length;
            }
        }
        catch 
        { 
        }
        return totalSize;
    }
}

[MemoryDiagnoser]
public class SettingsSerializationBenchmarks
{
    private AppSettings _settings = new();
    private string _json = "";
    private JsonSerializerOptions _options = new();

    [GlobalSetup]
    public void Setup()
    {
        _settings = new AppSettings();
        _options = new JsonSerializerOptions 
        { 
            WriteIndented = true,
            PropertyNameCaseInsensitive = true,
            Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
        };
        _json = JsonSerializer.Serialize(_settings, _options);
    }

    [Benchmark]
    public string SerializeSettings() => JsonSerializer.Serialize(_settings, _options);

    [Benchmark]
    public AppSettings? DeserializeSettings() => JsonSerializer.Deserialize<AppSettings>(_json, _options);
}

[MemoryDiagnoser]
public class AIPathServiceBenchmarks
{
    private AIPathService _pathService = null!;

    [GlobalSetup]
    public void Setup()
    {
        var settingsService = new AppSettingsService();
        _pathService = new AIPathService(settingsService);
    }

    [Benchmark]
    public (string, string) SAM2Paths() => _pathService.GetSAM2Paths(SAM2Variant.BasePlus);

    [Benchmark]
    public (string, string, string) OCRPaths() => _pathService.GetOCRPaths(OCRLanguage.TraditionalChinese);

    [Benchmark]
    public (string, string, string, string, string, string) NmtPaths() => _pathService.GetNmtPaths();
}

[MemoryDiagnoser]
public class InMemoryTranslationCacheBenchmarks
{
    private InMemoryTranslationCache _cache = null!;
    private string _hitKey = "Ollama|English|TraditionalChinese|hello";
    private string _missKey = "Ollama|English|TraditionalChinese|world";

    [GlobalSetup]
    public void Setup()
    {
        _cache = new InMemoryTranslationCache();
        for (int i = 0; i < 500; i++)
        {
            _cache.Set($"Ollama|English|TraditionalChinese|word{i}", $"word{i} translated");
        }
        _cache.Set(_hitKey, "你好");
    }

    [Benchmark]
    public bool CacheHit() => _cache.TryGet(_hitKey, out _);

    [Benchmark]
    public bool CacheMiss() => _cache.TryGet(_missKey, out _);

    [Benchmark]
    public void CacheSet() => _cache.Set("Ollama|English|TraditionalChinese|test", "測試");
}

public class FakeOllamaClient : IOllamaApiClient
{
    public Task<string> GenerateAsync(string model, string prompt, CancellationToken cancellationToken = default)
    {
        return Task.FromResult("{\"response\": \"<think>thinking...</think>Translation: \\\"你好\\\"\"}");
    }

    public Task<System.Collections.Generic.IReadOnlyList<string>> GetModelsAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult<System.Collections.Generic.IReadOnlyList<string>>(new[] { "llama3" });
    }

    public Task<bool> IsReadyAsync(string model, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(true);
    }
}

[MemoryDiagnoser]
public class LLMTranslationEngineBenchmarks
{
    private LLMTranslationEngine _engine = null!;
    private const string text = "Hello";

    [GlobalSetup]
    public void Setup()
    {
        var settingsService = new AppSettingsService();
        settingsService.Settings.OllamaModel = "llama3";
        _engine = new LLMTranslationEngine(new FakeOllamaClient(), settingsService, new InMemoryTranslationCache());
    }

    [Benchmark]
    public async Task<string> TranslateAndParseOverhead()
    {
        return await _engine.TranslateAsync(text, OCRLanguage.English, TranslationLanguage.TraditionalChinese);
    }
}

class Program
{
    static void Main(string[] args)
    {
        Console.WriteLine("Starting Benchmark Switcher...");
        var switcher = BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly);
        switcher.Run(args);
    }
}
