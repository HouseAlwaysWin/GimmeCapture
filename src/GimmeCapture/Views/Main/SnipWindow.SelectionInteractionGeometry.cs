using Avalonia;
using GimmeCapture.ViewModels.Main;
using System;
using System.Collections.Generic;

namespace GimmeCapture.Views.Main;

public partial class SnipWindow
{
    private enum SelectionInteractionZone
    {
        None,
        MoveTop,
        ResizeTopLeft,
        ResizeTopRight,
        ResizeBottomLeft,
        ResizeBottomRight,
        ResizeLeft,
        ResizeRight,
        ResizeBottom
    }

    private sealed class SelectionInteractionGeometry
    {
        public required Rect OuterBounds { get; init; }
        public required IReadOnlyList<Rect> VisibleFrameRects { get; init; }
        public required IReadOnlyList<Rect> VisibleDecorationRects { get; init; }
        public required IReadOnlyList<Rect> MoveHandleRects { get; init; }
        public required IReadOnlyList<Rect> ResizeHandleRects { get; init; }
        public required Rect TopMoveRect { get; init; }
        public required Rect LeftResizeRect { get; init; }
        public required Rect RightResizeRect { get; init; }
        public required Rect BottomResizeRect { get; init; }
        public required Rect TopLeftResizeRect { get; init; }
        public required Rect TopRightResizeRect { get; init; }
        public required Rect BottomLeftResizeRect { get; init; }
        public required Rect BottomRightResizeRect { get; init; }

        public IEnumerable<Rect> EnumerateWindowRegionRects()
        {
            foreach (var rect in VisibleFrameRects)
            {
                if (rect.Width > 0 && rect.Height > 0)
                {
                    yield return rect;
                }
            }

            foreach (var rect in VisibleDecorationRects)
            {
                if (rect.Width > 0 && rect.Height > 0)
                {
                    yield return rect;
                }
            }

            foreach (var rect in EnumerateHitTestRects())
            {
                yield return rect;
            }
        }

        public IEnumerable<Rect> EnumerateHitTestRects()
        {
            foreach (var rect in MoveHandleRects)
            {
                if (rect.Width > 0 && rect.Height > 0)
                {
                    yield return rect;
                }
            }

            foreach (var rect in ResizeHandleRects)
            {
                if (rect.Width > 0 && rect.Height > 0)
                {
                    yield return rect;
                }
            }
        }

        public SelectionInteractionZone HitTest(Point point)
        {
            if (TopLeftResizeRect.Contains(point)) return SelectionInteractionZone.ResizeTopLeft;
            if (TopRightResizeRect.Contains(point)) return SelectionInteractionZone.ResizeTopRight;
            if (BottomLeftResizeRect.Contains(point)) return SelectionInteractionZone.ResizeBottomLeft;
            if (BottomRightResizeRect.Contains(point)) return SelectionInteractionZone.ResizeBottomRight;
            if (LeftResizeRect.Contains(point)) return SelectionInteractionZone.ResizeLeft;
            if (RightResizeRect.Contains(point)) return SelectionInteractionZone.ResizeRight;
            if (BottomResizeRect.Contains(point)) return SelectionInteractionZone.ResizeBottom;
            if (TopMoveRect.Contains(point)) return SelectionInteractionZone.MoveTop;
            return SelectionInteractionZone.None;
        }
    }

    private SelectionInteractionGeometry? BuildSelectionInteractionGeometry()
    {
        if (_viewModel == null
            || _viewModel.IsTranslationMode
            || _viewModel.IsDrawingMode
            || _viewModel.CurrentState != SnipState.Selected
            || _viewModel.SelectionRect.Width <= 0
            || _viewModel.SelectionRect.Height <= 0)
        {
            return null;
        }

        return BuildSelectionInteractionGeometry(_viewModel.SelectionRect);
    }

