using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace GimmeCapture.Services.Platforms.Windows;

/// <summary>
/// Hands the frames a <see cref="WgcWindowCaptureSource"/> reads back to the recording encoder. The readback fills a
/// buffer nobody else holds (<see cref="Rent"/>) and makes it the latest frame (<see cref="Publish"/>); the encoder reads
/// the latest frame in place under a <see cref="FrameLease"/>, and the buffer it reads is not handed out for writing
/// until the lease is disposed. Buffers are used again, and three cover every overlap (one being written, the latest,
/// one being read), so a steady stream of same-sized frames allocates nothing. Before, every frame was a new array of
/// the frame's size, on the large object heap, and the encoder copied it twice more, even when nothing had changed.
/// Each published frame gets the next sequence number, which tells the encoder whether anything came in since the
/// frame it last converted.
/// </summary>
/// <remarks>
/// Rows start 64-byte aligned, like those of FFmpeg's own frames, so swscale reads them at full speed and any read
/// ahead past a row's end stays inside the buffer. The arrays are on the pinned object heap, so their addresses never
/// change; a frame that is still being read after <see cref="Dispose"/> stays alive, since its lease holds it.
/// </remarks>
internal sealed class WgcFrameExchange : IDisposable
{
    private const int Alignment = 64;
    private const int MaxIdleBuffers = 2; // with the latest (or the one being read), the three a steady stream needs

    private readonly object _gate = new();
    private readonly Stack<FrameBuffer> _idle = new(MaxIdleBuffers); // all fields below are under _gate
    private FrameBuffer? _latest;
    private FrameBuffer? _leased;
    private long _leaseId;
    private long _sequence;
    private bool _disposed;

    /// <summary>How many frames have been published: it changes whenever a new frame is in.</summary>
    public long Sequence => Interlocked.Read(ref _sequence);

    /// <summary>
    /// A buffer for a <paramref name="width"/> x <paramref name="height"/> frame that no reader holds, for the caller
    /// to fill and then <see cref="Publish"/>, or hand back with <see cref="Return"/>.
    /// </summary>
    public FrameBuffer Rent(int width, int height)
    {
        lock (_gate)
        {
            while (_idle.Count > 0)
            {
                FrameBuffer idle = _idle.Pop();
                if (idle.Width == width && idle.Height == height)
                {
                    return idle;
                }

                // Sized for frames from before the window was resized: let it go.
            }
        }

        return new FrameBuffer(width, height);
    }

    /// <summary>Makes a filled <see cref="Rent"/>ed buffer the latest frame. After <see cref="Dispose"/> it is dropped.</summary>
    public void Publish(FrameBuffer buffer)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            buffer.Sequence = ++_sequence;
            FrameBuffer? previous = _latest;
            _latest = buffer;
            if (previous != null && (previous.Width != buffer.Width || previous.Height != buffer.Height))
            {
                _idle.Clear(); // the window was resized: those were for the old size
            }

