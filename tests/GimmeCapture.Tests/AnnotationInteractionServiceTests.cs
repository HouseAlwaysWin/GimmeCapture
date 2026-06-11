using Avalonia;
using GimmeCapture.Models;
using GimmeCapture.Services.Core;

namespace GimmeCapture.Tests;

public class AnnotationInteractionServiceTests
{
    [Fact]
    public void HitTest_PrefersSelectedHandleAndTopmostBody()
    {
        var bottom = CreateArea(10, 10, 80, 80);
        var top = CreateArea(20, 20, 60, 60);
        bottom.IsSelected = true;
        var annotations = new List<Annotation> { bottom, top };

        var handleHit = AnnotationInteractionService.HitTest(
            annotations,
            bottom,
            new Point(10, 10));
        var bodyHit = AnnotationInteractionService.HitTest(
            annotations,
            bottom,
            new Point(40, 40));

        Assert.Same(bottom, handleHit.Annotation);
        Assert.Equal(AnnotationHitZone.TopLeft, handleHit.Zone);
        Assert.Same(top, bodyHit.Annotation);
        Assert.Equal(AnnotationHitZone.Body, bodyHit.Zone);
    }

    [Fact]
    public void HitTest_LineEndpointsAndBody_AreEditableButPenIsIgnored()
    {
        var line = new Annotation
        {
            Type = AnnotationType.Arrow,
            StartPoint = new Point(10, 10),
            EndPoint = new Point(90, 10),
            Thickness = 2,
            IsSelected = true
        };
        var pen = new Annotation { Type = AnnotationType.Pen };
        pen.AddPoint(new Point(0, 0));
        pen.AddPoint(new Point(100, 100));
        var annotations = new List<Annotation> { line, pen };

        Assert.Equal(
            AnnotationHitZone.StartPoint,
            AnnotationInteractionService.HitTest(annotations, line, new Point(10, 10)).Zone);
        Assert.Equal(
            AnnotationHitZone.Body,
            AnnotationInteractionService.HitTest(annotations, line, new Point(50, 12)).Zone);
        Assert.False(
            AnnotationInteractionService.HitTest([pen], null, new Point(50, 50)).IsHit);
    }

    [Fact]
    public void ApplyTransform_ResizesNormalizedAreaAndEnforcesMinimumSize()
    {
        var annotation = CreateArea(80, 70, 20, 10);
        var before = AnnotationSnapshot.Capture(annotation);

        AnnotationInteractionService.ApplyTransform(
            annotation,
            before,
            AnnotationHitZone.Left,
            new Point(20, 40),
            new Point(78, 40),
            new Size(100, 100));

        var bounds = AnnotationInteractionService.GetNormalizedBounds(annotation);
        Assert.Equal(72, bounds.Left);
        Assert.Equal(80, bounds.Right);
        Assert.Equal(8, bounds.Width);
        Assert.Equal(10, bounds.Top);
        Assert.Equal(70, bounds.Bottom);
    }

    [Fact]
    public void ApplyTransform_MovesLineWithoutLeavingCanvas()
    {
        var annotation = new Annotation
        {
            Type = AnnotationType.Line,
            StartPoint = new Point(70, 70),
            EndPoint = new Point(90, 90)
        };
        var before = AnnotationSnapshot.Capture(annotation);

        AnnotationInteractionService.ApplyTransform(
            annotation,
            before,
            AnnotationHitZone.Body,
            new Point(80, 80),
            new Point(130, 140),
            new Size(100, 100));

        Assert.Equal(new Point(80, 80), annotation.StartPoint);
        Assert.Equal(new Point(100, 100), annotation.EndPoint);
    }

    [Fact]
    public void ApplyTransform_AllowsAreaHandleToCrossOppositeEdge()
    {
        var annotation = CreateArea(20, 20, 60, 60);
        var before = AnnotationSnapshot.Capture(annotation);

        AnnotationInteractionService.ApplyTransform(
            annotation,
            before,
            AnnotationHitZone.Left,
            new Point(20, 40),
            new Point(90, 40),
            new Size(100, 100));

        var bounds = AnnotationInteractionService.GetNormalizedBounds(annotation);
        Assert.Equal(60, bounds.Left);
        Assert.Equal(90, bounds.Right);
    }

    [Fact]
    public void ApplyTransform_ChangesOnlySelectedLineEndpoint()
    {
        var annotation = new Annotation
        {
            Type = AnnotationType.Arrow,
            StartPoint = new Point(10, 10),
            EndPoint = new Point(40, 40)
        };
        var before = AnnotationSnapshot.Capture(annotation);

        AnnotationInteractionService.ApplyTransform(
            annotation,
            before,
            AnnotationHitZone.EndPoint,
            annotation.EndPoint,
            new Point(90, 20),
            new Size(100, 100));

        Assert.Equal(new Point(10, 10), annotation.StartPoint);
        Assert.Equal(new Point(90, 20), annotation.EndPoint);
    }

    private static Annotation CreateArea(double x1, double y1, double x2, double y2)
    {
        return new Annotation
        {
            Type = AnnotationType.Rectangle,
            StartPoint = new Point(x1, y1),
            EndPoint = new Point(x2, y2),
            Thickness = 2
        };
    }
}
