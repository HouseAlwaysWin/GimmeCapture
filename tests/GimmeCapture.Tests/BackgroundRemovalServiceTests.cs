using System.Diagnostics;
using SkiaSharp;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Xunit.Abstractions;

namespace GimmeCapture.Tests;

public class BackgroundRemovalServiceTests
{
    // Path to u2net.onnx, the AI Core model (176 MB), for the opt-in test that runs it.
    private const string ModelVariable = "GIMMECAPTURE_U2NET_MODEL";

    private readonly ITestOutputHelper _output;

    public BackgroundRemovalServiceTests(ITestOutputHelper output) => _output = output;

    // Runs the real model in the session the service builds (DirectML, where there is a GPU) and in one on the CPU
    // alone, and checks they give the same mask. An ONNX Runtime or DirectML update changes the GPU path without any
    // of our code changing, so run it after one; it also reports how long each takes. Opt-in: it needs the model.
    [OptInFact(ModelVariable)]
    public void TheServiceSession_GivesTheSameMaskAsTheCpu()
    {
        string modelPath = Environment.GetEnvironmentVariable(ModelVariable)!;
        using var scene = CreateTestScene();
        var input = BackgroundRemovalService.ExtractPixels(scene);

        float[] cpu;
        using (var cpuOptions = new SessionOptions())
        {
            cpu = RunModel("CPU", () => new InferenceSession(modelPath, cpuOptions), input);
        }

        string provider = "?";
        float[] service = RunModel("service", () => BackgroundRemovalService.CreateSession(modelPath, out provider), input);
        _output.WriteLine($"The service's session runs on: {provider}");

        Assert.Equal(cpu.Length, service.Length);
        double largest = 0;
        for (int i = 0; i < cpu.Length; i++)
        {
            largest = Math.Max(largest, Math.Abs(cpu[i] - service[i]));
        }

        _output.WriteLine($"Largest mask difference: {largest:G3}");
        Assert.True(largest < 1e-3, $"The masks differ by up to {largest:G3}.");
    }

    [Fact]
    public void IsSolidBackground_DetectsUniformOpaqueEdges()
    {
        using var bitmap = new SKBitmap(64, 64, SKColorType.Bgra8888, SKAlphaType.Unpremul);
        bitmap.Erase(new SKColor(240, 240, 240, 255));

        bool result = BackgroundRemovalService.IsSolidBackground(bitmap, out SKColor backgroundColor);

        Assert.True(result);
        Assert.Equal((byte)240, backgroundColor.Red);
        Assert.Equal((byte)240, backgroundColor.Green);
        Assert.Equal((byte)240, backgroundColor.Blue);
    }

    [Fact]
    public void TryGetCornerBackgroundColor_DetectsDominantCornerColor()
    {
        using var bitmap = new SKBitmap(40, 40, SKColorType.Bgra8888, SKAlphaType.Unpremul);
        bitmap.Erase(SKColors.White);
        using var canvas = new SKCanvas(bitmap);
        using var paint = new SKPaint { Color = SKColors.Red, Style = SKPaintStyle.Fill };
        canvas.DrawRect(new SKRect(10, 10, 30, 30), paint);

        bool result = BackgroundRemovalService.TryGetCornerBackgroundColor(bitmap, out SKColor backgroundColor);

        Assert.True(result);
        Assert.Equal(SKColors.White.Red, backgroundColor.Red);
        Assert.Equal(SKColors.White.Green, backgroundColor.Green);
        Assert.Equal(SKColors.White.Blue, backgroundColor.Blue);
    }

    [Fact]
    public void RemoveSolidBackground_MakesMatchingPixelsTransparent()
    {
        using var bitmap = new SKBitmap(2, 1, SKColorType.Bgra8888, SKAlphaType.Unpremul);
        bitmap.Erase(SKColors.White);
        bitmap.SetPixel(1, 0, SKColors.Black);

        using var result = BackgroundRemovalService.RemoveSolidBackground(bitmap, SKColors.White);

        Assert.Equal((byte)0, result.GetPixel(0, 0).Alpha);
        Assert.Equal((byte)255, result.GetPixel(1, 0).Alpha);
    }

    [Fact]
    public void ProcessMask_ResizesToSingleChannelMask()
    {
        var tensor = CreateLeftOpaqueMaskTensor();

        using var mask = BackgroundRemovalService.ProcessMask(tensor, 435, 434, out _, out _);

        Assert.Equal(435, mask.Width);
        Assert.Equal(434, mask.Height);
        Assert.Equal(SKColorType.Gray8, mask.ColorType);
    }

    [Fact]
    public void ApplyMask_UsesResizedMaskAcrossFullWidth()
    {
        using var bitmap = new SKBitmap(435, 4, SKColorType.Bgra8888, SKAlphaType.Unpremul);
        bitmap.Erase(SKColors.Red);
        var tensor = CreateLeftOpaqueMaskTensor();
        using var mask = BackgroundRemovalService.ProcessMask(tensor, bitmap.Width, bitmap.Height, out _, out _);

        using var result = BackgroundRemovalService.ApplyMask(bitmap, mask, 0, 1);

        Assert.True(result.GetPixel(20, 2).Alpha > 240);
        Assert.True(result.GetPixel(420, 2).Alpha < 15);
    }

    /// <summary>Runs the model once to keep, then ten times to time, and reports the times.</summary>
    private float[] RunModel(string label, Func<InferenceSession> buildSession, DenseTensor<float> input)
    {
        var stopwatch = Stopwatch.StartNew();
        using var session = buildSession();
        double build = stopwatch.Elapsed.TotalMilliseconds;
        var inputs = new[] { NamedOnnxValue.CreateFromTensor("input.1", input) };

        stopwatch.Restart();
        float[] output = Run();
        double first = stopwatch.Elapsed.TotalMilliseconds;
        var times = new double[10];
        for (int i = 0; i < times.Length; i++)
        {
            stopwatch.Restart();
            Run();
            times[i] = stopwatch.Elapsed.TotalMilliseconds;
        }

        Array.Sort(times);
        _output.WriteLine($"{label}: session built in {build:F0} ms, first run {first:F0} ms, then {times[times.Length / 2]:F0} ms (median of {times.Length})");
        return output;

        float[] Run()
        {
            using var results = session.Run(inputs);
            return results.First().AsTensor<float>().ToArray();
        }
    }

    /// <summary>A dark disc and bar on a lit gradient, at the model's input size, so there is a subject to find.</summary>
    private static SKBitmap CreateTestScene()
    {
        var scene = new SKBitmap(320, 320, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(scene);
        using var background = new SKPaint
        {
            Shader = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(320, 320),
                [new SKColor(250, 220, 170), new SKColor(90, 140, 210)], SKShaderTileMode.Clamp),
        };
        canvas.DrawRect(new SKRect(0, 0, 320, 320), background);
        using var subject = new SKPaint { Color = new SKColor(40, 30, 30), IsAntialias = true };
        canvas.DrawCircle(160, 150, 80, subject);
        canvas.DrawRect(new SKRect(140, 220, 180, 300), subject);
        return scene;
    }

    private static DenseTensor<float> CreateLeftOpaqueMaskTensor()
    {
        var data = new float[320 * 320];
        for (int y = 0; y < 320; y++)
        {
            int rowOffset = y * 320;
            for (int x = 0; x < 320; x++)
            {
                data[rowOffset + x] = x < 160 ? 6f : -6f;
            }
        }

        return new DenseTensor<float>(data, new[] { 1, 1, 320, 320 });
    }
}
