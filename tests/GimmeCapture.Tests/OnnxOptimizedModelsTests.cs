using GimmeCapture.Services.Core.AI;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace GimmeCapture.Tests;

// The optimized copies OCR's sessions build from, on a model small enough to write here: y = x + c.
public sealed class OnnxOptimizedModelsTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "onnx-optimized-" + Guid.NewGuid().ToString("N"));
    private readonly string _model;

    public OnnxOptimizedModelsTests()
    {
        Directory.CreateDirectory(_folder);
        _model = Path.Combine(_folder, "tiny.onnx");
        TinyOnnxModel.WriteAdd(_model, [1f, 2f, 3f, 4f]);
    }

    private const string Cpu = OnnxProviderConfigurator.CpuProvider;

    private static SessionOptions NewOptions() => new();

    private static float[] Run(InferenceSession session, float[] x)
    {
        var input = new DenseTensor<float>(x, [4]);
        using var results = session.Run([NamedOnnxValue.CreateFromTensor("x", input)]);
        return results.First().AsTensor<float>().ToArray();
    }

    [Fact]
    public void ASavedCopy_IsWhatTheNextSessionBuildsFrom_WithTheSameAnswer()
    {
        float[] shippedAnswer;
        using (var session = OnnxOptimizedModels.CreateSession(_model, NewOptions, Cpu, out bool fromCopy))
        {
            Assert.False(fromCopy);
            shippedAnswer = Run(session, [10f, 20f, 30f, 40f]);
        }

        Assert.True(OnnxOptimizedModels.EnsureSaved(_model, NewOptions, Cpu));
        string copy = OnnxOptimizedModels.CopyPathFor(_model, Cpu)!;
        Assert.True(File.Exists(copy));
        Assert.Equal(Path.Combine(_folder, OnnxOptimizedModels.FolderName), Path.GetDirectoryName(copy));
        // Saved once: the next idle unload leaves it.
        Assert.False(OnnxOptimizedModels.EnsureSaved(_model, NewOptions, Cpu));

        using (var session = OnnxOptimizedModels.CreateSession(_model, NewOptions, Cpu, out bool fromCopy))
        {
            Assert.True(fromCopy);
            Assert.Equal(shippedAnswer, Run(session, [10f, 20f, 30f, 40f]));
        }

        Assert.Equal([11f, 22f, 33f, 44f], shippedAnswer);
        // Nothing half written stays beside it.
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(copy)!));
    }

    [Fact]
    public void ACopyThatDoesNotLoad_IsDeleted_AndTheModelLoadsAsBefore()
    {
        string copy = OnnxOptimizedModels.CopyPathFor(_model, Cpu)!;
        Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
        File.WriteAllBytes(copy, [1, 2, 3, 4, 5, 6, 7, 8]);

        using (var session = OnnxOptimizedModels.CreateSession(_model, NewOptions, Cpu, out bool fromCopy))
        {
            Assert.False(fromCopy);
            Assert.Equal([2f, 3f, 4f, 5f], Run(session, [1f, 1f, 1f, 1f]));
        }

        Assert.False(File.Exists(copy));
        Assert.True(OnnxOptimizedModels.EnsureSaved(_model, NewOptions, Cpu));
    }

    [Fact]
    public void AChangedModel_IsNotServedTheOldCopy_AndItsNewCopyReplacesIt()
    {
        Assert.True(OnnxOptimizedModels.EnsureSaved(_model, NewOptions, Cpu));
        string oldCopy = OnnxOptimizedModels.CopyPathFor(_model, Cpu)!;

        // A module update writes the model again: another file time, here other constants too.
        TinyOnnxModel.WriteAdd(_model, [100f, 200f, 300f, 400f]);
        File.SetLastWriteTimeUtc(_model, File.GetLastWriteTimeUtc(oldCopy).AddMinutes(5));
        Assert.NotEqual(oldCopy, OnnxOptimizedModels.CopyPathFor(_model, Cpu));

        using (var session = OnnxOptimizedModels.CreateSession(_model, NewOptions, Cpu, out bool fromCopy))
        {
            Assert.False(fromCopy);
            Assert.Equal([101f, 201f, 301f, 401f], Run(session, [1f, 1f, 1f, 1f]));
        }

        Assert.True(OnnxOptimizedModels.EnsureSaved(_model, NewOptions, Cpu));
        Assert.False(File.Exists(oldCopy));
        using (var session = OnnxOptimizedModels.CreateSession(_model, NewOptions, Cpu, out bool fromCopy))
        {
            Assert.True(fromCopy);
            Assert.Equal([101f, 201f, 301f, 401f], Run(session, [1f, 1f, 1f, 1f]));
        }
    }

    [Fact]
    public void AFolderThatCannotBeWritten_SavesNothing_AndTheModelLoadsAsBefore()
    {
        // A file where the copies' folder would go: the folder cannot be made, as on a full disk or a read-only share.
        File.WriteAllText(Path.Combine(_folder, OnnxOptimizedModels.FolderName), "not a folder");

        Assert.False(OnnxOptimizedModels.EnsureSaved(_model, NewOptions, Cpu));
        using var session = OnnxOptimizedModels.CreateSession(_model, NewOptions, Cpu, out bool fromCopy);
        Assert.False(fromCopy);
        Assert.Equal([2f, 3f, 4f, 5f], Run(session, [1f, 1f, 1f, 1f]));
    }

    [Fact]
    public void AnotherProvider_GetsACopyOfItsOwn()
    {
        // A copy is fitted to the providers it was made for: another set must not load it.
        Assert.NotEqual(OnnxOptimizedModels.CopyPathFor(_model, Cpu), OnnxOptimizedModels.CopyPathFor(_model, "DML"));
    }

    [Fact]
    public void AMissingModel_HasNoCopy()
    {
        Assert.Null(OnnxOptimizedModels.CopyPathFor(Path.Combine(_folder, "gone.onnx"), Cpu));
        Assert.False(OnnxOptimizedModels.EnsureSaved(Path.Combine(_folder, "gone.onnx"), NewOptions, Cpu));
    }

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch { /* best effort temp cleanup */ }
    }
}
