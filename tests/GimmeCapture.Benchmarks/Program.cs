using System;
using System.IO;
using System.Text;
using ZLinq;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using System.Text.Json;
using GimmeCapture.Models;
using GimmeCapture.Services.Core.Infrastructure;
using GimmeCapture.Services.Core.AI;
using GimmeCapture.Services.Core.Media.NativeFFmpeg;
using GimmeCapture.Services.Core.Rendering;
using GimmeCapture.Services.Translation;
using FFmpeg.AutoGen;
using Microsoft.ML.OnnxRuntime.Tensors;
using SkiaSharp;

namespace GimmeCapture.Benchmarks;

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

}

[MemoryDiagnoser]
public class InMemoryTranslationCacheBenchmarks
{
    private InMemoryTranslationCache _cache = null!;
    private string _hitKey = "LlamaSharp|English|TraditionalChinese|hello";
    private string _missKey = "LlamaSharp|English|TraditionalChinese|world";

    [GlobalSetup]
    public void Setup()
    {
        _cache = new InMemoryTranslationCache();
        for (int i = 0; i < 500; i++)
        {
            _cache.Set($"LlamaSharp|English|TraditionalChinese|word{i}", $"word{i} translated");
        }
        _cache.Set(_hitKey, "你好");
    }

    [Benchmark]
    public bool CacheHit() => _cache.TryGet(_hitKey, out _);

    [Benchmark]
    public bool CacheMiss() => _cache.TryGet(_missKey, out _);

    [Benchmark]
    public void CacheSet() => _cache.Set("LlamaSharp|English|TraditionalChinese|test", "測試");
}

[MemoryDiagnoser]
public class BackgroundRemovalPixelBenchmarks
{
    private SKBitmap _solidBackgroundBitmap = null!;
    private SKBitmap _maskBitmap = null!;
    private float[] _maskTensorData = null!;
    private DenseTensor<float> _maskTensor = null!;

    [GlobalSetup]
    public void Setup()
    {
        _solidBackgroundBitmap = new SKBitmap(320, 320, SKColorType.Bgra8888, SKAlphaType.Unpremul);
        _solidBackgroundBitmap.Erase(new SKColor(245, 245, 245, 255));

        using var canvas = new SKCanvas(_solidBackgroundBitmap);
        using var paint = new SKPaint { Color = new SKColor(32, 48, 96, 255), Style = SKPaintStyle.Fill };
        canvas.DrawCircle(160, 160, 80, paint);

        _maskBitmap = _solidBackgroundBitmap.Copy(SKColorType.Bgra8888);
        _maskTensorData = new float[320 * 320];
        for (int i = 0; i < _maskTensorData.Length; i++)
        {
            _maskTensorData[i] = (i % 320) < 160 ? -2f : 2f;
        }

        _maskTensor = new DenseTensor<float>(_maskTensorData, new[] { 1, 1, 320, 320 });
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _solidBackgroundBitmap.Dispose();
        _maskBitmap.Dispose();
    }

    [Benchmark]
    public bool DetectSolidBackground()
        => BackgroundRemovalService.IsSolidBackground(_solidBackgroundBitmap, out _);

    [Benchmark]
    public byte[] RemoveSolidBackground()
        => BackgroundRemovalService.RemoveSolidBackground(_solidBackgroundBitmap, new SKColor(245, 245, 245, 255));

    [Benchmark]
    public byte[] ApplyMask()
    {
        using var resizedMask = BackgroundRemovalService.ProcessMask(_maskTensor, _maskBitmap.Width, _maskBitmap.Height, out _, out _);
        return BackgroundRemovalService.ApplyMask(_maskBitmap, resizedMask, 0f, 1f);
    }
}

[MemoryDiagnoser]
public unsafe class LibavVideoFrameCopyBenchmarks
{
    private const int Width = 640;
    private const int Height = 360;
    private byte[] _destination = null!;
    private byte[] _source = null!;
    private AVFrame* _frame;
    private GCHandle _sourceHandle;

