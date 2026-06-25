using System;
using System.Collections.Generic;
using GimmeCapture.Models;

namespace GimmeCapture.Services.Core.Rendering;

/// <summary>
/// Pure (UI-free, unit-testable) sampling of a <see cref="RedactionTrack"/>: given a time, returns the
/// tracked object's interpolated normalized box. Keyframes are treated as sorted by time; before the
/// first / after the last keyframe the box is held (clamped, no extrapolation), and between two
/// keyframes each box edge is linearly interpolated. This is the core of the Tier-2a "keyframe +
/// interpolation" object tracking — the export hook calls it per frame to position the blur/mosaic box.
/// </summary>
public static class RedactionInterpolator
{
    /// <summary>
    /// Evaluates the tracked box at <paramref name="timeSeconds"/>. Returns null when the track has no
    /// keyframes. A single keyframe yields its box at every time.
    /// </summary>
    public static RedactionBox? EvaluateAt(RedactionTrack track, double timeSeconds)
    {
        ArgumentNullException.ThrowIfNull(track);

        IReadOnlyList<RedactionKeyframe> ordered = OrderedKeyframes(track.Keyframes);
        if (ordered.Count == 0)
        {
            return null;
        }

        if (timeSeconds <= ordered[0].TimeSeconds)
        {
            return ToBox(ordered[0]);
        }

        RedactionKeyframe last = ordered[ordered.Count - 1];
        if (timeSeconds >= last.TimeSeconds)
        {
            return ToBox(last);
        }

        // Find the bracketing pair [a, b] with a.Time <= t < b.Time and lerp each edge.
        for (int i = 0; i < ordered.Count - 1; i++)
        {
            RedactionKeyframe a = ordered[i];
            RedactionKeyframe b = ordered[i + 1];
            if (timeSeconds < b.TimeSeconds)
            {
                double span = b.TimeSeconds - a.TimeSeconds;
                double f = span <= 0 ? 0 : (timeSeconds - a.TimeSeconds) / span;
                return new RedactionBox(
                    Lerp(a.X, b.X, f),
                    Lerp(a.Y, b.Y, f),
                    Lerp(a.Width, b.Width, f),
                    Lerp(a.Height, b.Height, f));
            }
        }

        // Unreachable (t is strictly between first and last), but hold the last box defensively.
        return ToBox(last);
    }

    private static IReadOnlyList<RedactionKeyframe> OrderedKeyframes(List<RedactionKeyframe> keyframes)
    {
        // Author order is usually already chronological; only pay for a sort when it isn't, and never
        // mutate the caller's list (Evaluate must be side-effect free).
        bool sorted = true;
        for (int i = 1; i < keyframes.Count; i++)
        {
            if (keyframes[i].TimeSeconds < keyframes[i - 1].TimeSeconds)
            {
                sorted = false;
                break;
            }
        }

        if (sorted)
        {
            return keyframes;
        }

        var copy = new List<RedactionKeyframe>(keyframes);
        copy.Sort(static (a, b) => a.TimeSeconds.CompareTo(b.TimeSeconds));
        return copy;
    }

    private static RedactionBox ToBox(RedactionKeyframe k) => new(k.X, k.Y, k.Width, k.Height);

    private static double Lerp(double a, double b, double f) => a + ((b - a) * f);
}
