using Avalonia;
using GimmeCapture.Models;
using GimmeCapture.Services.Core.AI;
using GimmeCapture.Services.Core.Interaction;

namespace GimmeCapture.Tests;

public class OcrCandidateGrouperTests
{
    [Fact]
    public void Group_MergesAdjacentRectsIntoSingleLine()
    {
        Rect[] rawRects =
        [
            new Rect(10, 10, 40, 18),
            new Rect(58, 12, 42, 16),
            new Rect(108, 11, 44, 18)
        ];

        var result = OcrCandidateGrouper.Group(rawRects);

        Assert.Single(result.LineCandidates);
        Assert.Single(result.ParagraphCandidates);
    }

    [Fact]
    public void Group_MergesMultipleLinesIntoSingleParagraph()
    {
        Rect[] rawRects =
        [
            new Rect(20, 20, 90, 20),
            new Rect(22, 48, 88, 18),
            new Rect(24, 75, 86, 20)
        ];

        var result = OcrCandidateGrouper.Group(rawRects);

        Assert.Equal(3, result.LineCandidates.Count);
        Assert.Single(result.ParagraphCandidates);
    }

    [Fact]
    public void Group_DoesNotMergeDistantLinesIntoParagraph()
    {
        Rect[] rawRects =
        [
            new Rect(20, 20, 90, 20),
            new Rect(24, 95, 86, 20)
        ];

        var result = OcrCandidateGrouper.Group(rawRects);

        Assert.Equal(2, result.LineCandidates.Count);
        Assert.Equal(2, result.ParagraphCandidates.Count);
    }

    [Fact]
    public void OcrHitTester_PrefersLineWhenPointIsSafelyInsideLine()
    {
        var paragraph = new OcrCandidate(new Rect(20, 20, 220, 70), OcrCandidateKind.Paragraph, 1);
        var line = new OcrCandidate(new Rect(30, 30, 180, 18), OcrCandidateKind.Line, 1);

        var result = OcrCandidateHitTester.SelectCandidate(
            new Point(60, 38),
            [paragraph],
            [line]);

        Assert.NotNull(result);
        Assert.Equal(OcrCandidateKind.Line, result!.Kind);
    }

    [Fact]
    public void OcrHitTester_HoldsPreviousCandidateNearBoundary()
    {
        var paragraph = new OcrCandidate(new Rect(20, 20, 220, 70), OcrCandidateKind.Paragraph, 1);
        var line = new OcrCandidate(new Rect(30, 30, 180, 18), OcrCandidateKind.Line, 1);

        var result = OcrCandidateHitTester.SelectCandidate(
            new Point(33, 38),
            [paragraph],
            [line],
            paragraph);

        Assert.NotNull(result);
        Assert.Equal(OcrCandidateKind.Paragraph, result!.Kind);
    }
}
