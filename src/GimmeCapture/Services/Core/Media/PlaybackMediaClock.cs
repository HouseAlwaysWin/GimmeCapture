using System;

namespace GimmeCapture.Services.Core.Media;

/// <summary>
/// The clock a looping pin's picture is shown by, in seconds of playback (<see cref="LoopPassTimeline"/>). While the
/// pin's sound plays, it is the sound's own position, read from the output device, so the picture keeps to what is
/// heard; a stopwatch kept the time before, while the sound ran on its own one, and the two came apart by whatever the
/// sound took to start. While there is no sound to follow (muted, a clip without one, a device that stopped or keeps no
/// clock), the stopwatch keeps the time. The picture holds its first frame until the sound starts, a second at most.
/// Readings come from the decode thread, every time it waits for a frame. A device may report its position in steps,
/// so between two the time runs on from the last by the stopwatch, a little way at most; and it never runs back: a
/// sound found behind the picture, at a hand-over from the stopwatch or after a stall, is waited for.
/// </summary>
internal sealed class PlaybackMediaClock
{
    /// <summary>The longest the picture holds its first frame for its sound to start.</summary>
    internal const double SoundStartWaitSeconds = 1.0;

    /// <summary>A sound whose position has not moved for this long is not followed until it moves again.</summary>
    internal const double SoundStallSeconds = 0.5;

    /// <summary>How far the time runs on from the sound's last reported position before it waits for the next.</summary>
    internal const double MaxRunOnSeconds = 0.05;

    private enum Mode
    {
        WaitingForSound,
        FollowingSound,
        Stopwatch,
    }

    private readonly double _speed;
    private Mode _mode = Mode.WaitingForSound;
    private double _beganAtWall;
    private double _offset;                         // on the stopwatch, the time is wall × speed + offset
    private double _lastSeconds;
    private double _soundSeconds = double.NaN;      // the sound's position when it last moved, and when that was
    private double _soundMovedAtWall;

    /// <param name="speed">Playback seconds per stopwatch second; a sound is followed at 1× only.</param>
    public PlaybackMediaClock(double speed = 1.0)
    {
        _speed = Math.Max(0.1, speed);
    }

    /// <summary>True while the picture holds its first frame for the sound to start: the time stands still.</summary>
    public bool IsWaitingForSound => _mode == Mode.WaitingForSound;

    /// <summary>True while the time is the sound's.</summary>
    public bool IsFollowingSound => _mode == Mode.FollowingSound;

    /// <summary>
    /// The first frame, at <paramref name="playbackSeconds"/>, is shown at <paramref name="wallSeconds"/>; the time
    /// starts there.
    /// </summary>
    public double Begin(double wallSeconds, double playbackSeconds, SoundClockReading sound)
    {
        _beganAtWall = wallSeconds;
        _lastSeconds = playbackSeconds;
        _mode = Mode.WaitingForSound;
        return Now(wallSeconds, sound);
    }

    /// <summary>The playback time at <paramref name="wallSeconds"/> on the stopwatch, given the sound's reading then.</summary>
    public double Now(double wallSeconds, SoundClockReading sound)
    {
        _lastSeconds = Read(wallSeconds, sound);
        return _lastSeconds;
    }

    private double Read(double wall, SoundClockReading sound)
    {
        if (sound.State == SoundClockState.Playing && sound.Seconds > 0)
        {
            if (sound.Seconds != _soundSeconds)
            {
                _soundSeconds = sound.Seconds;
                _soundMovedAtWall = wall;
            }

            double sinceMoved = wall - _soundMovedAtWall;
            if (sinceMoved <= SoundStallSeconds)
            {
                _mode = Mode.FollowingSound;
                double heard = _soundSeconds + (Math.Min(sinceMoved, MaxRunOnSeconds) * _speed);
                return Math.Max(_lastSeconds, heard);
            }
        }
        else if (sound.State != SoundClockState.Unavailable
                 && _mode == Mode.WaitingForSound
                 && wall - _beganAtWall < SoundStartWaitSeconds)
        {
            return _lastSeconds;
        }

        if (_mode != Mode.Stopwatch)
        {
            _offset = _lastSeconds - (wall * _speed);
            _mode = Mode.Stopwatch;
        }

        return (wall * _speed) + _offset;
    }
}
