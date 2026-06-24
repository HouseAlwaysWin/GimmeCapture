using System;
using System.Collections.Generic;
using GimmeCapture.Models;

namespace GimmeCapture.Services.Core.Media;

/// <summary>
/// Pure editing math for a list of kept <see cref="VideoEditSegment"/>s — split, delete, and the
/// OUTPUT↔SOURCE time mapping the timeline UI, export, and gap-skipping preview all share. No
/// ffmpeg, no UI, so it is fully unit-testable (mirrors <see cref="ScrollStitcher"/>'s style).
///
/// "Output time" is the position on the edited timeline (segments laid end to end, honoring each
/// segment's <see cref="VideoEditSegment.Speed"/>); "source time" is the position in the original
/// file. The gaps between consecutive segments are the parts that were cut out.
/// </summary>
public static class VideoSegmentEditor
{
    // Two output times closer than this are treated as the same point (avoids zero-length splits).
    private const double Epsilon = 1e-3;

    /// <summary>A single full-clip (or single head/tail trim) edit: one segment over the kept range.</summary>
    public static IReadOnlyList<VideoEditSegment> FromTrim(double trimStart, double trimEnd, double totalDuration)
    {
        double start = Math.Max(0, trimStart);
        double end = trimEnd > start ? trimEnd : Math.Max(start, totalDuration);
        return new[] { new VideoEditSegment(start, end) };
    }

    /// <summary>
    /// The OUTPUT-time start of the segment at <paramref name="index"/> (sum of the prior segments'
    /// output durations). Returns the total output duration when <paramref name="index"/> == Count.
    /// </summary>
    public static double SegmentOutputStart(IReadOnlyList<VideoEditSegment> segments, int index)
    {
        ArgumentNullException.ThrowIfNull(segments);
        double start = 0;
        int upto = Math.Clamp(index, 0, segments.Count);
        for (int i = 0; i < upto; i++)
        {
            start += segments[i].OutputDuration;
        }

        return start;
    }

    /// <summary>
    /// Maps an OUTPUT time to the owning segment index and the corresponding SOURCE time (honoring
    /// the segment's speed). Returns false when the list is empty. Output times past the end clamp to
    /// the last segment's end; negative times clamp to the first segment's start.
    /// </summary>
    public static bool TryMapOutputToSource(
        IReadOnlyList<VideoEditSegment> segments,
        double outputTime,
        out int index,
        out double sourceTime)
    {
        ArgumentNullException.ThrowIfNull(segments);
        index = 0;
        sourceTime = 0;
        if (segments.Count == 0)
        {
            return false;
        }

        if (outputTime <= 0)
        {
            sourceTime = segments[0].SourceStart;
            return true;
        }

        double cursor = 0;
        for (int i = 0; i < segments.Count; i++)
        {
            VideoEditSegment s = segments[i];
            double outDur = s.OutputDuration;
            if (outputTime < cursor + outDur || i == segments.Count - 1)
            {
                index = i;
                double outOffset = Math.Clamp(outputTime - cursor, 0, outDur);
                double speed = s.Speed > 0 ? s.Speed : 1.0;
                sourceTime = Math.Min(s.SourceEnd, s.SourceStart + (outOffset * speed));
                return true;
            }

            cursor += outDur;
        }

        // Unreachable (loop returns on the last segment), but keep the compiler happy.
        index = segments.Count - 1;
        sourceTime = segments[^1].SourceEnd;
        return true;
    }

    /// <summary>
    /// Maps a SOURCE time to its position on the OUTPUT timeline (for placing the playhead on the
    /// segment strip). A source time inside a kept segment maps within that segment; a source time in
    /// a cut gap snaps to the nearest segment boundary; before/after everything clamps to 0 / total
    /// output. Returns false when the list is empty.
    /// </summary>
    public static bool TryMapSourceToOutput(
        IReadOnlyList<VideoEditSegment> segments,
        double sourceTime,
        out double outputTime)
    {
        ArgumentNullException.ThrowIfNull(segments);
        outputTime = 0;
        if (segments.Count == 0)
        {
            return false;
        }

        double cursor = 0;
        for (int i = 0; i < segments.Count; i++)
        {
            VideoEditSegment s = segments[i];
            double outDur = s.OutputDuration;
            if (sourceTime < s.SourceStart)
            {
                // In the gap before this segment (or before the first) -> snap to its output start.
                outputTime = cursor;
                return true;
            }

            if (sourceTime <= s.SourceEnd)
            {
                double speed = s.Speed > 0 ? s.Speed : 1.0;
                outputTime = cursor + ((sourceTime - s.SourceStart) / speed);
                return true;
            }

            cursor += outDur;
        }

        // Past the last segment -> end of the output.
        outputTime = cursor;
        return true;
    }

