using System;
using System.Collections.Generic;
using Avalonia;
using GimmeCapture.Models;

namespace GimmeCapture.Services.Core.AI;

public sealed record OcrCandidateGroupingResult(
    IReadOnlyList<Rect> RawRects,
    IReadOnlyList<OcrCandidate> LineCandidates,
    IReadOnlyList<OcrCandidate> ParagraphCandidates)
{
    public IReadOnlyList<OcrCandidate> AllCandidates
    {
        get
        {
            var combined = new List<OcrCandidate>(ParagraphCandidates.Count + LineCandidates.Count);
            combined.AddRange(ParagraphCandidates);
            combined.AddRange(LineCandidates);
            return combined;
        }
    }
}

public static class OcrCandidateGrouper
{
    public static OcrCandidateGroupingResult Group(IReadOnlyList<Rect> rawRects)
    {
        if (rawRects.Count == 0)
        {
            return new OcrCandidateGroupingResult(Array.Empty<Rect>(), Array.Empty<OcrCandidate>(), Array.Empty<OcrCandidate>());
        }

        var normalizedRects = new List<Rect>(rawRects.Count);
        foreach (var rect in rawRects)
        {
            if (rect.Width >= 12 && rect.Height >= 8)
            {
                normalizedRects.Add(rect);
            }
        }

        normalizedRects.Sort(CompareByCenterYThenX);
        var lineGroups = BuildLineGroups(normalizedRects);
        var paragraphGroups = BuildParagraphGroups(lineGroups);

        var lineCandidates = new List<OcrCandidate>(lineGroups.Count);
        var paragraphCandidates = new List<OcrCandidate>(paragraphGroups.Count);

        for (int i = 0; i < paragraphGroups.Count; i++)
        {
            int groupId = i + 1;
            var paragraphGroup = paragraphGroups[i];
            paragraphCandidates.Add(new OcrCandidate(paragraphGroup.Bounds, OcrCandidateKind.Paragraph, groupId));

            foreach (var lineGroup in paragraphGroup.Lines)
            {
                lineCandidates.Add(new OcrCandidate(lineGroup.Bounds, OcrCandidateKind.Line, groupId));
            }
        }

        return new OcrCandidateGroupingResult(normalizedRects, lineCandidates, paragraphCandidates);
    }

    private static List<LineGroup> BuildLineGroups(List<Rect> rects)
    {
        var lineGroups = new List<LineGroup>();
        foreach (var rect in rects)
        {
            LineGroup? bestGroup = null;
            double bestScore = double.MaxValue;
            for (int i = 0; i < lineGroups.Count; i++)
            {
                var group = lineGroups[i];
                if (!CanJoinLine(group, rect))
                {
                    continue;
                }

                double score = Math.Abs(group.CenterY - GetCenterY(rect));
                if (score < bestScore)
                {
                    bestGroup = group;
                    bestScore = score;
                }
            }

            if (bestGroup == null)
            {
                lineGroups.Add(new LineGroup(rect));
            }
            else
            {
                bestGroup.Add(rect);
            }
        }

        lineGroups.Sort((left, right) => CompareByCenterYThenX(left.Bounds, right.Bounds));
        return lineGroups;
    }

    private static List<ParagraphGroup> BuildParagraphGroups(List<LineGroup> lineGroups)
    {
        var paragraphs = new List<ParagraphGroup>();
        foreach (var line in lineGroups)
        {
            ParagraphGroup? bestGroup = null;
            double bestScore = double.MaxValue;
            for (int i = 0; i < paragraphs.Count; i++)
            {
                var paragraph = paragraphs[i];
                if (!CanJoinParagraph(paragraph, line))
                {
                    continue;
                }

                double score = Math.Abs(paragraph.Bottom - line.Bounds.Top);
                if (score < bestScore)
                {
                    bestGroup = paragraph;
                    bestScore = score;
                }
            }

            if (bestGroup == null)
            {
                paragraphs.Add(new ParagraphGroup(line));
            }
            else
            {
                bestGroup.Add(line);
            }
        }

        paragraphs.Sort((left, right) => CompareByCenterYThenX(left.Bounds, right.Bounds));
        return paragraphs;
    }

