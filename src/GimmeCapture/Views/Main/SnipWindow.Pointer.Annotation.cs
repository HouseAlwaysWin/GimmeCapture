using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using GimmeCapture.Models;
using GimmeCapture.Services.Core;
using GimmeCapture.ViewModels.Main;
using GimmeCapture.Views.Controls;
using System;

namespace GimmeCapture.Views.Main;

public partial class SnipWindow
{
    private bool TryHandleEditableAnnotationPressed(Point point, PointerPressedEventArgs e)
    {
        if (_viewModel == null) return false;

        var properties = e.GetCurrentPoint(this).Properties;
        if (!properties.IsLeftButtonPressed
            || !_viewModel.IsDrawingMode
            || _viewModel.CurrentState != SnipState.Selected
            || _viewModel.CurrentAnnotationTool == AnnotationType.None
            || !_viewModel.SelectionRect.Inflate(AnnotationInteractionService.HandleRadius + 2).Contains(point))
        {
            return false;
        }

        var relativePoint = new Point(
            point.X - _viewModel.SelectionRect.X,
            point.Y - _viewModel.SelectionRect.Y);
        var hit = AnnotationInteractionService.HitTest(
            _viewModel.Annotations,
            _viewModel.SelectedAnnotation,
            relativePoint);

        if (!hit.IsHit || hit.Annotation == null)
        {
            if (_viewModel.SelectionRect.Contains(point))
            {
                _viewModel.ClearAnnotationSelection();
            }
            return false;
        }

        _draggingAnnotation = hit.Annotation;
        _annotationHitZone = hit.Zone;
        _annotationDragStart = relativePoint;
        _annotationEditBefore = _viewModel.BeginAnnotationEdit(hit.Annotation);
        _pointerState = PointerInteractionState.DraggingAnnotation;
        e.Pointer.Capture(this);
        e.Handled = true;
        return true;
    }

    private bool TryHandleTextAnnotationPressed(Point point, PointerPressedEventArgs e)
    {
        if (_viewModel == null) return false;

        var props = e.GetCurrentPoint(this).Properties;
        if (!props.IsLeftButtonPressed || !_viewModel.IsDrawingMode || _viewModel.CurrentAnnotationTool != AnnotationType.Text)
            return false;

        var selectionSpacePoint = new Point(point.X - _viewModel.SelectionRect.X, point.Y - _viewModel.SelectionRect.Y);

        for (int i = _viewModel.Annotations.Count - 1; i >= 0; i--)
        {
            var ann = _viewModel.Annotations[i];
            if (ann.Type != AnnotationType.Text) continue;

            double estimatedWidth = ann.Text.Length * ann.FontSize * 0.6;
            double estimatedHeight = ann.FontSize * 1.5;
            var rect = new Rect(ann.StartPoint.X, ann.StartPoint.Y, estimatedWidth, estimatedHeight);

            if (!rect.Contains(selectionSpacePoint)) continue;

            if (e.ClickCount == 2)
            {
                _viewModel.RemoveAnnotation(ann);

                _viewModel.CurrentFontSize = ann.FontSize;
                _viewModel.CurrentFontFamily = ann.FontFamily;
                _viewModel.IsBold = ann.IsBold;
                _viewModel.IsItalic = ann.IsItalic;
                _viewModel.SelectedColor = ann.Color;
                BeginSelectionTextEntry(
                    new Point(
                        ann.StartPoint.X + _viewModel.SelectionRect.X,
                        ann.StartPoint.Y + _viewModel.SelectionRect.Y),
                    ann.Text);

                e.Handled = true;
                return true;
            }

            _pointerState = PointerInteractionState.DraggingAnnotation;
            _draggingAnnotation = ann;
            _dragOffset = new Point(selectionSpacePoint.X - ann.StartPoint.X, selectionSpacePoint.Y - ann.StartPoint.Y);
            _annotationHitZone = AnnotationHitZone.Body;
            _annotationDragStart = selectionSpacePoint;
            _annotationEditBefore = _viewModel.BeginAnnotationEdit(ann);
            e.Pointer.Capture(this);
            e.Handled = true;
            return true;
        }

        return false;
    }

    private bool TryHandleDrawingStartPressed(Point point, PointerPressedEventArgs e)
    {
        if (_viewModel == null) return false;
        if (!_viewModel.IsDrawingMode || _viewModel.CurrentState != SnipState.Selected) return false;
        if (!_viewModel.SelectionRect.Contains(point)) return false;

        // Text places a label directly via the text-entry overlay. Callout is drawn like a line:
        // press on the target and drag out to the label position; the text box opens on release
        // (handled in TryHandleAnnotationPointerReleased).
        if (_viewModel.CurrentAnnotationTool == AnnotationType.Text)
        {
            BeginSelectionTextEntry(point, string.Empty);
            e.Handled = true;
            return true;
        }

        _startPoint = point;
        var relPoint = new Point(point.X - _viewModel.SelectionRect.X, point.Y - _viewModel.SelectionRect.Y);

        _currentAnnotation = _viewModel.CreateAnnotationForCurrentTool(relPoint);

        _viewModel.BeginPendingAnnotation(_currentAnnotation);
        e.Pointer.Capture(this);
        e.Handled = true;
        return true;
    }

