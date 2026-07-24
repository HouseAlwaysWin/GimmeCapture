using System;

namespace GimmeCapture.Services.Core.Media;

/// <summary>
/// Sanity check for manual scrolling-capture placements: a frame-to-strip alignment is only trusted when it
/// agrees with the motion actually observed frame-to-frame since the last accepted placement.
/// <para>
/// Why: frame-to-strip matching sees the WHOLE strip, so on pages with repeated content (comment boxes,
/// banners, list rows) a frame can score an excellent match at a far-away — wrong — position. The observed
/// failure: while the user scrolled DOWN, two such matches PREPENDED big blocks to the top of the strip,
/// duplicating content. Score/ambiguity thresholds cannot catch this (one bad placement scored 0.028 — better
/// than many correct ones). Consecutive frames 40&#160;ms apart, however, overlap almost entirely, so their
/// relative shift is cheap and nearly unambiguous; accumulated, it predicts where the next accepted placement
/// must land. A placement that contradicts that dead-reckoned position by more than a tolerance is vetoed.
/// </para>
/// </summary>
internal static class ManualScrollMotionGate
{
    /// <summary>
    /// After this many consecutive vetoes the accumulated prediction itself is considered broken (drift, a
    /// mis-measured frame shift) and the caller should drop back to trusting the strip matcher, so a wrong
    /// prediction can never lock the capture out permanently.
    /// </summary>
    public const int MaxConsecutiveVetoes = 10;

    /// <summary>
    /// True when the candidate placement should be REJECTED: motion tracking is valid, and the jump the
    /// candidate implies from the last accepted placement disagrees with the accumulated frame-to-frame
    /// shift by more than <see cref="Tolerance"/>.
    /// </summary>
    /// <param name="motionValid">False when any frame-to-frame shift since the last accepted placement could
    /// not be measured — the prediction is incomplete, so nothing is vetoed (old behavior).</param>
    /// <param name="accumulatedShiftRows">Sum of measured frame-to-frame shifts since the last accepted
    /// placement (positive = scrolled down, matching strip-offset direction).</param>
    /// <param name="lastAcceptedOffset">Strip offset of the last accepted frame, in CURRENT strip
    /// coordinates (0 after a prepend, which rebases the strip).</param>
    /// <param name="candidateOffset">Strip offset the matcher proposes for the current frame.</param>
    /// <param name="frameHeight">Stitch-space frame height, used to scale the tolerance.</param>
    public static bool ShouldVeto(
        bool motionValid,
        long accumulatedShiftRows,
        int lastAcceptedOffset,
        int candidateOffset,
        int frameHeight)
    {
        if (!motionValid)
        {
            return false;
        }

        long implied = (long)candidateOffset - lastAcceptedOffset;
        return Math.Abs(implied - accumulatedShiftRows) > Tolerance(frameHeight);
    }

    /// <summary>
    /// Allowed disagreement between prediction and placement. A third of a frame absorbs per-frame
    /// measurement error accumulated over a realistic miss-run; the 120-row floor keeps small captures from
    /// vetoing ordinary jitter. The wrong placements this exists for are off by several hundred rows.
    /// </summary>
    public static int Tolerance(int frameHeight) => Math.Max(frameHeight / 3, 120);
}