            if (previous != null && previous != _leased)
            {
                KeepIdle(previous); // one still being read comes back when its lease is disposed
            }
        }
    }

    /// <summary>Hands back a <see cref="Rent"/>ed buffer that could not be filled.</summary>
    public void Return(FrameBuffer buffer)
    {
        lock (_gate)
        {
            if (!_disposed && buffer != _latest && buffer != _leased)
            {
                KeepIdle(buffer);
            }
        }
    }

    /// <summary>
    /// The latest frame, if it is newer than <paramref name="afterSequence"/>, to read in place until the lease is
    /// disposed. One lease at a time.
    /// </summary>
    public bool TryRead(long afterSequence, out FrameLease lease)
    {
        lock (_gate)
        {
            FrameBuffer? latest = _latest;
            if (_disposed || latest == null || latest.Sequence <= afterSequence)
            {
                lease = default;
                return false;
            }

            if (_leased != null)
            {
                throw new InvalidOperationException("A captured frame is already being read; dispose its lease first.");
            }

            _leased = latest;
            lease = new FrameLease(this, latest, latest.Sequence, ++_leaseId);
            return true;
        }
    }

    /// <summary>
    /// Copies the latest frame into <paramref name="destination"/> as tightly packed rows (stride == width*4), growing
    /// it if it is too small. False when there is no frame yet.
    /// </summary>
    public unsafe bool TryCopyLatest(ref byte[]? destination, out int width, out int height)
    {
        lock (_gate)
        {
            FrameBuffer? latest = _latest;
            if (_disposed || latest == null)
            {
                width = 0;
                height = 0;
                return false;
            }

            int rowBytes = latest.Width * 4;
            int length = rowBytes * latest.Height;
            if (destination == null || destination.Length < length)
            {
                destination = new byte[length];
            }

            byte* source = (byte*)latest.Pixels;
            fixed (byte* target = destination)
            {
                for (int row = 0; row < latest.Height; row++)
                {
                    Buffer.MemoryCopy(source + (long)row * latest.Stride, target + (long)row * rowBytes, rowBytes, rowBytes);
                }
            }

            width = latest.Width;
            height = latest.Height;
            return true;
        }
    }

    /// <summary>Drops every frame. A frame still being read stays valid until its lease is disposed.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _latest = null;
            _idle.Clear();
        }
    }

    private void Release(FrameBuffer buffer, long leaseId)
    {
        lock (_gate)
        {
            if (_leased != buffer || _leaseId != leaseId)
            {
                return; // this lease was disposed already
            }

            _leased = null;
            if (!_disposed && buffer != _latest)
            {
                KeepIdle(buffer);
            }
        }
    }

    // Only buffers the size of the latest frame are kept: after a resize, those of the old size go as they come back.
    private void KeepIdle(FrameBuffer buffer)
    {
        FrameBuffer? latest = _latest;
        bool fits = latest == null || (buffer.Width == latest.Width && buffer.Height == latest.Height);
        if (fits && _idle.Count < MaxIdleBuffers)
        {
            _idle.Push(buffer);
        }
    }

    /// <summary>One frame's BGRA pixels: <see cref="Height"/> rows of <see cref="Width"/> pixels, <see cref="Stride"/> bytes apart.</summary>
    internal sealed unsafe class FrameBuffer
    {
        private readonly byte[] _memory; // Pixels points into it; pinned for its whole life

        public FrameBuffer(int width, int height)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
            Width = width;
            Height = height;
            Stride = checked((width * 4 + Alignment - 1) & ~(Alignment - 1));

            // One alignment's worth to align the first row, and one more past the last row for readers that look ahead.
            _memory = GC.AllocateUninitializedArray<byte>(checked(Stride * height + 2 * Alignment), pinned: true);
            nint start = (nint)Unsafe.AsPointer(ref MemoryMarshal.GetArrayDataReference(_memory));
            Pixels = (start + (Alignment - 1)) & ~(nint)(Alignment - 1);
        }

        public int Width { get; }

        public int Height { get; }

        public int Stride { get; }

        public IntPtr Pixels { get; }

        /// <summary>Set when the buffer is published; under the exchange's lock.</summary>
        internal long Sequence { get; set; }
    }

    /// <summary>
    /// The latest frame, held for reading in place: the exchange does not write into it again until this is disposed.
    /// Disposing it twice is harmless.
    /// </summary>
    internal readonly struct FrameLease : IDisposable
    {
        private readonly WgcFrameExchange? _owner;
        private readonly FrameBuffer? _buffer;
        private readonly long _id;

        internal FrameLease(WgcFrameExchange owner, FrameBuffer buffer, long sequence, long id)
        {
            _owner = owner;
            _buffer = buffer;
            _id = id;
            Sequence = sequence;
        }

        public IntPtr Pixels => _buffer?.Pixels ?? IntPtr.Zero;

        public int Width => _buffer?.Width ?? 0;

        public int Height => _buffer?.Height ?? 0;

        public int Stride => _buffer?.Stride ?? 0;

        /// <summary>The frame's place in the order frames were published, from 1.</summary>
        public long Sequence { get; }

        public void Dispose()
        {
            if (_owner != null && _buffer != null)
            {
                _owner.Release(_buffer, _id);
            }
        }
    }
}
