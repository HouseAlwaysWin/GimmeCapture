using System;
using SkiaSharp;

namespace GimmeCapture.Services.Core.Media;

/// <summary>
/// Tells which way a manual scrolling capture scrolls (the Auto direction), from its first frame and each later one.
/// <para>
/// For each axis it measures how far the content moved since the first frame, as the stitcher does, over the middle
/// half of the frame across that axis: a chat's title bar and message box, or a sidebar, stay put at the edges and
/// pull toward no motion. An axis counts when the content moved at least <see cref="MinShiftRows"/> along it; when both
/// do, the one that matched clearly better (by <see cref="ScoreMargin"/>), and when neither does, nothing is decided.
/// The same axis must win <see cref="VotesToLock"/> frames in a row, the other axis winning resets the count.
/// </para>
/// <para>
/// It used to lock onto whichever axis first showed the first frame's edge overhung by 8 rows, matching whole frames
/// with the strip matcher's loose thresholds. A user's LINE capture locked sideways and placed none of its 674 frames.
/// In a simulated LINE chat scrolled up under its title bar and floating date (<c>ScrollCaptureSimulationProbeTests</c>,
/// <c>AxisReport</c>), the old way found no match down the frame, while turned sideways the mostly empty chat matched
/// itself 489 of its 545 columns over: it locked sideways at the first frame. With a message box as well it never
/// locked at all.
/// </para>
/// </summary>
internal sealed class ManualScrollAxisDetector
{
    /// <summary>The least motion along an axis that counts (as the stitcher's calibration).</summary>
    internal const int MinShiftRows = 8;

    /// <summary>How much better one axis must match than the other when the content seems to move along both.</summary>
    internal const double ScoreMargin = 0.02;

    /// <summary>Frames in a row that must agree on the axis before it is locked.</summary>
    internal const int VotesToLock = 2;

    private readonly int _minOverlapVertical;
    private readonly int _ignoreRightVertical;
    private readonly int _minOverlapHorizontal;
    private readonly int _ignoreRightHorizontal;
    private readonly double _rowTolerance;

    private bool? _leading;
    private int _votes;

    /// <param name="minOverlapVertical">The stitcher's least overlap for a vertical scroll (rows of the region).</param>
    /// <param name="ignoreRightVertical">Columns at the region's right left out of matching a vertical scroll.</param>
    /// <param name="minOverlapHorizontal">The least overlap for a horizontal scroll (columns of the region).</param>
    /// <param name="ignoreRightHorizontal">Rows at the region's bottom left out of matching a horizontal scroll.</param>
    /// <param name="rowMismatchTolerance">The share of overlapping rows that may differ.</param>
    public ManualScrollAxisDetector(
        int minOverlapVertical, int ignoreRightVertical, int minOverlapHorizontal, int ignoreRightHorizontal, double rowMismatchTolerance)
    {
        _minOverlapVertical = Math.Max(1, minOverlapVertical);
        _ignoreRightVertical = Math.Max(0, ignoreRightVertical);
        _minOverlapHorizontal = Math.Max(1, minOverlapHorizontal);
        _ignoreRightHorizontal = Math.Max(0, ignoreRightHorizontal);
        _rowTolerance = rowMismatchTolerance;
    }

    /// <summary>
    /// The axis once decided — <c>false</c> vertical, <c>true</c> horizontal — or <c>null</c> while it is not. Both
    /// frames are as captured (screen orientation); neither is kept.
    /// </summary>
    public bool? Decide(SKBitmap first, SKBitmap frame)
    {
        ArgumentNullException.ThrowIfNull(first);
        ArgumentNullException.ThrowIfNull(frame);
        if (first.Width != frame.Width || first.Height != frame.Height)
        {
            return null;
        }

        (int down, double downScore) = Shift(first, frame, _minOverlapVertical, _ignoreRightVertical);
        int across;
        double acrossScore;
        using (SKBitmap firstTurned = ScrollStitcher.RotateCcw90(first))
        using (SKBitmap frameTurned = ScrollStitcher.RotateCcw90(frame))
        {
            (across, acrossScore) = Shift(firstTurned, frameTurned, _minOverlapHorizontal, _ignoreRightHorizontal);
        }

        bool? vote = Vote(Math.Abs(down) >= MinShiftRows, downScore, Math.Abs(across) >= MinShiftRows, acrossScore);
        if (vote == null)
        {
            return null; // nothing moved enough, or both did about as well: wait, keeping the count
        }

        _votes = vote == _leading ? _votes + 1 : 1;
        _leading = vote;
        return _votes >= VotesToLock ? vote : null;
    }

    private static bool? Vote(bool down, double downScore, bool across, double acrossScore)
    {
        if (down && !across)
        {
            return false;
        }

        if (across && !down)
        {
            return true;
        }

        if (down && across)
        {
            if (downScore + ScoreMargin < acrossScore)
            {
                return false;
            }

            if (acrossScore + ScoreMargin < downScore)
            {
                return true;
            }
        }

        return null;
    }

    // How far the content moved down the frame since the first one (rows, 0 when it cannot tell), and how well that
    // matched: over the middle half, then the whole frame for a motion too big for the middle.
    private (int Rows, double Score) Shift(SKBitmap first, SKBitmap frame, int minOverlap, int ignoreRight)
    {
        int height = first.Height;
        int quarter = height / 4;
        if (height >= 64)
        {
            using SKBitmap firstMiddle = ScrollStitcher.RowsOf(first, quarter, height - (2 * quarter));
            using SKBitmap frameMiddle = ScrollStitcher.RowsOf(frame, quarter, height - (2 * quarter));
            ScrollStitcher.VerticalShift middle = ScrollStitcher.FindVerticalShift(
                firstMiddle, frameMiddle, Math.Min(minOverlap, firstMiddle.Height), ignoreRight, _rowTolerance, out double middleScore);
            if (middle.Found)
            {
                return (middle.Rows, middleScore);
            }
        }

        ScrollStitcher.VerticalShift whole = ScrollStitcher.FindVerticalShift(
            first, frame, minOverlap, ignoreRight, _rowTolerance, out double wholeScore);
        return whole.Found ? (whole.Rows, wholeScore) : (0, double.MaxValue);
    }
}
