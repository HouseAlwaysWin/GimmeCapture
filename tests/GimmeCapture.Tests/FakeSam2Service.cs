using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using GimmeCapture.Services.Abstractions;
using SkiaSharp;

namespace GimmeCapture.Tests;

/// <summary>
/// SAM2 for a pin's smart selection, without the model: it prepares an image at once and answers every click with a
/// copy of one mask, off the caller's thread as the real service does. It counts what the pin hands it, and a test can
/// hold a prepare or any mask open to put something else in between.
/// </summary>
/// <remarks>
/// It reads a click's points where SAM2Service used to, off the caller's thread once any hold is released, so a pin
/// that hands over a list it goes on changing is caught asking for points it no longer has. Each mask it hands out is
/// a <see cref="TrackedMask"/>, which says whether the pin has freed it.
/// </remarks>
internal sealed class FakeSam2Service : ISam2Service
{
    private readonly SKBitmap _mask;
    private readonly bool _keepFreedMasks;
    private readonly TaskCompletionSource _firstPrepare = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly List<MaskRequest> _requests = new();
    private readonly List<TrackedMask> _masks = new();
    private readonly object _prepareLock = new();
    private readonly List<(int Count, TaskCompletionSource Reached)> _prepareWaiters = new();
    private bool _holdMasks;
    private TaskCompletionSource? _prepareHold;
    private int _prepareCount;
    private int _preparedGeneration;
    private volatile bool _isImagePrepared;

    /// <param name="mask">The mask every click gets a copy of.</param>
    /// <param name="keepFreedMasks">
    /// Whether a mask the pin frees keeps its pixels until <see cref="FreeKeptMasks"/>. A test can then catch a mask freed
    /// while something still had to read it, and that read stays a read instead of an access violation.
    /// </param>
    public FakeSam2Service(SKBitmap mask, bool keepFreedMasks = false)
    {
        _mask = mask;
        _keepFreedMasks = keepFreedMasks;
    }

    public string LastIouInfo => string.Empty;
    public string ModelVariantName => "Fake";
    public bool IsImagePrepared => _isImagePrepared;

    /// <summary>How many images the pin handed to <see cref="EnsureImagePreparedAsync"/>.</summary>
    public int PrepareCount => Volatile.Read(ref _prepareCount);

    /// <summary>The first pixel of the last image handed over, as the word it is in memory (0xAARRGGBB).</summary>
    public uint LastPreparedPixel { get; private set; }

    /// <summary>Done once the first image has been handed over.</summary>
    public Task FirstPrepare => _firstPrepare.Task;

    /// <summary>Every mask asked for, in the order asked.</summary>
    public IReadOnlyList<MaskRequest> Requests
    {
        get
        {
            lock (_requests)
            {
                return _requests.ToArray();
            }
        }
    }

    /// <summary>The points of each mask computed so far, in the order asked.</summary>
    public IReadOnlyList<(double X, double Y, bool IsPositive)[]> MaskRequests =>
        Requests.Where(r => r.Points != null).Select(r => r.Points!).ToArray();

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
        int generation;
        TaskCompletionSource? hold;
        var reached = new List<TaskCompletionSource>();
        lock (_prepareLock)
        {
            generation = _preparedGeneration;
            hold = _prepareHold;
            _prepareCount++;
            for (int i = _prepareWaiters.Count - 1; i >= 0; i--)
            {
                if (_prepareWaiters[i].Count <= _prepareCount)
                {
                    reached.Add(_prepareWaiters[i].Reached);
                    _prepareWaiters.RemoveAt(i);
                }
            }
        }

        _firstPrepare.TrySetResult();
        reached.ForEach(r => r.TrySetResult());
        if (hold == null)
        {
            Finish();
            return Task.CompletedTask;
        }

        return FinishAfterAsync(hold.Task);

        async Task FinishAfterAsync(Task held)
        {
            await held;
            Finish();
        }

