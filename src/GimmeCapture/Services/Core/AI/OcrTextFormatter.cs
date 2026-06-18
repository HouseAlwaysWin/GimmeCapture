using System;
using System.Collections.Generic;
using GimmeCapture.Models;
using SkiaSharp;

namespace GimmeCapture.Services.Core.AI;

public sealed record OcrTextFragment(SKRectI Bounds, string Text);

public static class OcrTextFormatter
{
    public static string Format(IEnumerable<OcrTextFragment> fragments, OcrTextLayout layout)
    {
        ArgumentNullException.ThrowIfNull(fragments);

        var items = new List<OcrTextFragment>();
        foreach (var fragment in fragments)
        {
            if (!string.IsNullOrWhiteSpace(fragment.Text))
            {
                items.Add(fragment);
            }
        }

        items.Sort(static (left, right) =>
        {
            int vertical = left.Bounds.Top.CompareTo(right.Bounds.Top);
            return vertical != 0 ? vertical : left.Bounds.Left.CompareTo(right.Bounds.Left);
        });

        if (items.Count == 0)
        {
            return string.Empty;
        }

        if (layout == OcrTextLayout.SingleLine)
        {
            var text = new string[items.Count];
            for (int i = 0; i < items.Count; i++)
            {
                text[i] = items[i].Text.Trim();
            }

            return string.Join(" ", text);
        }

        var lines = new List<List<OcrTextFragment>>();
        foreach (var item in items)
        {
            List<OcrTextFragment>? matchingLine = null;
            foreach (var line in lines.AsValueEnumerable())
            {
                double centerSum = 0;
                double heightSum = 0;
                foreach (var existing in line)
                {
                    centerSum += existing.Bounds.Top + (existing.Bounds.Height / 2d);
                    heightSum += existing.Bounds.Height;
                }

                double averageCenterY = centerSum / line.Count;
                double itemCenterY = item.Bounds.Top + (item.Bounds.Height / 2d);
                double averageHeight = heightSum / line.Count;
                if (Math.Abs(itemCenterY - averageCenterY) <= Math.Max(4, averageHeight * 0.6))
                {
                    matchingLine = line;
                    break;
                }
            }

            if (matchingLine == null)
            {
                matchingLine = [];
                lines.Add(matchingLine);
            }

            matchingLine.Add(item);
        }

        lines.Sort(static (left, right) =>
        {
            int leftTop = left.Count == 0 ? 0 : left[0].Bounds.Top;
            int rightTop = right.Count == 0 ? 0 : right[0].Bounds.Top;
            return leftTop.CompareTo(rightTop);
        });

        var lineTexts = new string[lines.Count];
        for (int lineIndex = 0; lineIndex < lines.Count; lineIndex++)
        {
            var line = lines[lineIndex];
            line.Sort(static (left, right) => left.Bounds.Left.CompareTo(right.Bounds.Left));
            var words = new string[line.Count];
            for (int wordIndex = 0; wordIndex < line.Count; wordIndex++)
            {
                words[wordIndex] = line[wordIndex].Text.Trim();
            }

            lineTexts[lineIndex] = string.Join(" ", words);
        }

        return string.Join(Environment.NewLine, lineTexts);
    }
}