    private bool TryHandleAnnotationPointerMoved(Point currentPoint)
    {
        if (_viewModel == null) return false;

        if (_pointerState == PointerInteractionState.DraggingAnnotation && _draggingAnnotation != null)
        {
            var selectionSpacePoint = new Point(currentPoint.X - _viewModel.SelectionRect.X, currentPoint.Y - _viewModel.SelectionRect.Y);
            if (_draggingAnnotation.Type == AnnotationType.Text)
            {
                var estimatedWidth = _draggingAnnotation.Text.Length * _draggingAnnotation.FontSize * 0.6;
                var estimatedHeight = _draggingAnnotation.FontSize * 1.5;
                var x = Math.Clamp(
                    selectionSpacePoint.X - _dragOffset.X,
                    0,
                    Math.Max(0, _viewModel.SelectionRect.Width - estimatedWidth));
                var y = Math.Clamp(
                    selectionSpacePoint.Y - _dragOffset.Y,
                    0,
                    Math.Max(0, _viewModel.SelectionRect.Height - estimatedHeight));
                var delta = new Point(x - _draggingAnnotation.StartPoint.X, y - _draggingAnnotation.StartPoint.Y);
                _draggingAnnotation.StartPoint = new Point(x, y);
                _draggingAnnotation.EndPoint = new Point(
                    _draggingAnnotation.EndPoint.X + delta.X,
                    _draggingAnnotation.EndPoint.Y + delta.Y);
            }
            else if (_annotationEditBefore != null)
            {
                AnnotationInteractionService.ApplyTransform(
                    _draggingAnnotation,
                    _annotationEditBefore,
                    _annotationHitZone,
                    _annotationDragStart,
                    selectionSpacePoint,
                    _viewModel.SelectionRect.Size);
            }
            return true;
        }

        if (_viewModel.CurrentState == SnipState.Selected && _currentAnnotation != null)
        {
            var relPoint = new Point(
                Math.Clamp(currentPoint.X - _viewModel.SelectionRect.X, 0, _viewModel.SelectionRect.Width),
                Math.Clamp(currentPoint.Y - _viewModel.SelectionRect.Y, 0, _viewModel.SelectionRect.Height));
            if (_currentAnnotation.Type == AnnotationType.Pen)
                _currentAnnotation.AddPoint(relPoint);
            else
                _currentAnnotation.EndPoint = relPoint;
            return true;
        }

        return false;
    }

    private void BeginSelectionTextEntry(Point position, string text)
    {
        if (_viewModel == null) return;

        _viewModel.TextInputPosition = position;
        _viewModel.PendingText = text;
        _viewModel.IsEnteringText = true;
        _viewModel.RefreshInteractionRegion();
        this.FindDescendantOfType<DrawingToolbar>()?.HideTextFlyout();

        Avalonia.Threading.Dispatcher.UIThread.Post(
            () => this.FindControl<TextEntryOverlay>("TextEntryOverlay")?.FocusTextInput(),
            Avalonia.Threading.DispatcherPriority.Loaded);
    }

    private bool TryHandleAnnotationPointerReleased(PointerReleasedEventArgs e)
    {
        if (_viewModel == null) return false;

        if (_pointerState == PointerInteractionState.DraggingAnnotation)
        {
            if (_draggingAnnotation != null && _annotationEditBefore != null)
            {
                _viewModel.CommitAnnotationEdit(_draggingAnnotation, _annotationEditBefore);
            }
            _pointerState = PointerInteractionState.None;
            _draggingAnnotation = null;
            _annotationEditBefore = null;
            _annotationHitZone = AnnotationHitZone.None;
            e.Pointer.Capture(null);
            return true;
        }

        if (_currentAnnotation != null)
        {
            if (_currentAnnotation.Type == AnnotationType.Callout)
            {
                // Leader drawn; keep it pending and collect the label text at the release point.
                BeginCalloutLabelEntry(_currentAnnotation);
                _currentAnnotation = null;
                e.Pointer.Capture(null);
                return true;
            }

            _viewModel.CommitPendingAnnotation(_currentAnnotation);
            _currentAnnotation = null;
            e.Pointer.Capture(null);
            return true;
        }

        return false;
    }

    // After the Callout leader is dragged, open the text-entry overlay at the label anchor
    // (EndPoint). A click with no real drag gets a default offset so the leader stays visible
    // and valid. The pending leader is finalized by ConfirmTextEntryCommand.
    private void BeginCalloutLabelEntry(Annotation leader)
    {
        if (_viewModel == null) return;

        var dx = leader.EndPoint.X - leader.StartPoint.X;
        var dy = leader.EndPoint.Y - leader.StartPoint.Y;
        if (Math.Sqrt((dx * dx) + (dy * dy)) < AnnotationInteractionService.MinimumLineLength)
        {
            leader.EndPoint = new Point(
                Math.Min(_viewModel.SelectionRect.Width, leader.StartPoint.X + 48),
                Math.Min(_viewModel.SelectionRect.Height, leader.StartPoint.Y + 48));
        }

        _viewModel.BeginCalloutTextEntry(leader);

        var anchorWindow = new Point(
            leader.EndPoint.X + _viewModel.SelectionRect.X,
            leader.EndPoint.Y + _viewModel.SelectionRect.Y);
        BeginSelectionTextEntry(anchorWindow, leader.Text ?? string.Empty);
    }
}
