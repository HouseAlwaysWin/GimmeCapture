using System;

namespace GimmeCapture.ViewModels.Shared;

/// <summary>
/// Hands decoded video frames from a decode thread to the UI thread, keeping only the newest. A frame that arrives
/// before the UI thread took the previous one replaces it, so a UI thread that falls behind skips frames instead of
/// working off a queue of stale ones. Its buffers are reused, so a steady stream of same-sized frames allocates
/// nothing.
/// </summary>
/// <remarks>
/// The decode thread copies into a buffer nobody else holds and then publishes it, and the UI thread takes the
/// published one away before drawing it: neither waits for the other's copy, and a frame is never overwritten while
/// it is being drawn.
/// </remarks>
internal sealed class LatestFrameSlot
{
    /// <summary>
    /// One decoded frame: tightly packed BGRA, <see cref="Width"/>×<see cref="Height"/>, at the start of
    /// <see cref="Pixels"/> (which can be longer). <see cref="Ticket"/> is the owner's tag for the playback that
    /// decoded it, so a frame from a playback since stopped can be told apart.
    /// </summary>
    internal readonly record struct Frame(byte[] Pixels, int Width, int Height, int Ticket, double Seconds);

    private readonly object _gate = new();
    private Frame? _pending;
    private byte[]? _spare;
    private bool _takeScheduled;

    /// <summary>
    /// Copies a frame in, replacing one the UI thread has not taken yet. Returns true when the caller must schedule a
    /// <see cref="Take"/> on the UI thread, false when one is already scheduled and will find this frame.
    /// </summary>
    public bool Offer(ReadOnlySpan<byte> bgra, int width, int height, int ticket, double seconds)
    {
        if (width <= 0 || height <= 0 || bgra.Length < width * height * 4)
        {
            return false;
        }

        int length = width * height * 4;
        byte[]? buffer;
        lock (_gate)
        {
            buffer = _spare;
            _spare = null;
        }

        if (buffer == null || buffer.Length < length)
        {
            buffer = GC.AllocateUninitializedArray<byte>(length);
        }

        bgra[..length].CopyTo(buffer);

        lock (_gate)
        {
            if (_pending is { } replaced)
            {
                Keep(replaced.Pixels); // never drawn: its buffer takes a later frame
            }

            _pending = new Frame(buffer, width, height, ticket, seconds);
            if (_takeScheduled)
            {
                return false;
            }

            _takeScheduled = true;
            return true;
        }
    }

    /// <summary>
    /// Takes the newest frame, or null when there is none. Hand its buffer back with <see cref="Return"/> once its
    /// pixels have been drawn.
    /// </summary>
    public Frame? Take()
    {
        lock (_gate)
        {
            Frame? frame = _pending;
            _pending = null;
            _takeScheduled = false;
            return frame;
        }
    }

    /// <summary>Gives a taken frame's buffer back for a later frame.</summary>
    public void Return(byte[] buffer)
    {
        lock (_gate)
        {
            Keep(buffer);
        }
    }

    // One spare is enough: with the pending frame and the one being drawn, three buffers cover every hand-over.
    private void Keep(byte[] buffer)
    {
        if (_spare == null || _spare.Length < buffer.Length)
        {
            _spare = buffer;
        }
    }
}