    private static bool CanJoinLine(LineGroup group, Rect rect)
    {
        double centerYDiff = Math.Abs(group.CenterY - GetCenterY(rect));
        double maxHeight = Math.Max(group.AverageHeight, rect.Height);
        if (centerYDiff > maxHeight * 0.35)
        {
            return false;
        }

        double averageCharHeight = (group.AverageHeight + rect.Height) / 2.0;
        double maxHorizontalGap = Math.Max(averageCharHeight * 1.15, 12.0);
        double horizontalGap = rect.Left >= group.Bounds.Right
            ? rect.Left - group.Bounds.Right
            : group.Bounds.Left >= rect.Right
                ? group.Bounds.Left - rect.Right
                : 0;

        return horizontalGap <= maxHorizontalGap;
    }

    private static bool CanJoinParagraph(ParagraphGroup group, LineGroup line)
    {
        double verticalGap = line.Bounds.Top >= group.Bottom
            ? line.Bounds.Top - group.Bottom
            : group.Bounds.Top >= line.Bounds.Bottom
                ? group.Bounds.Top - line.Bounds.Bottom
                : 0;

        double maxHeight = Math.Max(group.AverageLineHeight, line.Bounds.Height);
        if (verticalGap > maxHeight * 0.55)
        {
            return false;
        }

        double overlapWidth = Math.Max(
            0,
            Math.Min(group.Bounds.Right, line.Bounds.Right) - Math.Max(group.Bounds.Left, line.Bounds.Left));
        double minWidth = Math.Min(group.Bounds.Width, line.Bounds.Width);
        if (minWidth <= 0)
        {
            return false;
        }

        double overlapRatio = overlapWidth / minWidth;
        double lineCenterX = line.Bounds.X + (line.Bounds.Width / 2.0);
        double horizontalTolerance = Math.Max(group.Bounds.Width * 0.15, 18.0);
        bool lineCenterAligned =
            lineCenterX >= (group.Bounds.Left - horizontalTolerance) &&
            lineCenterX <= (group.Bounds.Right + horizontalTolerance);

        return overlapRatio >= 0.55 && lineCenterAligned;
    }

    private static int CompareByCenterYThenX(Rect left, Rect right)
    {
        int centerCompare = GetCenterY(left).CompareTo(GetCenterY(right));
        if (centerCompare != 0)
        {
            return centerCompare;
        }

        return left.X.CompareTo(right.X);
    }

    private static double GetCenterY(Rect rect) => rect.Y + (rect.Height / 2.0);

    private sealed class LineGroup
    {
        private readonly List<Rect> _segments = new();

        public LineGroup(Rect rect)
        {
            Bounds = rect;
            AverageHeight = rect.Height;
            _segments.Add(rect);
        }

        public Rect Bounds { get; private set; }
        public double AverageHeight { get; private set; }
        public double CenterY => GetCenterY(Bounds);
        public IReadOnlyList<Rect> Segments => _segments;

        public void Add(Rect rect)
        {
            _segments.Add(rect);
            Bounds = Bounds.Union(rect);
            AverageHeight = ((AverageHeight * (_segments.Count - 1)) + rect.Height) / _segments.Count;
        }
    }

    private sealed class ParagraphGroup
    {
        public ParagraphGroup(LineGroup firstLine)
        {
            Bounds = firstLine.Bounds;
            AverageLineHeight = firstLine.Bounds.Height;
            Lines = new List<LineGroup> { firstLine };
        }

        public Rect Bounds { get; private set; }
        public double AverageLineHeight { get; private set; }
        public double Bottom => Bounds.Bottom;
        public List<LineGroup> Lines { get; }

        public void Add(LineGroup line)
        {
            Bounds = Bounds.Union(line.Bounds);
            AverageLineHeight = ((AverageLineHeight * Lines.Count) + line.Bounds.Height) / (Lines.Count + 1);
            Lines.Add(line);
        }
    }
}
