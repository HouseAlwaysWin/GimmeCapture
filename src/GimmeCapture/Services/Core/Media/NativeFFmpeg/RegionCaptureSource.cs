using System;
using System.Threading;
using FFmpeg.AutoGen;

namespace GimmeCapture.Services.Core.Media.NativeFFmpeg;

/// <summary>
/// The frames of a region recording: from Desktop Duplication where it takes the region, from the screen-grab demuxer
/// otherwise. Desktop Duplication copies the region on the graphics card and reads only it back, where gdigrab has
/// Windows read the screen back through GDI for every frame: grabbing 1450x900 at 30 fps took 110 M CPU cycles a second
/// instead of 603, for the same pixels. It captures one display, so a region across two is grabbed as before.
/// Windows takes a duplication away when the desktop changes under it (a display mode change, the secure desktop of
/// a UAC prompt or of locking the computer), and the next frame then fails: the recording carries on from a fresh
/// duplication, or from the screen grab when a fresh one fails before its first frame or does not open.
/// </summary>
internal sealed unsafe class RegionCaptureSource : ICapturedFrameSource
{
    private readonly Func<ICapturedFrameSource?> _openDuplication;
    private readonly Func<ICapturedFrameSource> _openScreenGrab;
    private ICapturedFrameSource? _current;
    private bool _duplicating;
    private bool _gaveFrame;

    /// <param name="openDuplication">Opens Desktop Duplication on the region, or answers null where it does not open.</param>
    /// <param name="openScreenGrab">Opens the screen grab on the region; a failure there fails the recording, as it did.</param>
    public RegionCaptureSource(Func<ICapturedFrameSource?> openDuplication, Func<ICapturedFrameSource> openScreenGrab)
    {
        _openDuplication = openDuplication;
        _openScreenGrab = openScreenGrab;
        _current = Open(duplicate: true);
    }

    public string Name => _current?.Name ?? string.Empty;

    public bool TryRead(AVFrame* frame, CancellationToken ct)
    {
        while (true)
        {
            var current = _current ?? throw new ObjectDisposedException(nameof(RegionCaptureSource));
            try
            {
                bool read = current.TryRead(frame, ct);
                _gaveFrame |= read;
                return read;
            }
            catch (Exception ex) when (_duplicating)
            {
                // One that had worked is opened again; one that failed before giving a frame gives way to the screen grab.
                bool again = _gaveFrame;
                string why = ex is FFmpegNativeException ? ex.Message : ex.GetType().Name;
                AppLog.Information(
                    "RecordingNative",
                    $"Desktop Duplication stopped ({why}); {(again ? "opening it again" : "grabbing with the screen grab")}.");
                _current = null;
                current.Dispose();
                _current = Open(again);
            }
        }
    }

    public void Dispose()
    {
        var current = _current;
        _current = null;
        current?.Dispose();
    }

    private ICapturedFrameSource Open(bool duplicate)
    {
        _gaveFrame = false;
        var source = duplicate ? _openDuplication() : null;
        _duplicating = source != null;
        return source ?? _openScreenGrab();
    }
}
