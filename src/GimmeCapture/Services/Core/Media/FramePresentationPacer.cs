using System;
using System.Diagnostics;

namespace GimmeCapture.Services.Core.Media;

/// <summary>
/// Which of a video's decoded frames go to the UI thread, when the UI shows at most so many a second (the pin's
/// PlaybackUiFps): every frame of a clip that plays no faster than that, an even share of a faster one. Shares fill at
/// the UI's rate, and a frame passes on one. A video pin measured instead how long ago the UI had last drawn, on
/// Environment.TickCount64, which moves in 15.6 ms steps: two frames 33 ms apart mostly read as 31 ms, under the 33 ms
/// allowed, so a 30 fps recording played in its pin at about 16 fps (194 of 367 frames shown).
/// </summary>
internal sealed class FramePresentationPacer
{
    // A frame late by up to half an interval keeps its share for the next one, which then comes that much sooner.
    private const double MaxShares = 1.5;

    // A frame up to a tenth of an interval early still passes: decode and timer wake-ups jitter by a millisecond or so.
    private const double Slack = 0.1;

    private readonly double _intervalTicks;
    private double _shares = MaxShares;
    private long _lastTicks;
    private bool _started;

    /// <param name="interval">The least time between frames the UI shows, on average.</param>
    public FramePresentationPacer(TimeSpan interval)
    {
        _intervalTicks = Math.Max(1, interval.TotalSeconds * Stopwatch.Frequency);
    }

    /// <summary>True when the frame decoded at <paramref name="nowTicks"/> (<see cref="Stopwatch"/> ticks) goes to the UI.</summary>
    public bool TryPass(long nowTicks)
    {
        if (_started)
        {
            _shares = Math.Min(MaxShares, _shares + ((nowTicks - _lastTicks) / _intervalTicks));
        }

        _started = true;
        _lastTicks = nowTicks;
        if (_shares < 1 - Slack)
        {
            return false;
        }

        _shares -= 1;
        return true;
    }
}
