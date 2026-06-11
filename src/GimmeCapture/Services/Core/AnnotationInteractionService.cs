using System;
using System.Collections.Generic;
using Avalonia;
using GimmeCapture.Models;

namespace GimmeCapture.Services.Core;

public enum AnnotationHitZone
{
    None,
    Body,
    TopLeft,
    Top,
    TopRight,
    Right,
    BottomRight,
    Bottom,
    BottomLeft,
    Left,
    StartPoint,
    EndPoint
}

public readonly record struct AnnotationHitResult(Annotation? Annotation, AnnotationHitZone Zone)
{
    public static AnnotationHitResult None => new(null, AnnotationHitZone.None);
    public bool IsHit => Annotation != null && Zone != AnnotationHitZone.None;
}

public static class AnnotationInteractionService
{
    public const double HandleRadius = 6;
    public const double MinimumAreaSize = 8;
    public const double MinimumLineLength = 4;

    public static AnnotationHitResult HitTest(
        IReadOnlyList<Annotation> annotations,
        Annotation? selectedAnnotation,
        Point point)
    {
        if (selectedAnnotation != null)
        {
            var handle = HitTestHandles(selectedAnnotation, point);
            if (handle != AnnotationHitZone.None)
            {
                return new AnnotationHitResult(selectedAnnotation, handle);
            }
        }

        for (int i = annotations.Count - 1; i >= 0; i--)
        {
            var annotation = annotations[i];
            if (!IsEditable(annotation.Type))
            {
                continue;
            }

            if (HitTestBody(annotation, point))
            {
                return new AnnotationHitResult(annotation, AnnotationHitZone.Body);
            }
        }

        return AnnotationHitResult.None;
    }

    public static AnnotationHitZone HitTestHandles(Annotation annotation, Point point)
    {
        if (!annotation.IsSelected)
        {
            return AnnotationHitZone.None;
        }

        if (IsArea(annotation.Type))
        {
            var bounds = GetNormalizedBounds(annotation);
            var midX = bounds.X + bounds.Width / 2;
            var midY = bounds.Y + bounds.Height / 2;
            var handles = new (Point Point, AnnotationHitZone Zone)[]
            {
                (bounds.TopLeft, AnnotationHitZone.TopLeft),
                (new Point(midX, bounds.Top), AnnotationHitZone.Top),
                (bounds.TopRight, AnnotationHitZone.TopRight),
                (new Point(bounds.Right, midY), AnnotationHitZone.Right),
                (bounds.BottomRight, AnnotationHitZone.BottomRight),
                (new Point(midX, bounds.Bottom), AnnotationHitZone.Bottom),
                (bounds.BottomLeft, AnnotationHitZone.BottomLeft),
                (new Point(bounds.Left, midY), AnnotationHitZone.Left)
            };

            foreach (var handle in handles)
            {
                if (Distance(point, handle.Point) <= HandleRadius + 2)
                {
                    return handle.Zone;
                }
            }
        }
        else if (IsLine(annotation.Type))
        {
            if (Distance(point, annotation.StartPoint) <= HandleRadius + 2)
            {
                return AnnotationHitZone.StartPoint;
            }

            if (Distance(point, annotation.EndPoint) <= HandleRadius + 2)
            {
                return AnnotationHitZone.EndPoint;
            }
        }

        return AnnotationHitZone.None;
    }

    public static void ApplyTransform(
        Annotation annotation,
        AnnotationSnapshot original,
        AnnotationHitZone zone,
        Point dragStart,
        Point pointer,
        Size canvasSize)
    {
        if (zone == AnnotationHitZone.Body)
        {
            Move(annotation, original, pointer - dragStart, canvasSize);
            return;
        }

        if (IsArea(annotation.Type))
        {
            ResizeArea(annotation, original, zone, pointer, canvasSize);
        }
        else if (IsLine(annotation.Type))
        {
            ResizeLine(annotation, original, zone, pointer, canvasSize);
        }
    }

