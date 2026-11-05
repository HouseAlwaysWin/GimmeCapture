using System;
using System.Threading;
using System.Threading.Tasks;
using GimmeCapture.Services.Core.Media.NativeFFmpeg;

namespace GimmeCapture.Services.Core.Media;

/// <summary>
/// The frames a paused video pin shows when it is sought, from one <see cref="LibavStillFrameDecoder"/> kept for the
/// pin's file at the pin's size. Each paused seek opened the file again, found its streams, set up the decoder and its
/// GPU device and decoded from the keyframe: 130-195 ms a frame on the user's recordings, where the kept decoder takes
/// 10-20 ms for a frame a little further on and 20-55 ms for one anywhere else. Released when the pin plays, since an open
/// decoder holds 70-90 MB, and closed by itself after <see cref="LibavStillFrameDecoder.IdleCloseDelay"/> unused. For one
/// caller at a time, as the pin's playback loop is.
/// </summary>
internal sealed class KeptStillFrames : IDisposable
{
    private LibavStillFrameDecoder? _decoder;
    private (string Path, int Width, int Height) _key;

    /// <summary>How many times the kept decoder opened its file; 0 with none kept.</summary>
    internal int Opens => _decoder?.Opens ?? 0;

    /// <summary>The time of the frame given last, the first at or after the time asked for.</summary>
    public double LastFrameSeconds => _decoder?.LastFrameSeconds ?? 0;

    /// <summary>
    /// The frame at <paramref name="seconds"/> of <paramref name="path"/> as width × height BGRA pixels, rows packed, or
    /// null when there is none or it was cancelled. Read it before the next call: the decoder reuses it.
    /// </summary>
    public Task<byte[]?> DecodeFrameAtAsync(string path, int width, int height, double seconds, CancellationToken ct)
    {
        if (_decoder == null || _key != (path, width, height))
        {
            _decoder?.Dispose();
            _decoder = new LibavStillFrameDecoder(path, width, height);
            _key = (path, width, height);
        }

        return _decoder.DecodeFrameAtAsync(seconds, ct);
    }

    /// <summary>Lets go of the decoder and the file it holds open.</summary>
    public void Release()
    {
        _decoder?.Dispose();
        _decoder = null;
    }

    public void Dispose() => Release();
}