    [GlobalSetup]
    public void Setup()
    {
        _destination = new byte[Width * Height * 4];
        _source = new byte[Width * Height * 4];
        for (int i = 0; i < _source.Length; i++)
        {
            _source[i] = (byte)(i % 251);
        }

        _frame = ffmpeg.av_frame_alloc();
        _sourceHandle = GCHandle.Alloc(_source, GCHandleType.Pinned);
        _frame->data[0] = (byte*)_sourceHandle.AddrOfPinnedObject();
        _frame->linesize[0] = Width * 4;
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        if (_frame != null)
        {
            AVFrame* frame = _frame;
            ffmpeg.av_frame_free(&frame);
            _frame = null;
        }

        if (_sourceHandle.IsAllocated)
        {
            _sourceHandle.Free();
        }
    }

    [Benchmark]
    public void CopyBgraFrame() => LibavVideoFramePlayer.CopyBgraFrame(_frame, Width, Height, _destination);
}

[MemoryDiagnoser]
public class TranslationSanitizerBenchmarks
{
    private const string Original = "神級研究員\n重要";
    private const string Translation =
        "5. Translate into Japanese.\n神級研究員\n重要。\n神級研究員 (Shin-kyuu kenkyuuin)\n重要 (Juuyou)";

    [Benchmark]
    public string SanitizeTranslation() =>
        TranslationTextSanitizer.Sanitize(Original, Translation, TranslationLanguage.Japanese);

    [Benchmark]
    public string SanitizeOcr() =>
        OcrTextSanitizer.Sanitize("※※\n\u200B因為\uFEFF\n***");
}

[MemoryDiagnoser]
public class OcrGroupingBenchmarks
{
    private Rect[] _rects = null!;

    [GlobalSetup]
    public void Setup()
    {
        var rects = new List<Rect>(240);
        for (int paragraph = 0; paragraph < 20; paragraph++)
        {
            double top = paragraph * 90;
            for (int line = 0; line < 3; line++)
            {
                for (int segment = 0; segment < 4; segment++)
                {
                    rects.Add(new Rect(segment * 72, top + (line * 22), 60, 16));
                }
            }
        }

        _rects = rects.ToArray();
    }

    [Benchmark]
    public OcrCandidateGroupingResult GroupCandidates() => OcrCandidateGrouper.Group(_rects);
}

[MemoryDiagnoser]
public class AnnotationRenderBenchmarks
{
    private SKBitmap _bitmap = null!;
    private Annotation[] _annotations = null!;
    private Annotation _blurAnnotation = null!;

    [GlobalSetup]
    public void Setup()
    {
        _bitmap = new SKBitmap(1280, 720, SKColorType.Bgra8888, SKAlphaType.Premul);
        _blurAnnotation = new Annotation
        {
            Type = AnnotationType.Blur,
            StartPoint = new Avalonia.Point(300, 200),
            EndPoint = new Avalonia.Point(700, 480),
            EffectSettings = new AnnotationEffectSettings { BlurRadius = 12f },
            DrawingModeReferenceSize = new Avalonia.Size(1280, 720)
        };
        _annotations =
        [
            new Annotation
            {
                Type = AnnotationType.Rectangle,
                StartPoint = new Avalonia.Point(40, 40),
                EndPoint = new Avalonia.Point(420, 240),
                Color = Avalonia.Media.Colors.Red,
                Thickness = 4
            },
            new Annotation
            {
                Type = AnnotationType.Arrow,
                StartPoint = new Avalonia.Point(80, 500),
                EndPoint = new Avalonia.Point(900, 180),
                Color = Avalonia.Media.Colors.Gold,
                Thickness = 6
            },
            new Annotation
            {
                Type = AnnotationType.Pen,
                Color = Avalonia.Media.Colors.White,
                Thickness = 3
            }
        ];

        for (int i = 0; i < 100; i++)
        {
            _annotations[2].AddPoint(new Avalonia.Point(100 + (i * 7), 360 + Math.Sin(i / 5d) * 60));
        }
    }

    [GlobalCleanup]
    public void Cleanup() => _bitmap.Dispose();

    [Benchmark]
    public void RenderAnnotations()
    {
        _bitmap.Erase(SKColors.Black);
        AnnotationRenderService.Shared.RenderAnnotationsToBitmap(
            _bitmap,
            _annotations,
            1280,
            720,
            1280,
            720);
    }

    // Live-preview path (runs on every pointer move during a blur drag): exercises the ROI-crop +
    // single-allocation blur so the allocation/throughput win is measurable.
    [Benchmark]
    public void RenderBlurPreview()
    {
        using var preview = AnnotationRenderService.Shared.RenderAnnotationPreviewToSkBitmap(_bitmap, _blurAnnotation, 1280, 720);
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
