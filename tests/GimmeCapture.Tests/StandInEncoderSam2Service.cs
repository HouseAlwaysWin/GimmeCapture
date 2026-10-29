using System.Threading.Channels;
using GimmeCapture.Models;
using GimmeCapture.Services.Abstractions;
using Microsoft.ML.OnnxRuntime.Tensors;
using SkiaSharp;

namespace GimmeCapture.Tests;

/// <summary>
/// A <see cref="SAM2Service"/> whose model is a stand-in: it counts as loaded, and its encoder makes nothing of an image
/// but its size. With <see cref="HoldEncodes"/>, each encoder run waits until the test finishes it, so the test can put
/// an invalidation, another prepare or a dispose in the middle of a prepare.
/// </summary>
internal sealed class StandInEncoderSam2Service : SAM2Service
{
    private readonly Channel<StandInEncode> _encodes = Channel.CreateUnbounded<StandInEncode>();
    private int _encodeCount;
    private int _loadCount;

    private StandInEncoderSam2Service(SAM2RuntimeService runtime, IAppSettingsService settings)
        : base(runtime, settings)
    {
    }

    public static StandInEncoderSam2Service Create()
    {
        var settings = new Mock<IAppSettingsService>();
        settings.Setup(s => s.Settings).Returns(new AppSettings());
        return new StandInEncoderSam2Service(new SAM2RuntimeService(new AIPathService(settings.Object)), settings.Object);
    }

    /// <summary>Whether an encoder run started from now on waits for <see cref="StandInEncode.Finish"/>.</summary>
    public bool HoldEncodes { get; set; }

    /// <summary>How many encoder runs have started.</summary>
    public int EncodeCount => Volatile.Read(ref _encodeCount);

    /// <summary>How many times a prepare made sure the model was loaded, which loads it if it is not.</summary>
    public int LoadCount => Volatile.Read(ref _loadCount);

    /// <summary>The next encoder run to start, in the order they start.</summary>
    public Task<StandInEncode> NextEncodeAsync() => _encodes.Reader.ReadAsync().AsTask();

    internal override Task<bool> EnsureEncoderLoadedAsync()
    {
        Interlocked.Increment(ref _loadCount);
        return Task.FromResult(true);
    }

    internal override Task<Sam2ImageEncoding> EncodeImageAsync(SKBitmap original)
    {
        Interlocked.Increment(ref _encodeCount);
        var encode = new StandInEncode(original.Width, original.Height);
        if (!HoldEncodes)
        {
            encode.Finish();
        }

        _encodes.Writer.TryWrite(encode);
        return encode.Result;
    }
}

/// <summary>One run of <see cref="StandInEncoderSam2Service"/>'s encoder.</summary>
internal sealed class StandInEncode
{
    private readonly TaskCompletionSource<Sam2ImageEncoding> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly int _width;
    private readonly int _height;

    public StandInEncode(int width, int height)
    {
        _width = width;
        _height = height;
    }

    public Task<Sam2ImageEncoding> Result => _result.Task;

    public void Finish() => _result.TrySetResult(new Sam2ImageEncoding(_width, _height, Tensor(), Tensor(), Tensor()));

    private static DenseTensor<float> Tensor() => new(new[] { 1 });
}
