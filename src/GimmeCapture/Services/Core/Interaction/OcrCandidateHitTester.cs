using System;
using System.Collections.Generic;
using Avalonia;
using GimmeCapture.Models;

namespace GimmeCapture.Services.Core.Interaction;

public static class OcrCandidateHitTester
{
    private const double CandidateHysteresisPadding = 6.0;

    public static OcrCandidate? SelectCandidate(
        Point point,
        IReadOnlyList<OcrCandidate> paragraphCandidates,
        IReadOnlyList<OcrCandidate> lineCandidates,
        OcrCandidate? previousCandidate = null)
    {
        OcrCandidate? paragraph = FindContainingCandidate(point, paragraphCandidates);
        OcrCandidate? line = FindContainingCandidate(point, lineCandidates);

        if (paragraph == null && line == null)
        {
            if (previousCandidate != null && previousCandidate.Bounds.Inflate(CandidateHysteresisPadding).Contains(point))
            {
                return previousCandidate;
            }

            return null;
        }

        OcrCandidate? best = ResolvePreferredCandidate(point, paragraph, line, lineCandidates);
        if (best == null || previousCandidate == null)
        {
            return best;
        }

        if (previousCandidate.IsSameIdentity(best))
        {
            return previousCandidate;
        }

        if (previousCandidate.GroupId == best.GroupId &&
            previousCandidate.Bounds.Inflate(CandidateHysteresisPadding).Contains(point) &&
            !IsPointSafelyInside(point, best.Bounds, CandidateHysteresisPadding))
        {
            return previousCandidate;
        }

        return best;
    }

    private static OcrCandidate? ResolvePreferredCandidate(
        Point point,
        OcrCandidate? paragraph,
        OcrCandidate? line,
        IReadOnlyList<OcrCandidate> lineCandidates)
    {
        if (line == null)
        {
            return paragraph;
        }

        if (paragraph == null)
        {
            return line;
        }

        int linesInParagraph = 0;
        for (int i = 0; i < lineCandidates.Count; i++)
        {
            if (lineCandidates[i].GroupId == paragraph.GroupId)
            {
                linesInParagraph++;
            }
        }

        if (linesInParagraph <= 1 || IsPointSafelyInside(point, line.Bounds, CandidateHysteresisPadding))
        {
            return line;
        }

        return paragraph;
    }

    private static OcrCandidate? FindContainingCandidate(Point point, IReadOnlyList<OcrCandidate> candidates)
    {
        OcrCandidate? best = null;
        for (int i = 0; i < candidates.Count; i++)
        {
            var candidate = candidates[i];
            if (!candidate.Bounds.Contains(point))
            {
                continue;
            }

            if (best == null || candidate.Area < best.Area)
            {
                best = candidate;
            }
        }

        return best;
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
}
