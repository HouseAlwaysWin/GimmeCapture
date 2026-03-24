using System;
using System.IO;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using System.Text.Json;
using GimmeCapture.Models;
using GimmeCapture.Services.Core.Infrastructure;
using GimmeCapture.Services.Core.AI;

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

class Program
{
    static void Main(string[] args)
    {
        Console.WriteLine("Starting Benchmark Switcher...");
        var switcher = BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly);
        switcher.Run(args);
    }
}
