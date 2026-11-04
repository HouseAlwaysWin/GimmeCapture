namespace GimmeCapture.Services.Core.Media;

/// <summary>
/// Lays the passes of a looping playback end to end. Each frame's time in the file becomes its time since the playback
/// began, which is what the picture is shown by: the first pass runs from where the playback began to the loop's end,
/// every later one from the loop's start to its end. The sound lays its passes out the same way
/// (<see cref="LoopingWaveProvider"/>), so a frame is due when the sound has played up to it, in any pass. A clip whose
/// picture ends before the loop does holds its last frame until the pass is over, as a player does while the sound
/// finishes.
/// </summary>
internal sealed class LoopPassTimeline
{
    // A frame within half a millisecond of a time counts as at it: the decoder's timestamps are rounded.
    private const double Epsilon = 0.0005;

    private readonly PlaybackLoop _loop;
    private double _passStartSeconds; // the file time the pass under way starts at
    private double _passBeganAt;      // the playback time it starts at

    public LoopPassTimeline(PlaybackLoop loop, double startSeconds)
    {
        _loop = loop;
        _passStartSeconds = loop.ClampStart(startSeconds);
    }

    /// <summary>How many passes have ended.</summary>
    public int PassesEnded { get; private set; }

    /// <summary>The file time the pass under way starts at; the seek lands before it, on a keyframe.</summary>
    public double PassStartSeconds => _passStartSeconds;

    /// <summary>A frame the seek decoded on its way to the pass's start: decoded, never shown.</summary>
    public bool IsBeforePassStart(double frameSeconds) => frameSeconds + Epsilon < _passStartSeconds;

    /// <summary>A frame at or past the loop's end, which ends the pass instead of being shown.</summary>
    public bool EndsPass(double frameSeconds) => frameSeconds >= _loop.EndSeconds - Epsilon;

    /// <summary>The playback time the frame at <paramref name="frameSeconds"/> of the pass under way is due at.</summary>
    public double PlaybackSeconds(double frameSeconds) => _passBeganAt + (frameSeconds - _passStartSeconds);

    /// <summary>The pass under way is over, at the loop's end; the next starts at the loop's start.</summary>
    public void NextPass()
    {
        _passBeganAt += _loop.EndSeconds - _passStartSeconds;
        _passStartSeconds = _loop.StartSeconds;
        PassesEnded++;
    }
}