    private SelectionInteractionGeometry BuildSelectionInteractionGeometry(Rect selectionBoundsLogical)
    {
        double borderThickness = Math.Max(1.0, _viewModel?.SelectionBorderThickness ?? 2.0);
        double edgeThickness = Math.Clamp(borderThickness + 4.0, 6.0, 10.0);
        double cornerSize = Math.Clamp(edgeThickness * 2.0, 14.0, 20.0);
        double moveHeight = Math.Clamp(edgeThickness + 2.0, 8.0, 14.0);

        var outer = new Rect(
            selectionBoundsLogical.X - borderThickness,
            selectionBoundsLogical.Y - borderThickness,
            selectionBoundsLogical.Width + (borderThickness * 2),
            selectionBoundsLogical.Height + (borderThickness * 2));

        double horizontalCorner = Math.Min(cornerSize, Math.Max(0, outer.Width / 2));
        double verticalCorner = Math.Min(cornerSize, Math.Max(0, outer.Height / 2));

        var visibleTop = new Rect(outer.X, outer.Y, outer.Width, borderThickness);
        var visibleLeft = new Rect(outer.X, outer.Y, borderThickness, outer.Height);
        var visibleRight = new Rect(Math.Max(outer.X, outer.Right - borderThickness), outer.Y, borderThickness, outer.Height);
        var visibleBottom = new Rect(outer.X, Math.Max(outer.Y, outer.Bottom - borderThickness), outer.Width, borderThickness);

        var topMove = new Rect(
            outer.X + horizontalCorner,
            outer.Y,
            Math.Max(0, outer.Width - (horizontalCorner * 2)),
            moveHeight);

        var leftResize = new Rect(
            outer.X,
            outer.Y + verticalCorner,
            edgeThickness,
            Math.Max(0, outer.Height - (verticalCorner * 2)));

        var rightResize = new Rect(
            Math.Max(outer.X, outer.Right - edgeThickness),
            outer.Y + verticalCorner,
            edgeThickness,
            Math.Max(0, outer.Height - (verticalCorner * 2)));

        var bottomResize = new Rect(
            outer.X + horizontalCorner,
            Math.Max(outer.Y, outer.Bottom - edgeThickness),
            Math.Max(0, outer.Width - (horizontalCorner * 2)),
            edgeThickness);

        var topLeft = new Rect(outer.X, outer.Y, horizontalCorner, verticalCorner);
        var topRight = new Rect(Math.Max(outer.X, outer.Right - horizontalCorner), outer.Y, horizontalCorner, verticalCorner);
        var bottomLeft = new Rect(outer.X, Math.Max(outer.Y, outer.Bottom - verticalCorner), horizontalCorner, verticalCorner);
        var bottomRight = new Rect(Math.Max(outer.X, outer.Right - horizontalCorner), Math.Max(outer.Y, outer.Bottom - verticalCorner), horizontalCorner, verticalCorner);

        double wingWidth = Math.Max(0, _viewModel?.WingWidth ?? 0);
        double wingHeight = Math.Max(0, _viewModel?.WingHeight ?? 0);
        var leftWing = new Rect(
            outer.X - wingWidth,
            outer.Y + (outer.Height - wingHeight) / 2.0,
            wingWidth,
            wingHeight);
        var rightWing = new Rect(
            outer.Right,
            outer.Y + (outer.Height - wingHeight) / 2.0,
            wingWidth,
            wingHeight);

        return new SelectionInteractionGeometry
        {
            OuterBounds = outer,
            VisibleFrameRects = new[] { visibleTop, visibleLeft, visibleRight, visibleBottom },
            VisibleDecorationRects = new[] { leftWing, rightWing },
            MoveHandleRects = new[] { topMove },
            ResizeHandleRects = new[] { leftResize, rightResize, bottomResize, topLeft, topRight, bottomLeft, bottomRight },
            TopMoveRect = topMove,
            LeftResizeRect = leftResize,
            RightResizeRect = rightResize,
            BottomResizeRect = bottomResize,
            TopLeftResizeRect = topLeft,
            TopRightResizeRect = topRight,
            BottomLeftResizeRect = bottomLeft,
            BottomRightResizeRect = bottomRight
        };
    }

    private SelectionInteractionZone HitTestSelectionInteraction(Point point)
    {
        return BuildSelectionInteractionGeometry()?.HitTest(point) ?? SelectionInteractionZone.None;
    }

    private static Rect ScaleRect(Rect logicalRect, double scaling)
    {
        return new Rect(
            logicalRect.X * scaling,
            logicalRect.Y * scaling,
            logicalRect.Width * scaling,
            logicalRect.Height * scaling);
    }
}
