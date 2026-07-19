using System.Collections.Generic;
using Avalonia;
using GimmeCapture.Models;

namespace GimmeCapture.Services.Core.Interaction;

/// <summary>
/// Pure hit-testing over enumerated <see cref="WindowCandidate"/>s: picks the best candidate under the
/// pointer (child controls beat top-levels, then smaller area, then z-order) with hysteresis so the hover
/// target doesn't flicker at candidate edges. Extracted from the Windows detection service so both
/// platform services share it and tests can exercise it without Win32.
/// </summary>
public static class WindowCandidateHitTester
{
    private const double CandidateHysteresisPadding = 6.0;

    public static WindowCandidate? GetCandidateAtPoint(
        Point point, IReadOnlyList<WindowCandidate> candidates, WindowCandidate? previousCandidate = null)
    {
        if (candidates.Count == 0)
        {
            return null;
        }

        WindowCandidate? bestCandidate = null;
        WindowCandidate? previousMatch = null;
        for (int i = 0; i < candidates.Count; i++)
        {
            var candidate = candidates[i];
            if (!candidate.Bounds.Contains(point))
            {
                continue;
            }

            if (previousCandidate != null && candidate.IsSameIdentity(previousCandidate))
            {
                previousMatch = candidate;
            }

            if (bestCandidate == null || CompareCandidates(candidate, bestCandidate) < 0)
            {
                bestCandidate = candidate;
            }
        }

        if (bestCandidate == null)
        {
            if (previousCandidate != null && previousCandidate.Bounds.Inflate(CandidateHysteresisPadding).Contains(point))
            {
                return previousCandidate;
            }

            return null;
        }

        if (previousCandidate == null)
        {
            return bestCandidate;
        }

        if (previousMatch != null)
        {
            return ShouldHoldPreviousCandidate(point, previousMatch, bestCandidate)
                ? previousMatch
                : bestCandidate;
        }

        if (previousCandidate.Bounds.Inflate(CandidateHysteresisPadding).Contains(point) &&
            IsSameTree(previousCandidate, bestCandidate) &&
            !IsPointSafelyInside(point, bestCandidate.Bounds, CandidateHysteresisPadding))
        {
            return previousCandidate;
        }

        return bestCandidate;
    }

    private static bool ShouldHoldPreviousCandidate(Point point, WindowCandidate previousCandidate, WindowCandidate bestCandidate)
    {
        if (previousCandidate.IsSameIdentity(bestCandidate))
        {
            return true;
        }

        if (!IsSameTree(previousCandidate, bestCandidate))
        {
            return false;
        }

        if (previousCandidate.Bounds.Inflate(CandidateHysteresisPadding).Contains(point) &&
            !IsPointSafelyInside(point, bestCandidate.Bounds, CandidateHysteresisPadding))
        {
            return true;
        }

        return false;
    }

    private static bool IsSameTree(WindowCandidate left, WindowCandidate right)
    {
        return left.RootHwnd == right.RootHwnd;
    }

    private static bool IsPointSafelyInside(Point point, Rect bounds, double padding)
    {
        if (bounds.Width <= padding * 2 || bounds.Height <= padding * 2)
        {
            return bounds.Contains(point);
        }

        Rect insetBounds = new(
            bounds.X + padding,
            bounds.Y + padding,
            bounds.Width - (padding * 2),
            bounds.Height - (padding * 2));

        return insetBounds.Contains(point);
    }

    private static int CompareCandidates(WindowCandidate left, WindowCandidate right)
    {
        int childPriority = (left.IsChild ? 0 : 1).CompareTo(right.IsChild ? 0 : 1);
        if (childPriority != 0)
        {
            return childPriority;
        }

        int areaPriority = left.Area.CompareTo(right.Area);
        if (areaPriority != 0)
        {
            return areaPriority;
        }

        return left.ZOrder.CompareTo(right.ZOrder);
    }
}
