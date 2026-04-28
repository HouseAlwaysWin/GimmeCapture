using System;
using System.IO;
using System.Text;
using ZLinq;
using System.Collections.Generic;
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

[MemoryDiagnoser]
public class FFmpegArgumentBuildingBenchmarks
{
    private string _mergedMkv = "C:\\Temp\\merged_video_file.mkv";
    private string _mergedAudio = "C:\\Temp\\merged_audio_file.wav";
    private string _cropFilter = "crop=1920:1080:0:0";
    private string _outputFile = "C:\\Output\\final_recording.mkv";

    [Benchmark]
    public string MkvConvertArgs()
    {
        var options = (Codec: "libx264", Crf: "23", Preset: "medium", WebmCrf: "25", WebmCpuUsed: "2");
        return $"-y -i \"{_mergedMkv}\" -i \"{_mergedAudio}\" -map 0:v:0 -map 1:a:0 -vf \"{_cropFilter}\" -c:v {options.Codec} -preset {options.Preset} -crf {options.Crf} -pix_fmt yuv420p -c:a aac -b:a 128k -shortest \"{_outputFile}\"";
    }

    [Benchmark]
    public string Mp4ConvertArgs()
    {
        var options = (Codec: "libx264", Crf: "23", Preset: "medium", WebmCrf: "25", WebmCpuUsed: "2");
        return $"-y -fflags +genpts -i \"{_mergedMkv}\" -f lavfi -i anullsrc=channel_layout=stereo:sample_rate=48000 -map 0:v:0 -map 1:a:0 -vf \"{_cropFilter}\" -vsync cfr -r 60 -c:v {options.Codec} -preset {options.Preset} -crf {options.Crf} -pix_fmt yuv420p -c:a aac -b:a 128k -shortest -movflags +faststart \"{_outputFile}\"";
    }
}

[MemoryDiagnoser]
public class LineDispatchStreamBenchmarks
{
    private byte[][] _chunks = Array.Empty<byte[]>();

    [GlobalSetup]
    public void Setup()
    {
        // Generate realistic FFmpeg log chunks
        var logLines = new[]
        {
            "frame=  123 fps= 30 q=28.0 size=    2048kB time=00:00:04.10 bitrate=4096.0kbits/s speed=1.0x",
            "frame=  153 fps= 30 q=28.0 size=    2560kB time=00:00:05.10 bitrate=4096.0kbits/s speed=1.0x",
            "frame=  183 fps= 30 q=28.0 size=    3072kB time=00:00:06.10 bitrate=4096.0kbits/s speed=1.0x",
            "Past duration 0.999992 too large",
            "More generic FFmpeg output logs..."
        };

        var random = new Random(42);
        var bytes = new List<byte[]>();
        
        // chunk sizes 10 to 100 bytes
        string fullLog = string.Join("\n", System.Linq.Enumerable.Repeat(logLines, 100).AsValueEnumerable().SelectMany(x => x).ToArray());
        byte[] fullBytes = Encoding.UTF8.GetBytes(fullLog);
        
        int offset = 0;
        while (offset < fullBytes.Length)
        {
            int chunkSize = Math.Min(random.Next(10, 100), fullBytes.Length - offset);
            byte[] chunk = new byte[chunkSize];
            Array.Copy(fullBytes, offset, chunk, 0, chunkSize);
            bytes.Add(chunk);
            offset += chunkSize;
        }

        _chunks = bytes.ToArray();
    }

    [Benchmark]
    public void ProcessStream()
    {
        int lineCount = 0;
        using var stream = new LineDispatchStream(line => lineCount++);
        foreach (var chunk in _chunks)
        {
            stream.Write(chunk, 0, chunk.Length);
        }
    }

    private sealed class LineDispatchStream : Stream
    {
        private readonly Action<string> _onLine;
        private readonly Decoder _decoder = Encoding.UTF8.GetDecoder();
        private readonly StringBuilder _lineBuffer = new();
        private readonly char[] _charBuffer = new char[4096];

        public LineDispatchStream(Action<string> onLine)
        {
            _onLine = onLine;
        }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() => FlushLineBuffer();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
        {
            if (count <= 0) return;

            int bytesUsed = 0;
            while (bytesUsed < count)
            {
                _decoder.Convert(
                    buffer,
                    offset + bytesUsed,
                    count - bytesUsed,
                    _charBuffer,
                    0,
                    _charBuffer.Length,
                    flush: false,
                    out int consumed,
                    out int charsUsed,
                    out _);

                bytesUsed += consumed;
                if (charsUsed > 0)
                {
                    ProcessChars(_charBuffer, charsUsed);
                }
            }
        }

        private void ProcessChars(char[] chars, int length)
        {
            for (int i = 0; i < length; i++)
            {
                char ch = chars[i];
                if (ch == '\n') EmitCurrentLine();
                else if (ch != '\r') _lineBuffer.Append(ch);
            }
        }

        private void EmitCurrentLine()
        {
            if (_lineBuffer.Length == 0) return;
            _onLine(_lineBuffer.ToString());
            _lineBuffer.Clear();
        }

        private void FlushLineBuffer()
        {
            if (_lineBuffer.Length == 0) return;
            _onLine(_lineBuffer.ToString());
            _lineBuffer.Clear();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) FlushLineBuffer();
            base.Dispose(disposing);
        }
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