    public static bool IsValidCompletedAnnotation(Annotation annotation)
    {
        if (IsArea(annotation.Type))
        {
            var bounds = GetNormalizedBounds(annotation);
            return bounds.Width >= MinimumAreaSize && bounds.Height >= MinimumAreaSize;
        }

        if (IsLine(annotation.Type))
        {
            return Distance(annotation.StartPoint, annotation.EndPoint) >= MinimumLineLength;
        }

        if (annotation.Type == AnnotationType.Pen)
        {
            return annotation.Points.Count >= 2;
        }

        return true;
    }

    public static Rect GetNormalizedBounds(Annotation annotation)
    {
        return new Rect(
            Math.Min(annotation.StartPoint.X, annotation.EndPoint.X),
            Math.Min(annotation.StartPoint.Y, annotation.EndPoint.Y),
            Math.Abs(annotation.EndPoint.X - annotation.StartPoint.X),
            Math.Abs(annotation.EndPoint.Y - annotation.StartPoint.Y));
    }

    public static bool IsEditable(AnnotationType type) => IsArea(type) || IsLine(type);

    public static bool IsArea(AnnotationType type) =>
        type is AnnotationType.Rectangle or AnnotationType.Ellipse or AnnotationType.Mosaic or AnnotationType.Blur;

    public static bool IsLine(AnnotationType type) =>
        type is AnnotationType.Line or AnnotationType.Arrow;

    private static bool HitTestBody(Annotation annotation, Point point)
    {
        if (IsArea(annotation.Type))
        {
            var bounds = GetNormalizedBounds(annotation).Inflate(Math.Max(3, annotation.Thickness / 2));
            if (annotation.Type != AnnotationType.Ellipse)
            {
                return bounds.Contains(point);
            }

            if (bounds.Width <= 0 || bounds.Height <= 0)
            {
                return false;
            }

            var centerX = bounds.X + bounds.Width / 2;
            var centerY = bounds.Y + bounds.Height / 2;
            var dx = (point.X - centerX) / (bounds.Width / 2);
            var dy = (point.Y - centerY) / (bounds.Height / 2);
            return dx * dx + dy * dy <= 1;
        }

        if (IsLine(annotation.Type))
        {
            return DistanceToSegment(point, annotation.StartPoint, annotation.EndPoint)
                <= Math.Max(6, annotation.Thickness + 3);
        }

        return false;
    }

    private static void Move(
        Annotation annotation,
        AnnotationSnapshot original,
        Vector requestedDelta,
        Size canvasSize)
    {
        var geometryBounds = new Rect(
            Math.Min(original.StartPoint.X, original.EndPoint.X),
            Math.Min(original.StartPoint.Y, original.EndPoint.Y),
            Math.Abs(original.EndPoint.X - original.StartPoint.X),
            Math.Abs(original.EndPoint.Y - original.StartPoint.Y));

        var minDx = -geometryBounds.Left;
        var maxDx = canvasSize.Width - geometryBounds.Right;
        var minDy = -geometryBounds.Top;
        var maxDy = canvasSize.Height - geometryBounds.Bottom;
        var dx = minDx <= maxDx
            ? Math.Clamp(requestedDelta.X, minDx, maxDx)
            : -geometryBounds.Left;
        var dy = minDy <= maxDy
            ? Math.Clamp(requestedDelta.Y, minDy, maxDy)
            : -geometryBounds.Top;

        annotation.StartPoint = new Point(original.StartPoint.X + dx, original.StartPoint.Y + dy);
        annotation.EndPoint = new Point(original.EndPoint.X + dx, original.EndPoint.Y + dy);
    }

