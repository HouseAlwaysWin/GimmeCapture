using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using GimmeCapture.Models;
using GimmeCapture.ViewModels.Main;

namespace GimmeCapture.Views.Main;

public partial class SnipWindow
{
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

                _viewModel.IsEnteringText = true;
                _viewModel.TextInputPosition = new Point(ann.StartPoint.X + _viewModel.SelectionRect.X, ann.StartPoint.Y + _viewModel.SelectionRect.Y);
                _viewModel.PendingText = ann.Text;
                _viewModel.CurrentFontSize = ann.FontSize;
                _viewModel.CurrentFontFamily = ann.FontFamily;
                _viewModel.IsBold = ann.IsBold;
                _viewModel.IsItalic = ann.IsItalic;
                _viewModel.SelectedColor = ann.Color;

                var textBox = this.FindControl<TextBox>("TextInputOverlay");
                Avalonia.Threading.Dispatcher.UIThread.Post(() => textBox?.Focus());

                e.Handled = true;
                return true;
            }

            _pointerState = PointerInteractionState.DraggingAnnotation;
            _draggingAnnotation = ann;
            _dragOffset = new Point(selectionSpacePoint.X - ann.StartPoint.X, selectionSpacePoint.Y - ann.StartPoint.Y);
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

        if (_viewModel.CurrentAnnotationTool == AnnotationType.Text)
        {
            _viewModel.IsEnteringText = true;
            _viewModel.TextInputPosition = point;
            _viewModel.PendingText = string.Empty;
            e.Handled = true;
            return true;
        }

        _startPoint = point;
        var relPoint = new Point(point.X - _viewModel.SelectionRect.X, point.Y - _viewModel.SelectionRect.Y);

        _currentAnnotation = new Annotation
        {
            Type = _viewModel.CurrentAnnotationTool,
            StartPoint = relPoint,
            EndPoint = relPoint,
            Color = _viewModel.SelectedColor,
            Thickness = _viewModel.CurrentThickness,
            FontSize = _viewModel.CurrentFontSize,
            DrawingModeSnapshot = _viewModel.DrawingModeSnapshot,
            DrawingModeReferenceSize = _viewModel.SelectionRect.Size
        };

        if (_viewModel.CurrentAnnotationTool == AnnotationType.Pen)
        {
            _currentAnnotation.AddPoint(relPoint);
        }

        _viewModel.AddAnnotation(_currentAnnotation);
        e.Handled = true;
        return true;
    }

    private bool TryHandleAnnotationPointerMoved(Point currentPoint)
    {
        if (_viewModel == null) return false;

        if (_pointerState == PointerInteractionState.DraggingAnnotation && _draggingAnnotation != null)
        {
            var selectionSpacePoint = new Point(currentPoint.X - _viewModel.SelectionRect.X, currentPoint.Y - _viewModel.SelectionRect.Y);
            _draggingAnnotation.StartPoint = new Point(selectionSpacePoint.X - _dragOffset.X, selectionSpacePoint.Y - _dragOffset.Y);
            _draggingAnnotation.EndPoint = _draggingAnnotation.StartPoint;
            return true;
        }

        if (_viewModel.CurrentState == SnipState.Selected && _currentAnnotation != null)
        {
            var relPoint = new Point(currentPoint.X - _viewModel.SelectionRect.X, currentPoint.Y - _viewModel.SelectionRect.Y);
            if (_currentAnnotation.Type == AnnotationType.Pen)
                _currentAnnotation.AddPoint(relPoint);
            else
                _currentAnnotation.EndPoint = relPoint;
            return true;
        }

        return false;
    }

    private bool TryHandleAnnotationPointerReleased(PointerReleasedEventArgs e)
    {
        if (_pointerState == PointerInteractionState.DraggingAnnotation)
        {
            _pointerState = PointerInteractionState.None;
            _draggingAnnotation = null;
            e.Pointer.Capture(null);
            return true;
        }

        if (_currentAnnotation != null)
        {
            _currentAnnotation = null;
            return true;
        }

        return false;
    }
}
