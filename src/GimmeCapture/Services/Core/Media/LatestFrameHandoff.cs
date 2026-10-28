using System;
using System.Buffers;

namespace GimmeCapture.Services.Core.Media;

/// <summary>
/// Hands decoded video frames from the decode thread to the UI thread. The pin used to copy each frame into one shared
/// buffer that the UI then copied out of with no lock held, so a frame could be drawn half old and half new; and it
/// copied the whole pooled array, up to twice the frame. Here the decoder fills a buffer of its own outside the lock
/// and swaps it with the latest frame's under the lock, and the UI copies the latest frame out under that lock. So
/// neither ever reads a buffer the other is writing, the UI never waits for the decoder's copy, and the decoder waits
/// at most for one copy into the bitmap. A frame the UI never got to is replaced by the next one. Two frame buffers,
/// from <see cref="ArrayPool{T}"/>; meant for one decode thread and one UI thread at a time.
/// </summary>
internal sealed class LatestFrameHandoff : IDisposable
{
    private readonly object _gate = new();
    private byte[]? _writing; // the decode thread's own
    private byte[]? _latest;  // under _gate, like the fields below
    private int _latestLength;
    private int _latestGeneration;
    private bool _hasNewFrame;
    private volatile bool _disposed;

    /// <summary>Decode thread: copies <paramref name="frame"/> in as the latest frame, tagged with its playback generation.</summary>
    public void Publish(ReadOnlySpan<byte> frame, int generation)
    {
        if (_disposed)
        {
            ReturnWritingBuffer();
            return;
        }

        if (_writing == null || _writing.Length < frame.Length)
        {
            ReturnWritingBuffer();
            _writing = ArrayPool<byte>.Shared.Rent(frame.Length);
        }

        frame.CopyTo(_writing);

        lock (_gate)
        {
            if (_disposed)
            {
                ReturnWritingBuffer();
                return;
            }

            (_writing, _latest) = (_latest, _writing);
            _latestLength = frame.Length;
            _latestGeneration = generation;
            _hasNewFrame = true;
        }
    }

    /// <summary>UI thread: whether a frame of playback <paramref name="generation"/> came in since the last copy.</summary>
    public bool HasNewFrame(int generation)
    {
        lock (_gate)
        {
            return _hasNewFrame && !_disposed && _latestGeneration == generation;
        }
    }

    /// <summary>
    /// UI thread: copies the newest frame published since the last call into <paramref name="destination"/>, if it
    /// belongs to playback <paramref name="generation"/>. Returns the bytes copied: the frame's length or the
    /// destination's, whichever is smaller, and 0 when there was no such frame.
    /// </summary>
    public int CopyLatestTo(Span<byte> destination, int generation)
    {
        lock (_gate)
        {
            if (_disposed || !_hasNewFrame)
            {
                return 0;
            }

            _hasNewFrame = false;
            if (_latestGeneration != generation)
            {
                return 0;
            }

            int count = Math.Min(_latestLength, destination.Length);
            _latest.AsSpan(0, count).CopyTo(destination);
            return count;
        }
    }

    /// <summary>
    /// Returns the latest frame's buffer to the pool. The decode thread's own goes back on its next
    /// <see cref="Publish"/>, since it may still be filling it.
    /// </summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _hasNewFrame = false;
            if (_latest != null)
            {
                ArrayPool<byte>.Shared.Return(_latest);
                _latest = null;
            }
        }
    }

    // Only the decode thread touches _writing, so this needs no lock.
    private void ReturnWritingBuffer()
    {
        if (_writing != null)
        {
            ArrayPool<byte>.Shared.Return(_writing);
            _writing = null;
        }
    }
}