    /// <summary>
    /// The index of the kept segment "at" <paramref name="sourceTime"/> — the one containing it, or the
    /// next kept segment when the time falls in a cut gap (clamped to the ends). Returns -1 for an empty
    /// list. Used to auto-select the segment under the playhead so Delete removes the right one.
    /// </summary>
    public static int IndexForSourceTime(IReadOnlyList<VideoEditSegment> segments, double sourceTime)
    {
        ArgumentNullException.ThrowIfNull(segments);
        if (segments.Count == 0)
        {
            return -1;
        }

        for (int i = 0; i < segments.Count; i++)
        {
            VideoEditSegment s = segments[i];
            if (sourceTime < s.SourceStart || sourceTime <= s.SourceEnd)
            {
                return i;
            }
        }

        return segments.Count - 1;
    }

    /// <summary>
    /// Splits the segment containing <paramref name="outputTime"/> into two at that point. Returns a
    /// new list; the original is unchanged. A split too close to a segment boundary (or outside the
    /// output) is a no-op and returns an equivalent copy.
    /// </summary>
    public static IReadOnlyList<VideoEditSegment> SplitAtOutputTime(
        IReadOnlyList<VideoEditSegment> segments,
        double outputTime)
    {
        ArgumentNullException.ThrowIfNull(segments);
        var result = new List<VideoEditSegment>(segments.Count + 1);
        double cursor = 0;
        bool split = false;
        foreach (VideoEditSegment s in segments)
        {
            double outDur = s.OutputDuration;
            double localOffset = outputTime - cursor;
            if (!split && localOffset > Epsilon && localOffset < outDur - Epsilon)
            {
                double speed = s.Speed > 0 ? s.Speed : 1.0;
                double sourceSplit = s.SourceStart + (localOffset * speed);
                result.Add(s with { SourceEnd = sourceSplit });
                result.Add(s with { SourceStart = sourceSplit });
                split = true;
            }
            else
            {
                result.Add(s);
            }

            cursor += outDur;
        }

        return result;
    }

    /// <summary>
    /// Splits the segment containing <paramref name="sourceTime"/> (in SOURCE seconds) into two at
    /// that point. Returns a new list; the original is unchanged. A split at/near a segment boundary,
    /// or inside a cut gap, is a no-op and returns an equivalent copy. This is the natural operation
    /// for a "split at playhead" action, because the player tracks the source position directly.
    /// </summary>
    public static IReadOnlyList<VideoEditSegment> SplitAtSourceTime(
        IReadOnlyList<VideoEditSegment> segments,
        double sourceTime)
    {
        ArgumentNullException.ThrowIfNull(segments);
        var result = new List<VideoEditSegment>(segments.Count + 1);
        bool split = false;
        foreach (VideoEditSegment s in segments)
        {
            if (!split && sourceTime > s.SourceStart + Epsilon && sourceTime < s.SourceEnd - Epsilon)
            {
                result.Add(s with { SourceEnd = sourceTime });
                result.Add(s with { SourceStart = sourceTime });
                split = true;
            }
            else
            {
                result.Add(s);
            }
        }

        return result;
    }

    /// <summary>
    /// Merges adjacent segments that are contiguous in source (the next starts where the previous ends)
    /// and share the same speed into a single run. Used so a kept set that forms one continuous range
    /// (e.g. dropping only the head or tail) collapses to ONE segment — letting the export take the fast,
    /// lossless single-trim path instead of the heavier trim+concat graph. A genuine cut (a gap between
    /// kept pieces) is preserved as separate runs. The input list is unchanged; <c>Kept</c> and other
    /// fields are inherited from the first piece of each run.
    /// </summary>
    public static IReadOnlyList<VideoEditSegment> CoalesceContiguous(IReadOnlyList<VideoEditSegment> segments)
    {
        ArgumentNullException.ThrowIfNull(segments);
        var result = new List<VideoEditSegment>(segments.Count);
        foreach (VideoEditSegment s in segments)
        {
            if (result.Count > 0)
            {
                VideoEditSegment prev = result[^1];
                bool contiguous = Math.Abs(s.SourceStart - prev.SourceEnd) <= Epsilon;
                bool sameSpeed = Math.Abs(EffectiveSpeed(s) - EffectiveSpeed(prev)) <= Epsilon;
                if (contiguous && sameSpeed)
                {
                    result[^1] = prev with { SourceEnd = s.SourceEnd };
                    continue;
                }
            }

            result.Add(s);
        }

        return result;
    }

    private static double EffectiveSpeed(VideoEditSegment s) => s.Speed > 0 ? s.Speed : 1.0;

    /// <summary>
    /// Removes the segment at <paramref name="index"/> (the gap it leaves is the cut). The last
    /// remaining segment is never removed (the edit must keep at least one segment); returns an
    /// equivalent copy in that case.
    /// </summary>
    public static IReadOnlyList<VideoEditSegment> RemoveAt(IReadOnlyList<VideoEditSegment> segments, int index)
    {
        ArgumentNullException.ThrowIfNull(segments);
        if (index < 0 || index >= segments.Count || segments.Count <= 1)
        {
            return new List<VideoEditSegment>(segments);
        }

        var result = new List<VideoEditSegment>(segments.Count - 1);
        for (int i = 0; i < segments.Count; i++)
        {
            if (i != index)
            {
                result.Add(segments[i]);
            }
        }

        return result;
    }
}