    private static void ResizeArea(
        Annotation annotation,
        AnnotationSnapshot original,
        AnnotationHitZone zone,
        Point pointer,
        Size canvasSize)
    {
        var originalBounds = new Rect(
            Math.Min(original.StartPoint.X, original.EndPoint.X),
            Math.Min(original.StartPoint.Y, original.EndPoint.Y),
            Math.Abs(original.EndPoint.X - original.StartPoint.X),
            Math.Abs(original.EndPoint.Y - original.StartPoint.Y));

        double left = originalBounds.Left;
        double top = originalBounds.Top;
        double right = originalBounds.Right;
        double bottom = originalBounds.Bottom;
        var x = Math.Clamp(pointer.X, 0, canvasSize.Width);
        var y = Math.Clamp(pointer.Y, 0, canvasSize.Height);

        if (zone is AnnotationHitZone.TopLeft or AnnotationHitZone.Left or AnnotationHitZone.BottomLeft)
            (left, right) = ResolveMovingAxis(x, originalBounds.Right, canvasSize.Width);
        if (zone is AnnotationHitZone.TopRight or AnnotationHitZone.Right or AnnotationHitZone.BottomRight)
            (left, right) = ResolveMovingAxis(x, originalBounds.Left, canvasSize.Width);
        if (zone is AnnotationHitZone.TopLeft or AnnotationHitZone.Top or AnnotationHitZone.TopRight)
            (top, bottom) = ResolveMovingAxis(y, originalBounds.Bottom, canvasSize.Height);
        if (zone is AnnotationHitZone.BottomLeft or AnnotationHitZone.Bottom or AnnotationHitZone.BottomRight)
            (top, bottom) = ResolveMovingAxis(y, originalBounds.Top, canvasSize.Height);

        annotation.StartPoint = new Point(left, top);
        annotation.EndPoint = new Point(right, bottom);
    }

    private static (double Low, double High) ResolveMovingAxis(
        double movingCoordinate,
        double fixedCoordinate,
        double canvasLength)
    {
        var low = Math.Min(movingCoordinate, fixedCoordinate);
        var high = Math.Max(movingCoordinate, fixedCoordinate);
        if (high - low >= MinimumAreaSize)
        {
            return (low, high);
        }

        if (movingCoordinate <= fixedCoordinate)
        {
            low = Math.Max(0, fixedCoordinate - MinimumAreaSize);
            high = Math.Min(canvasLength, low + MinimumAreaSize);
        }
        else
        {
            high = Math.Min(canvasLength, fixedCoordinate + MinimumAreaSize);
            low = Math.Max(0, high - MinimumAreaSize);
        }

        return (low, high);
    }

    private static void ResizeLine(
        Annotation annotation,
        AnnotationSnapshot original,
        AnnotationHitZone zone,
        Point pointer,
        Size canvasSize)
    {
        var clamped = new Point(
            Math.Clamp(pointer.X, 0, canvasSize.Width),
            Math.Clamp(pointer.Y, 0, canvasSize.Height));

        if (zone == AnnotationHitZone.StartPoint)
        {
            annotation.EndPoint = original.EndPoint;
            annotation.StartPoint = EnsureMinimumLineLength(clamped, original.EndPoint, original.StartPoint);
        }
        else if (zone == AnnotationHitZone.EndPoint)
        {
            annotation.StartPoint = original.StartPoint;
            annotation.EndPoint = EnsureMinimumLineLength(clamped, original.StartPoint, original.EndPoint);
        }
    }

    private static Point EnsureMinimumLineLength(Point candidate, Point fixedPoint, Point fallback)
    {
        var vector = candidate - fixedPoint;
        var length = Math.Sqrt(vector.X * vector.X + vector.Y * vector.Y);
        if (length >= MinimumLineLength)
        {
            return candidate;
        }

        if (length < 0.001)
        {
            return fallback;
        }

        return fallback;
    }

    private static double DistanceToSegment(Point point, Point start, Point end)
    {
        var segment = end - start;
        var lengthSquared = segment.X * segment.X + segment.Y * segment.Y;
        if (lengthSquared <= double.Epsilon)
        {
            return Distance(point, start);
        }

        var relative = point - start;
        var t = Math.Clamp(
            (relative.X * segment.X + relative.Y * segment.Y) / lengthSquared,
            0,
            1);
        var projection = new Point(start.X + segment.X * t, start.Y + segment.Y * t);
        return Distance(point, projection);
    }

    private static double Distance(Point a, Point b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }
}
