using System;
using System.Threading;
using FFmpeg.AutoGen;

namespace GimmeCapture.Services.Core.Media.NativeFFmpeg;

/// <summary>
/// Where a region recording's frames come from, one at a time in the order they were grabbed: the screen-grab demuxer
/// (<see cref="LibavScreenGrabSource"/>) or Desktop Duplication (<see cref="LibavDesktopDuplicationSource"/>), with
/// <see cref="RegionCaptureSource"/> choosing between them.
/// </summary>
internal unsafe interface ICapturedFrameSource : IDisposable
{
    /// <summary>What grabs the frames, for the log: "ddagrab", "gdigrab" or "x11grab".</summary>
    string Name { get; }

    /// <summary>
    /// Takes the next frame into <paramref name="frame"/>, which it unreferences first, waiting until that frame is due:
    /// every source paces itself to the frame rate it was opened with. False once the input has ended, or when
    /// <paramref name="ct"/> is cancelled while it waits; a failure throws.
    /// </summary>
    bool TryRead(AVFrame* frame, CancellationToken ct);
}
