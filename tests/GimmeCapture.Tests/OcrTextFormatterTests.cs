using System;
using System.Collections.Generic;
using GimmeCapture.Models;
using GimmeCapture.Services.Core.AI;
using SkiaSharp;
using Xunit;

namespace GimmeCapture.Tests;

// OcrTextFormatter.Format turns recognized boxes into the copied string: sorts top→bottom / left→right, and in
// PreserveLines groups boxes onto the same line when their vertical centres are within Math.Max(4, avgHeight*0.6).
// Pure geometry/string logic — no engine or UI thread. These guard the line-grouping threshold whose off-by-one
// silently garbles multi-line copies.
public class OcrTextFormatterTests
{
    private static OcrTextFragment Frag(int left, int top, int right, int bottom, string text)
        => new(new SKRectI(left, top, right, bottom), text);

    [Fact]
    public void Format_EmptyInput_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, OcrTextFormatter.Format(Array.Empty<OcrTextFragment>(), OcrTextLayout.PreserveLines));
        Assert.Equal(string.Empty, OcrTextFormatter.Format(Array.Empty<OcrTextFragment>(), OcrTextLayout.SingleLine));
    }

    [Fact]
    public void Format_WhitespaceOnlyFragments_AreDropped()
    {
        var frags = new[] { Frag(0, 0, 10, 20, "   "), Frag(0, 30, 10, 50, "\t") };
        Assert.Equal(string.Empty, OcrTextFormatter.Format(frags, OcrTextLayout.PreserveLines));
    }

    [Fact]
    public void Format_SingleLine_JoinsAllWithSpace_SortedByTopThenLeft_AndTrims()
    {
        // Intentionally out of order; expect sort by Top then Left, each token trimmed.
        var frags = new[]
        {
            Frag(0, 100, 50, 120, "  third  "),
            Frag(60, 0, 110, 20, "second"),
            Frag(0, 0, 50, 20, "first"),
        };

        Assert.Equal("first second third", OcrTextFormatter.Format(frags, OcrTextLayout.SingleLine));
    }

    [Fact]
    public void Format_PreserveLines_GroupsSameRow_AndSeparatesRowsWithNewline()
    {
        // A and B share a row (centres 10 vs 12, within max(4, 20*0.6=12)); C is far below → its own line.
        var frags = new[]
        {
            Frag(0, 0, 50, 20, "Hello"),
            Frag(60, 2, 110, 22, "World"),
            Frag(0, 100, 50, 120, "Second"),
        };

        Assert.Equal($"Hello World{Environment.NewLine}Second",
            OcrTextFormatter.Format(frags, OcrTextLayout.PreserveLines));
    }

    [Fact]
    public void Format_PreserveLines_OrdersWordsLeftToRightWithinARow()
    {
        var frags = new[]
        {
            Frag(200, 0, 250, 20, "C"),
            Frag(0, 0, 50, 20, "A"),
            Frag(100, 1, 150, 21, "B"),
        };

        Assert.Equal("A B C", OcrTextFormatter.Format(frags, OcrTextLayout.PreserveLines));
    }

    [Fact]
    public void Format_PreserveLines_CentreDeltaBeyondThreshold_SplitsToSeparateLines()
    {
        // Height 20 → threshold max(4, 12) = 12. Centres 10 and 25 differ by 15 (> 12) → two lines.
        var frags = new[]
        {
            Frag(0, 0, 50, 20, "top"),
            Frag(0, 15, 50, 35, "bottom"),
        };

        Assert.Equal($"top{Environment.NewLine}bottom",
            OcrTextFormatter.Format(frags, OcrTextLayout.PreserveLines));
    }
}