        // As SAM2Service does: an image invalidated while it was being prepared is left unprepared.
        void Finish()
        {
            lock (_prepareLock)
            {
                if (generation == _preparedGeneration)
                {
                    _isImagePrepared = true;
                }
            }
        }
    }

    /// <summary>Done once the pin has handed over <paramref name="count"/> images in all.</summary>
    public Task Prepared(int count)
    {
        lock (_prepareLock)
        {
            if (_prepareCount >= count)
            {
                return Task.CompletedTask;
            }

            var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _prepareWaiters.Add((count, reached));
            return reached.Task;
        }
    }

    /// <summary>Holds every prepare from now on until <see cref="ReleasePrepares"/>.</summary>
    public void HoldPrepares()
    {
        lock (_prepareLock)
        {
            _prepareHold ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    public void ReleasePrepares()
    {
        TaskCompletionSource? hold;
        lock (_prepareLock)
        {
            hold = _prepareHold;
            _prepareHold = null;
        }

        hold?.TrySetResult();
    }

    public Task<SKBitmap?> GetMaskBitmapAsync(IReadOnlyList<(double X, double Y, bool IsPositive)> points)
    {
        MaskRequest request;
        lock (_requests)
        {
            request = new MaskRequest(_holdMasks);
            _requests.Add(request);
        }

        return Task.Run(async () =>
        {
            await request.Held;

            // Read here, off the caller's thread and after any hold, as SAM2Service read them.
            request.Points = [.. points];
            TrackedMask mask = CopyMask();
            request.Mask = mask;
            return (SKBitmap?)mask;
        });
    }

    /// <summary>Holds every mask asked for from now on until it is released, by itself or by <see cref="ReleaseMasks"/>.</summary>
    public void HoldMasks()
    {
        lock (_requests)
        {
            _holdMasks = true;
        }
    }

    /// <summary>Releases every mask held, and holds no more.</summary>
    public void ReleaseMasks()
    {
        MaskRequest[] requests;
        lock (_requests)
        {
            _holdMasks = false;
            requests = _requests.ToArray();
        }

        foreach (var request in requests)
        {
            request.Release();
        }
    }

    public void InvalidatePreparedImage()
    {
        lock (_prepareLock)
        {
            _preparedGeneration++;
            _isImagePrepared = false;
        }
    }

    /// <summary>
    /// The pin lets go of SAM2 at every Esc and change of tool. Unlike SAM2Service this one goes on working, so a test
    /// can hand it back to the pin.
    /// </summary>
    public void Dispose()
    {
    }

    /// <summary>Frees what the masks handed out still hold. For a test's clean-up, once the pin is gone.</summary>
    public void FreeKeptMasks()
    {
        TrackedMask[] masks;
        lock (_masks)
        {
            masks = _masks.ToArray();
            _masks.Clear();
        }

        foreach (var mask in masks)
        {
            mask.FreePixels();
        }
    }

    private TrackedMask CopyMask()
    {
        var mask = new TrackedMask(_mask.Info, _keepFreedMasks);
        using (var pixels = _mask.PeekPixels())
        {
            pixels.ReadPixels(mask.Info, mask.GetPixels(), mask.RowBytes);
        }

        lock (_masks)
        {
            _masks.Add(mask);
        }

        return mask;
    }

    /// <summary>One mask asked for.</summary>
    internal sealed class MaskRequest
    {
        private readonly TaskCompletionSource? _hold;

        internal MaskRequest(bool held)
        {
            _hold = held ? new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) : null;
        }

        /// <summary>The points the mask was computed for, as read once any hold was released; null until then.</summary>
        public (double X, double Y, bool IsPositive)[]? Points { get; internal set; }

        /// <summary>The mask handed out for it; null until then.</summary>
        public TrackedMask? Mask { get; internal set; }

        internal Task Held => _hold?.Task ?? Task.CompletedTask;

        /// <summary>Lets this mask through, if it is held.</summary>
        public void Release() => _hold?.TrySetResult();
    }
}

/// <summary>
/// A mask handed out by <see cref="FakeSam2Service"/>, which says whether it has been freed. One made to keep its pixels
/// only records being freed, and keeps them until <see cref="FreePixels"/>.
/// </summary>
internal sealed class TrackedMask : SKBitmap
{
    private readonly bool _keepPixels;
    private int _freed;

    public TrackedMask(SKImageInfo info, bool keepPixels)
        : base(info)
    {
        _keepPixels = keepPixels;
    }

    /// <summary>Whether something has freed this mask.</summary>
    public bool IsFreed => Volatile.Read(ref _freed) == 1;

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Interlocked.Exchange(ref _freed, 1);
            if (_keepPixels)
            {
                return;
            }
        }

        base.Dispose(disposing);
    }

    /// <summary>Frees the pixels, whether or not anything has freed the mask yet.</summary>
    internal void FreePixels() => base.Dispose(true);
}
