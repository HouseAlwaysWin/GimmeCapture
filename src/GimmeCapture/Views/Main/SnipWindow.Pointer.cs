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
    // Translation mode multi-selection fields
    private bool _isTranslationSelecting;
    private Point _translationSelectionStart;
    private UserSelectionRect? _currentTranslationSelection;

    // Toolbar dragging fields
    private bool _isDraggingToolbar;
    private Point _toolbarDragOffset;

    // Translation selection move fields
    private bool _isMovingTranslationSelection;
    private Point _translationMoveStart;
    private UserSelectionRect? _movingTranslationSelection;
    private Rect _originalTranslationBounds;

    // Translation selection resize fields
    private bool _isResizingTranslation;
    private ResizeDirection _translationResizeDirection;
    private UserSelectionRect? _resizingTranslationItem;
    private Rect _originalTranslationRect;
    private Point _translationResizeStartPoint;

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

        if (sourceControl != null && props.IsLeftButtonPressed && isMoveHandle && !_viewModel.IsTranslationSelectionActive)
        {
            var sel = sourceControl.DataContext as GimmeCapture.Models.UserSelectionRect;
            if (sel == null)
            {
                var parent = sourceControl.GetVisualParent();
                while (parent != null)
                {
                    if (parent is Control c && c.DataContext is GimmeCapture.Models.UserSelectionRect found)
                    {
                        sel = found;
                        break;
                    }
                    parent = parent.GetVisualParent();
                }
            }

            if (sel != null)
            {
                if (props.IsRightButtonPressed) return;

                _isMovingTranslationSelection = true;
                _movingTranslationSelection = sel;
                _translationMoveStart = point;
                _originalTranslationBounds = sel.Bounds;
                e.Pointer.Capture(this);
                e.Handled = true;
                return;
            }
            else if (_viewModel.RecState == RecordingState.Idle && _viewModel.CurrentState == SnipState.Selected)
            {
                _isMovingSelection = true;
                _moveStartPoint = point;
                _originalRect = _viewModel.SelectionRect;
                e.Handled = true;
                return;
            }
        }

        if (sourceControl != null && props.IsLeftButtonPressed && isResizeHandle && !_viewModel.IsTranslationSelectionActive)
        {
            var sel = sourceControl.DataContext as GimmeCapture.Models.UserSelectionRect;
            if (sel == null)
            {
                var parent = sourceControl.GetVisualParent();
                while (parent != null)
                {
                    if (parent is Control c && c.DataContext is GimmeCapture.Models.UserSelectionRect found)
                    {
                        sel = found;
                        break;
                    }
                    parent = parent.GetVisualParent();
                }
            }

            if (sel != null)
            {
                _isResizingTranslation = true;
                _translationResizeDirection = GetDirectionFromName(sourceControl.Name);
                _resizingTranslationItem = sel;
                _originalTranslationRect = sel.Bounds;
                _translationResizeStartPoint = point;
                e.Pointer.Capture(this);
                e.Handled = true;
                return;
            }

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

        if (props.IsLeftButtonPressed)
        {
            // Check if clicking on SnipToolbar
            Control? toolbarAncestor = sourceControl;
            while (toolbarAncestor != null && toolbarAncestor is not Views.Controls.SnipToolbar)
            {
                toolbarAncestor = toolbarAncestor.GetVisualParent() as Control;
            }

            if (toolbarAncestor is Views.Controls.SnipToolbar toolbar)
            {
                // If clicking a button, let the button handle it
                if (sourceControl is Button || sourceControl?.FindAncestorOfType<Button>() != null)
                {
                    return;
                }

                // Otherwise, start dragging the toolbar
                _isDraggingToolbar = true;
                _toolbarDragOffset = e.GetPosition(toolbar); // Position relative to toolbar
                e.Pointer.Capture(this);
                e.Handled = true;
                return;
            }
        }

        if (props.IsLeftButtonPressed)
        {
            // Translation mode: selection and interaction (V7+ Restructured)
            if (_viewModel.IsTranslationMode)
            {
                // B. Selection Creation (Only if no handle/corner hit and selection-active)

                // C. Selection Creation (Only if no handle/corner hit and selection-active)
                if (_viewModel.IsTranslationSelectionActive)
                {
                    // V8 Selection Mode: Always allow starting a new selection, ignoring existing hits
                    _isTranslationSelecting = true;
                    _translationSelectionStart = point;
                    _currentTranslationSelection = new UserSelectionRect { Bounds = new Rect(point, new Size(0, 0)) };
                    _viewModel.UserSelections.Add(_currentTranslationSelection);
                    
                    // Force mask update immediately for feedback
                    _viewModel.UpdateMask();
                    
                    e.Pointer.Capture(this);
                    e.Handled = true;
                    return;
                }
                
                // D. 翻譯模式下一律 return，不讓事件掉入標準選取邏輯
                // 已翻譯的區域在 Bubble phase 已被 SelectableTextBlock 接收
                return;
            }
        }
        
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
                    FontSize = _viewModel.CurrentFontSize,
                    DrawingModeSnapshot = _viewModel.DrawingModeSnapshot
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

        // 翻譯模式下非左鍵事件（右鍵等）不進入標準選取邏輯
        // ContextMenu 由 XAML Border/SelectableTextBlock 處理
        if (_viewModel.IsTranslationMode) return;

        // If clicking OUTSIDE or in Idle/Detecting, start NEW selection
        // Check if the click is within the toolbar bounds (coordinate-based check)
        var toolbarControl = this.FindControl<SnipToolbar>("Toolbar");
        if (toolbarControl != null && toolbarControl.IsVisible)
        {
            // Get toolbar bounds in window coordinates
            var toolbarBounds = toolbarControl.Bounds;
            var toolbarPos = toolbarControl.TranslatePoint(new Point(0, 0), this);
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
        
        // 修正：只有左鍵才能開啟新的選取框
        if (props.IsLeftButtonPressed && _viewModel.RecState == RecordingState.Idle && 
            (_viewModel.CurrentState == SnipState.Idle || _viewModel.CurrentState == SnipState.Detecting))
        {
            _startPoint = point;
            _viewModel.CurrentState = SnipState.Selecting;
            _viewModel.SelectionRect = new Rect(_startPoint, new Size(0, 0));
            _viewModel.IsDrawingMode = false;
            _viewModel.ClearAnnotationsCommand.Execute().Subscribe();
            e.Handled = true;
            return;
        }
        else if (props.IsLeftButtonPressed && _viewModel.RecState == RecordingState.Idle && _viewModel.CurrentState == SnipState.Selected)
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
                 e.Handled = true;
                 return;
            }
        }
        
        // Final Else-If for Right Button
        else if (props.IsRightButtonPressed)
        {
            if (_viewModel == null) return;
            if (_viewModel.IsTranslationMode) return; // V7+: Handled by ContextMenu on Text
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

        // --- Handle Cursors ---
        bool specialCursorSet = false;

        // 如果在工具列上方，強制使用箭頭游標
        var hitControl = this.InputHitTest(currentPoint) as Visual;
        bool isOverToolbar = false;
        var parent = hitControl;
        while (parent != null)
        {
            if (parent is SnipToolbar || (parent is Control c && c.Name == "Toolbar"))
            {
                isOverToolbar = true;
                break;
            }
            parent = parent.GetVisualParent();
        }

        if (isOverToolbar)
        {
            SetCursorShape(StandardCursorType.Arrow);
            specialCursorSet = true;
        }
        else if (_isDraggingToolbar || _isMovingTranslationSelection || _isMovingSelection || _isDraggingAnnotation)
        {
            SetCursorShape(StandardCursorType.SizeAll);
            specialCursorSet = true;
        }
        else if (_isResizing || _isResizingTranslation)
        {
            var dir = _isResizing ? _resizeDirection : _translationResizeDirection;
            switch (dir)
            {
                // 注意：這裡假設 ResizeDirection 已經定義了對應的方向
                case ResizeDirection.TopLeft: SetCursorShape(StandardCursorType.TopLeftCorner); break;
                case ResizeDirection.TopRight: SetCursorShape(StandardCursorType.TopRightCorner); break;
                case ResizeDirection.BottomLeft: SetCursorShape(StandardCursorType.BottomLeftCorner); break;
                case ResizeDirection.BottomRight: SetCursorShape(StandardCursorType.BottomRightCorner); break;
                case ResizeDirection.Top: case ResizeDirection.Bottom: SetCursorShape(StandardCursorType.SizeNorthSouth); break;
                case ResizeDirection.Left: case ResizeDirection.Right: SetCursorShape(StandardCursorType.SizeWestEast); break;
                default: SetCursorShape(StandardCursorType.SizeAll); break;
            }
            specialCursorSet = true;
        }

        if (!specialCursorSet)
        {
            bool actionCursorSet = false;
            if (_viewModel.CurrentState == SnipState.Selected || _viewModel.IsTranslationMode)
            {
                // 1. Text Annotation Hover (Hand Cursor)
                if (_viewModel.CurrentState == SnipState.Selected && _viewModel.IsDrawingMode && _viewModel.CurrentAnnotationTool == AnnotationType.Text)
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
                                actionCursorSet = true;
                                break;
                            }
                        }
                    }
                }

                // 2. Handle Hover (SizeAll) or Drawing Cursors
                if (!actionCursorSet)
                {
                    var hit = this.InputHitTest(currentPoint) as Control;
                    bool isResizeHandle = hit != null && hit.Classes.Contains("Handle");
                    bool isMoveHandle = hit != null && hit.Classes.Contains("MoveHandle");

                    if (isResizeHandle)
                    {
                        var dir = GetDirectionFromName(hit?.Name);
                        switch (dir)
                        {
                            case ResizeDirection.TopLeft: SetCursorShape(StandardCursorType.TopLeftCorner); break;
                            case ResizeDirection.TopRight: SetCursorShape(StandardCursorType.TopRightCorner); break;
                            case ResizeDirection.BottomLeft: SetCursorShape(StandardCursorType.BottomLeftCorner); break;
                            case ResizeDirection.BottomRight: SetCursorShape(StandardCursorType.BottomRightCorner); break;
                            case ResizeDirection.Top: case ResizeDirection.Bottom: SetCursorShape(StandardCursorType.SizeNorthSouth); break;
                            case ResizeDirection.Left: case ResizeDirection.Right: SetCursorShape(StandardCursorType.SizeWestEast); break;
                            default: SetCursorShape(StandardCursorType.SizeAll); break;
                        }
                        actionCursorSet = true;
                    }
                    else if (isMoveHandle || (!_viewModel.IsTranslationMode && !_viewModel.IsDrawingMode && _viewModel.SelectionRect.Contains(currentPoint)))
                    {
                        SetCursorShape(StandardCursorType.SizeAll);
                        actionCursorSet = true;
                    }
                    else if (!_viewModel.IsTranslationMode && _viewModel.IsDrawingMode && _viewModel.SelectionRect.Contains(currentPoint))
                    {
                        if (_viewModel.CurrentAnnotationTool == AnnotationType.Text)
                        {
                            SetCursorShape(StandardCursorType.Ibeam);
                        }
                        else
                        {
                            SetCursorShape(StandardCursorType.Cross);
                        }
                        actionCursorSet = true;
                    }
                }
            }
            
            // 3. Fallback to standard Selection/Translation Cross cursors if no action handles were hit
            if (!actionCursorSet)
            {
                if (_viewModel.IsTranslationMode && _viewModel.IsTranslationSelectionActive)
                {
                    SetCursorShape(StandardCursorType.Cross);
                }
                else if (_viewModel.CurrentState == SnipState.Selecting || _viewModel.CurrentState == SnipState.Detecting || _isTranslationSelecting)
                {
                    SetCursorShape(StandardCursorType.Cross);
                }
                else
                {
                    SetCursorShape(StandardCursorType.Arrow);
                }
            }
        }

        // --- Execution Logic ---

        // 1. Toolbar Dragging
        if (_isDraggingToolbar)
        {
            _viewModel.ToolbarLeft = currentPoint.X - _toolbarDragOffset.X;
            _viewModel.ToolbarTop = currentPoint.Y - _toolbarDragOffset.Y;
            _viewModel.IsToolbarManuallyPositioned = true;
            return;
        }

        // Translation Selection Moving
        if (_isResizingTranslation && _resizingTranslationItem != null)
        {
            var dx = currentPoint.X - _translationResizeStartPoint.X;
            var dy = currentPoint.Y - _translationResizeStartPoint.Y;

            double newX = _originalTranslationRect.X;
            double newY = _originalTranslationRect.Y;
            double newW = _originalTranslationRect.Width;
            double newH = _originalTranslationRect.Height;

            switch (_translationResizeDirection)
            {
                case ResizeDirection.TopLeft:
                    newX += dx; newY += dy; newW -= dx; newH -= dy;
                    break;
                case ResizeDirection.TopRight:
                    newY += dy; newW += dx; newH -= dy;
                    break;
                case ResizeDirection.BottomLeft:
                    newX += dx; newW -= dx; newH += dy;
                    break;
                case ResizeDirection.BottomRight:
                    newW += dx; newH += dy;
                    break;
                case ResizeDirection.Top:
                    newY += dy; newH -= dy;
                    break;
                case ResizeDirection.Bottom:
                    newH += dy;
                    break;
                case ResizeDirection.Left:
                    newX += dx; newW -= dx;
                    break;
                case ResizeDirection.Right:
                    newW += dx;
                    break;
            }

            // Apply limits (Min size 40x40 for stability V7+)
            if (newW < 40) newW = 40;
            if (newH < 40) newH = 40;
            
            _resizingTranslationItem.Bounds = new Rect(newX, newY, Math.Max(0, newW), Math.Max(0, newH));
            _viewModel.UpdateMask();
            e.Handled = true;
            return;
        }

        if (_isMovingTranslationSelection && _movingTranslationSelection != null)
        {
            var deltaX = currentPoint.X - _translationMoveStart.X;
            var deltaY = currentPoint.Y - _translationMoveStart.Y;
            _movingTranslationSelection.Bounds = new Rect(
                _originalTranslationBounds.X + deltaX,
                _originalTranslationBounds.Y + deltaY,
                _originalTranslationBounds.Width,
                _originalTranslationBounds.Height);
            return;
        }

        // 2. Selection Resizing
        if (_isResizing)
        {
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
        
        // 3. Selection Moving
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
        
        // 4. Annotation Dragging
        if (_isDraggingAnnotation && _draggingAnnotation != null)
        {
             var selectionSpacePoint = new Point(currentPoint.X - _viewModel.SelectionRect.X, currentPoint.Y - _viewModel.SelectionRect.Y);
             _draggingAnnotation.StartPoint = new Point(selectionSpacePoint.X - _dragOffset.X, selectionSpacePoint.Y - _dragOffset.Y);
             _draggingAnnotation.EndPoint = _draggingAnnotation.StartPoint; 
             return;
        }

        // 5. Normal Selection / Translation Selection / Detecting
        if (_viewModel.CurrentState == SnipState.Selecting)
        {
            var x = Math.Min(_startPoint.X, currentPoint.X);
            var y = Math.Min(_startPoint.Y, currentPoint.Y);
            var width = Math.Abs(currentPoint.X - _startPoint.X);
            var height = Math.Abs(currentPoint.Y - _startPoint.Y);
            _viewModel.SelectionRect = new Rect(x, y, width, height);
        }
        else if (_isTranslationSelecting && _currentTranslationSelection != null)
        {
            var x = Math.Min(_translationSelectionStart.X, currentPoint.X);
            var y = Math.Min(_translationSelectionStart.Y, currentPoint.Y);
            var width = Math.Abs(currentPoint.X - _translationSelectionStart.X);
            var height = Math.Abs(currentPoint.Y - _translationSelectionStart.Y);
            _currentTranslationSelection.Bounds = new Rect(x, y, width, height);
        }
        else if (_viewModel.CurrentState == SnipState.Detecting && !_viewModel.IsTranslationMode)
        {
            _viewModel.UpdateDetectedRect(currentPoint);
        }
        else if (_viewModel.CurrentState == SnipState.Selected && _currentAnnotation != null)
        {
            var relPoint = new Point(currentPoint.X - _viewModel.SelectionRect.X, currentPoint.Y - _viewModel.SelectionRect.Y);
            if (_currentAnnotation.Type == AnnotationType.Pen)
                _currentAnnotation.AddPoint(relPoint);
            else
                _currentAnnotation.EndPoint = relPoint;
        }
    }

    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_viewModel == null) return;
        
        if (_isDraggingToolbar)
        {
            _isDraggingToolbar = false;
            e.Pointer.Capture(null);
            e.Handled = true;
            return;
        }

        if (_isMovingTranslationSelection)
        {
            _isMovingTranslationSelection = false;
            _movingTranslationSelection = null;
            e.Pointer.Capture(null);
            e.Handled = true;
            return;
        }

        if (_isResizing)
        {
             _isResizing = false;
             _resizeDirection = ResizeDirection.None;
             _viewModel.CurrentState = SnipState.Selected;
             e.Pointer.Capture(null);
             return;
        }

        if (_isResizingTranslation)
        {
            _isResizingTranslation = false;
            _resizingTranslationItem = null;
            _translationResizeDirection = ResizeDirection.None;
            e.Pointer.Capture(null);
            e.Handled = true;
            return;
        }

        if (_isMovingSelection)
        {
            _isMovingSelection = false;
            e.Pointer.Capture(null);
            return;
        }
        
        if (_isDraggingAnnotation)
        {
            _isDraggingAnnotation = false;
            _draggingAnnotation = null;
            e.Pointer.Capture(null);
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

        // Translation mode: finish selection
        if (_isTranslationSelecting)
        {
            _isTranslationSelecting = false;
            if (_currentTranslationSelection != null)
            {
                // Remove if too small (accidental clicks)
                if (_currentTranslationSelection.Bounds.Width < 10 || _currentTranslationSelection.Bounds.Height < 5)
                {
                    _viewModel.UserSelections.Remove(_currentTranslationSelection);
                }
                
                // Finalize mask and region
                _viewModel.UpdateMask();
                _currentTranslationSelection = null;
            }
            return;
        }

        if (_currentAnnotation != null)
        {
            _currentAnnotation = null;
        }
    }
}
