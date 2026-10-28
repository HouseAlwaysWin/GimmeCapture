using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using GimmeCapture.Services.Abstractions;
using SkiaSharp;

namespace GimmeCapture.Tests;

/// <summary>
/// SAM2 for a pin's smart selection, without the model: it prepares an image at once and answers every click with a
/// copy of one mask, off the caller's thread as the real service does. It counts what the pin hands it.
/// </summary>
internal sealed class FakeSam2Service : ISam2Service
{
    private readonly SKBitmap _mask;
    private readonly TaskCompletionSource _firstPrepare = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TaskCompletionSource? _maskGate;
    private int _prepareCount;

    public FakeSam2Service(SKBitmap mask) => _mask = mask;

    public string LastIouInfo => string.Empty;
    public string ModelVariantName => "Fake";
    public bool IsImagePrepared { get; private set; }

    /// <summary>How many images the pin handed to <see cref="EnsureImagePreparedAsync"/>.</summary>
    public int PrepareCount => Volatile.Read(ref _prepareCount);

    /// <summary>The first pixel of the last image handed over, as the word it is in memory (0xAARRGGBB).</summary>
    public uint LastPreparedPixel { get; private set; }

    /// <summary>Done once the first image has been handed over.</summary>
    public Task FirstPrepare => _firstPrepare.Task;

    /// <summary>The points of each mask asked for, in order.</summary>
    public List<(double X, double Y, bool IsPositive)[]> MaskRequests { get; } = new();

    /// <summary>Makes the next <see cref="InitializeAsync"/> let go of the image, as a switch of model does.</summary>
    public bool SwitchModelOnNextInitialize { get; set; }

    public Task InitializeAsync()
    {
        if (SwitchModelOnNextInitialize)
        {
            SwitchModelOnNextInitialize = false;
            InvalidatePreparedImage();
        }

        return Task.CompletedTask;
    }

    public Task EnsureImagePreparedAsync(SKBitmap original)
    {
        LastPreparedPixel = MemoryMarshal.Read<uint>(original.GetPixelSpan());
        IsImagePrepared = true;
        Interlocked.Increment(ref _prepareCount);
        _firstPrepare.TrySetResult();
        return Task.CompletedTask;
    }

    public Task<SKBitmap?> GetMaskBitmapAsync(IReadOnlyList<(double X, double Y, bool IsPositive)> points)
    {
        lock (MaskRequests)
        {
            MaskRequests.Add([.. points]);
        }

        TaskCompletionSource? gate = _maskGate;
        return Task.Run(async () =>
        {
            if (gate != null)
            {
                await gate.Task;
            }

            return (SKBitmap?)_mask.Copy();
        });
    }

    /// <summary>Holds every mask asked for from now on until <see cref="ReleaseMasks"/>.</summary>
    public void HoldMasks() => _maskGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

    public void ReleaseMasks()
    {
        _maskGate?.TrySetResult();
        _maskGate = null;
    }

    public void InvalidatePreparedImage() => IsImagePrepared = false;

    public void Dispose()
    {
    }
}
