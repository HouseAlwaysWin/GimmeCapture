using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using GimmeCapture.ViewModels.Main;
using GimmeCapture.Views.Controls;
using GimmeCapture.Models;
using GimmeCapture.Services; // For RecordingState if it's there
using System;
using GimmeCapture.Services.Core;
using System.Linq;
using System.Reactive;
using ReactiveUI;

namespace GimmeCapture.Views.Main;

public partial class SnipWindow : Window
{
    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_viewModel == null) return;

        // Debounce: If we just finished text entry, ignore clicks for a short moment
        if ((DateTime.Now - _lastTextFinishTime).TotalMilliseconds < 300)
        {
            e.Handled = true;
            return;
        }

        // Prevent recursive text entry (If clicking to finish text, don't restart it immediately)
        if (_viewModel.IsEnteringText)
        {
             var src = e.Source as Control;
             // If clicking on the textbox itself or its children, let it function
             if (src != null && (src.Name == "TextInputOverlay" || src.FindAncestorOfType<TextBox>() != null))
             {
                 return;
             }
             
             // If clicking the OK button
             if (src is Button b && b.Content as string == "OK") return;
 
             _viewModel.ConfirmTextEntryCommand.Execute(Unit.Default).Subscribe();
             e.Handled = true;
             return;
        }

        var point = e.GetPosition(this);
        var props = e.GetCurrentPoint(this).Properties;
        var source = e.Source as Control;

        // 1. Text Interaction (Edit / Move) - High Priority
        if (props.IsLeftButtonPressed && _viewModel.IsDrawingMode && _viewModel.CurrentAnnotationTool == AnnotationType.Text)
        {
             // Convert Window Point to Selection Space for Hit Testing
             var selectionSpacePoint = new Point(point.X - _viewModel.SelectionRect.X, point.Y - _viewModel.SelectionRect.Y);
             
             // Check for hit on existing text annotations (Top-most first)
             for (int i = _viewModel.Annotations.Count - 1; i >= 0; i--)
             {
                 var ann = _viewModel.Annotations[i];
                 if (ann.Type == AnnotationType.Text)
                 {
                     double estimatedWidth = ann.Text.Length * ann.FontSize * 0.6; 
                     double estimatedHeight = ann.FontSize * 1.5;
                     
                     var rect = new Rect(ann.StartPoint.X, ann.StartPoint.Y, estimatedWidth, estimatedHeight);
                     if (rect.Contains(selectionSpacePoint))
                     {
                         if (e.ClickCount == 2)
                         {
                             // Double Click -> Edit Mode
                             _viewModel.RemoveAnnotation(ann);
                             
                             _viewModel.IsEnteringText = true;
                             _viewModel.TextInputPosition = new Point(ann.StartPoint.X + _viewModel.SelectionRect.X, ann.StartPoint.Y + _viewModel.SelectionRect.Y);
                             _viewModel.PendingText = ann.Text;
                             _viewModel.CurrentFontSize = ann.FontSize;
                             _viewModel.CurrentFontFamily = ann.FontFamily;
                             _viewModel.IsBold = ann.IsBold;
                             _viewModel.IsItalic = ann.IsItalic;
                             _viewModel.SelectedColor = ann.Color;

                            // Do NOT call FinishTextEntry() here. We want to START entry.
                            
                             // Focus Textbox
                             var textBox = this.FindControl<TextBox>("TextInputOverlay");
                             Avalonia.Threading.Dispatcher.UIThread.Post(() => textBox?.Focus());
                             
                             e.Handled = true;
                             return;
                         }
                         else
                         {
                             // Single Click -> Start Dragging
                             _isDraggingAnnotation = true;
                             _draggingAnnotation = ann;
                             _dragOffset = new Point(selectionSpacePoint.X - ann.StartPoint.X, selectionSpacePoint.Y - ann.StartPoint.Y);
                             e.Handled = true;
                             return;
                         }
                     }
                 }
             }
        }

        // Check for handle interaction (Move or Resize)
        // prioritized before drawing logic to allow adjustment while tool is active
        var sourceControl = e.Source as Control;
        bool isResizeHandle = sourceControl != null && sourceControl.Classes.Contains("Handle");
        bool isMoveHandle = sourceControl != null && (sourceControl.Classes.Contains("MoveHandle") || sourceControl.Name?.Contains("InnerCorner") == true);

        if (props.IsLeftButtonPressed && isResizeHandle)
        {
            if (_viewModel.RecState != RecordingState.Idle)
            {
                e.Handled = true;
                return; // Block resize during recording
            }
            _isResizing = true;
            _resizeDirection = GetDirectionFromName(sourceControl!.Name);
            _resizeStartPoint = point;
            _originalRect = _viewModel.SelectionRect;
            e.Handled = true;
            return;
        }

        if (props.IsLeftButtonPressed && isMoveHandle)
        {
            if (_viewModel.RecState == RecordingState.Idle && _viewModel.CurrentState == SnipState.Selected)
            {
                _isMovingSelection = true;
                _moveStartPoint = point;
                _originalRect = _viewModel.SelectionRect;
                e.Handled = true;
                return;
            }
        }

        if (props.IsLeftButtonPressed)
        {
            if (_viewModel.IsDrawingMode && _viewModel.CurrentState == SnipState.Selected)
            {
                // Logic: If in drawing mode and clicked INSIDE the selection area, draw.
                if (_viewModel.SelectionRect.Contains(point))
                {
                    if (_viewModel.CurrentAnnotationTool == AnnotationType.Text)
                    {
                        // Start Text Entry
                        _viewModel.IsEnteringText = true;
                        _viewModel.TextInputPosition = point;
                        _viewModel.PendingText = string.Empty;
                        
                        e.Handled = true;
                        return;
                    }

                    // Start Drawing
                    _startPoint = point;
                    var relPoint = new Point(point.X - _viewModel.SelectionRect.X, point.Y - _viewModel.SelectionRect.Y);
                    
                    _currentAnnotation = new Annotation
                    {
                        Type = _viewModel.CurrentAnnotationTool,
                        StartPoint = relPoint,
                        EndPoint = relPoint,
                        Color = _viewModel.SelectedColor,
                        Thickness = _viewModel.CurrentThickness,
                        FontSize = _viewModel.CurrentFontSize
                    };

                    if (_viewModel.CurrentAnnotationTool == AnnotationType.Pen)
                    {
                        _currentAnnotation.AddPoint(relPoint);
                    }
                    
                    _viewModel.AddAnnotation(_currentAnnotation);
                    e.Handled = true;
                    return;
                }
            }

            // If clicking OUTSIDE or in Idle/Detecting, start NEW selection
            // Check if the click is within the toolbar bounds (coordinate-based check)
            var toolbar = this.FindControl<SnipToolbar>("Toolbar");
            if (toolbar != null && toolbar.IsVisible)
            {
                // Get toolbar bounds in window coordinates
                var toolbarBounds = toolbar.Bounds;
                var toolbarPos = toolbar.TranslatePoint(new Point(0, 0), this);
                if (toolbarPos.HasValue)
                {
                    var toolbarRect = new Rect(toolbarPos.Value, toolbarBounds.Size);
                    // Expand the rect a bit to account for flyouts appearing below
                    var expandedRect = new Rect(
                        toolbarRect.X - 20, 
                        toolbarRect.Y - 20, 
                        toolbarRect.Width + 200,  // Flyouts can extend to the right
                        toolbarRect.Height + 250  // Flyouts can extend down
                    );
                    if (expandedRect.Contains(point))
                        return; // Don't start selection when clicking in toolbar area
                }
            }
            
            // Also check visual tree for popups
            if (sourceControl != null)
            {
                Control? ancestor = sourceControl;
                while (ancestor != null)
                {
                    if (ancestor is Views.Controls.SnipToolbar || 
                        ancestor is Avalonia.Controls.Primitives.Popup)
                        return;
                    ancestor = ancestor.GetVisualParent() as Control;
                }
            }
            
            if (_viewModel.RecState == RecordingState.Idle && 
                (_viewModel.CurrentState == SnipState.Idle || _viewModel.CurrentState == SnipState.Detecting))
            {
                _startPoint = point;
                _viewModel.CurrentState = SnipState.Selecting;
                _viewModel.SelectionRect = new Rect(_startPoint, new Size(0, 0));
                _viewModel.IsDrawingMode = false;
                _viewModel.ClearAnnotationsCommand.Execute().Subscribe();
            }
            else if (_viewModel.RecState == RecordingState.Idle && _viewModel.CurrentState == SnipState.Selected)
            {
                var expandedBounds = _viewModel.SelectionRect.Inflate(120);
                if (expandedBounds.Contains(point) && !_viewModel.SelectionRect.Contains(point))
                {
                    e.Handled = true;
                    return;
                }

                if (!_viewModel.SelectionRect.Contains(point))
                {
                     _startPoint = point;
                     _viewModel.CurrentState = SnipState.Selecting;
                     _viewModel.SelectionRect = new Rect(_startPoint, new Size(0, 0));
                     _viewModel.IsDrawingMode = false;
                     _viewModel.ClearAnnotationsCommand.Execute().Subscribe();
                }
            }
        }
        else if (props.IsRightButtonPressed)
        {
            if (_viewModel == null) return;

            _viewModel.HandleRightClick();
        }
    }

    private StandardCursorType _currentCursorType = StandardCursorType.Arrow;
    
    // Rename to avoid ambiguity
    private void SetCursorShape(StandardCursorType type)
    {
        if (_currentCursorType != type)
        {
            this.Cursor = new Cursor(type);
            _currentCursorType = type;
        }
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_viewModel == null) return;

        var currentPoint = e.GetPosition(this);
        UpdateActiveScreenBounds(currentPoint);
        var sourceControl = e.Source as Control;

        // --- Cursor Logic ---
        if (_isMovingSelection || _isDraggingAnnotation)
        {
            SetCursorShape(StandardCursorType.SizeAll);
        }
        else if (_isResizing)
        {
             // Handled by XAML/Capture
        }
        else if (_viewModel.CurrentState == SnipState.Selected)
        {
            bool cursorSet = false;

            // 1. Text Annotation Hover (Hand Cursor)
            if (_viewModel.IsDrawingMode && _viewModel.CurrentAnnotationTool == AnnotationType.Text)
            {
                var selectionSpacePoint = new Point(currentPoint.X - _viewModel.SelectionRect.X, currentPoint.Y - _viewModel.SelectionRect.Y);
                 for (int i = _viewModel.Annotations.Count - 1; i >= 0; i--)
                 {
                     var ann = _viewModel.Annotations[i];
                     if (ann.Type == AnnotationType.Text)
                     {
                         double estimatedWidth = ann.Text.Length * ann.FontSize * 0.6; 
                         double estimatedHeight = ann.FontSize * 1.5;
                         var rect = new Rect(ann.StartPoint.X, ann.StartPoint.Y, estimatedWidth, estimatedHeight);
                         
                         if (rect.Contains(selectionSpacePoint))
                         {
                             SetCursorShape(StandardCursorType.Hand);
                             cursorSet = true;
                             break;
                         }
                     }
                 }
            }

            if (!cursorSet && !_viewModel.IsDrawingMode && _viewModel.SelectionRect.Contains(currentPoint))
            {
                bool isOverHandle = sourceControl != null && sourceControl.Classes.Contains("Handle");
                
                if (!isOverHandle)
                {
                    SetCursorShape(StandardCursorType.SizeAll);
                    cursorSet = true;
                }
            }

            if (!cursorSet)
            {
                SetCursorShape(StandardCursorType.Arrow);
            }
        }
        else
        {
            SetCursorShape(StandardCursorType.Cross);
        }
        // ------------------

        if (_isResizing)
        {
             // Calculate delta
             var deltaX = currentPoint.X - _resizeStartPoint.X;
             var deltaY = currentPoint.Y - _resizeStartPoint.Y;
             
             double x = _originalRect.X;
             double y = _originalRect.Y;
             double w = _originalRect.Width;
             double h = _originalRect.Height;

             switch (_resizeDirection)
             {
                 case ResizeDirection.TopLeft:
                     x += deltaX; y += deltaY; w -= deltaX; h -= deltaY; break;
                 case ResizeDirection.TopRight:
                     y += deltaY; w += deltaX; h -= deltaY; break;
                 case ResizeDirection.BottomLeft:
                     x += deltaX; w -= deltaX; h += deltaY; break;
                 case ResizeDirection.BottomRight:
                     w += deltaX; h += deltaY; break;
                 case ResizeDirection.Top:
                     y += deltaY; h -= deltaY; break;
                 case ResizeDirection.Bottom:
                     h += deltaY; break;
                 case ResizeDirection.Left:
                     x += deltaX; w -= deltaX; break;
                 case ResizeDirection.Right:
                     w += deltaX; break;
             }

             if (w < 0) { x += w; w = Math.Abs(w); }
             if (h < 0) { y += h; h = Math.Abs(h); }
             
             _viewModel.SelectionRect = new Rect(x, y, w, h);
             return;
        }
        
        if (_isMovingSelection)
        {
             var deltaX = currentPoint.X - _moveStartPoint.X;
             var deltaY = currentPoint.Y - _moveStartPoint.Y;
             
             _viewModel.SelectionRect = new Rect(
                 _originalRect.X + deltaX,
                 _originalRect.Y + deltaY,
                 _originalRect.Width,
                 _originalRect.Height);
             return;
        }
        
        if (_isDraggingAnnotation && _draggingAnnotation != null)
        {
             var selectionSpacePoint = new Point(currentPoint.X - _viewModel.SelectionRect.X, currentPoint.Y - _viewModel.SelectionRect.Y);
             _draggingAnnotation.StartPoint = new Point(selectionSpacePoint.X - _dragOffset.X, selectionSpacePoint.Y - _dragOffset.Y);
             _draggingAnnotation.EndPoint = _draggingAnnotation.StartPoint; 
             return;
        }

        if (_viewModel.CurrentState == SnipState.Selecting)
        {
            var x = Math.Min(_startPoint.X, currentPoint.X);
            var y = Math.Min(_startPoint.Y, currentPoint.Y);
            var width = Math.Abs(currentPoint.X - _startPoint.X);
            var height = Math.Abs(currentPoint.Y - _startPoint.Y);

            _viewModel.SelectionRect = new Rect(x, y, width, height);
        }
        else if (_viewModel.CurrentState == SnipState.Detecting)
        {
            _viewModel.UpdateDetectedRect(currentPoint);
        }
        else if (_viewModel.CurrentState == SnipState.Selected && _currentAnnotation != null)
        {
            var relPoint = new Point(currentPoint.X - _viewModel.SelectionRect.X, currentPoint.Y - _viewModel.SelectionRect.Y);
            if (_currentAnnotation.Type == AnnotationType.Pen)
            {
                _currentAnnotation.AddPoint(relPoint);
            }
            else
            {
                _currentAnnotation.EndPoint = relPoint;
            }
        }
    }

    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_viewModel == null) return;
        
        if (_isResizing)
        {
             _isResizing = false;
            _resizeDirection = ResizeDirection.None;
             _viewModel.CurrentState = SnipState.Selected;
             return;
        }

        if (_isMovingSelection)
        {
            _isMovingSelection = false;
            return;
        }
        
        if (_isDraggingAnnotation)
        {
            _isDraggingAnnotation = false;
            _draggingAnnotation = null;
            return;
        }
        
        if (_viewModel.CurrentState == SnipState.Selecting)
        {
             var currentPoint = e.GetPosition(this);
             var dist = Math.Sqrt(Math.Pow(currentPoint.X - _startPoint.X, 2) + Math.Pow(currentPoint.Y - _startPoint.Y, 2));
             
             if (dist < 5 && _viewModel.DetectedRect.Width > 0)
             {
                 _viewModel.SelectionRect = _viewModel.DetectedRect;
             }
             
             _viewModel.CurrentState = SnipState.Selected;
        }

        if (_currentAnnotation != null)
        {
            _currentAnnotation = null;
        }
    }
}
