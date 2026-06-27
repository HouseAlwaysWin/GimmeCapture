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
using System.Reactive;
using ReactiveUI;

namespace GimmeCapture.Views.Main;

public partial class SnipWindow : Window
{
    // Translation mode multi-selection fields
    private Point _translationSelectionStart;
    private UserSelectionRect? _currentTranslationSelection;

    // Toolbar dragging fields
    private Point _toolbarDragOffset;

    // Translation selection move fields
    private Point _translationMoveStart;
    private UserSelectionRect? _movingTranslationSelection;
    private Rect _originalTranslationBounds;

    // Translation selection resize fields
    private ResizeDirection _translationResizeDirection;
    private UserSelectionRect? _resizingTranslationItem;
    private Rect _originalTranslationRect;
    private Point _translationResizeStartPoint;

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_viewModel == null) return;
        var source = e.Source as Control;

        // Debounce: If we just finished text entry, ignore clicks for a short moment
        if ((DateTime.Now - _lastTextFinishTime).TotalMilliseconds < 300)
        {
            e.Handled = true;
            return;
        }

        // Prevent recursive text entry (If clicking to finish text, don't restart it immediately)
        if (_viewModel.IsEnteringText)
        {
            if (source is TextEntryOverlay
                || source?.FindAncestorOfType<TextEntryOverlay>() != null)
            {
                return;
            }

            _viewModel.ConfirmTextEntryCommand.Execute(Unit.Default).Subscribe();
            _lastTextFinishTime = DateTime.Now;

            // Let the same click reach another toolbar button so switching tools is immediate.
            if (source is SnipToolbar || source?.FindAncestorOfType<SnipToolbar>() != null)
            {
                return;
            }

            e.Handled = true;
            return;
        }

        var point = e.GetPosition(this);
        var props = e.GetCurrentPoint(this).Properties;

        var isDrawingUiInteraction = source is Button
            or TextBox
            or ComboBox
            or Slider
            || source?.FindAncestorOfType<Button>() != null
            || source?.FindAncestorOfType<TextBox>() != null
            || source?.FindAncestorOfType<ComboBox>() != null
            || source?.FindAncestorOfType<Slider>() != null
            || source is SnipToolbar
            || source?.FindAncestorOfType<SnipToolbar>() != null;

        if (!isDrawingUiInteraction)
        {
            if (TryHandleTextAnnotationPressed(point, e))
                return;

            if (TryHandleEditableAnnotationPressed(point, e))
                return;
        }

        if (!_viewModel.IsTranslationMode
            && !_viewModel.IsDrawingMode
            && _viewModel.RecState == RecordingState.Idle
            && _viewModel.CurrentState == SnipState.Selected
            && props.IsLeftButtonPressed)
        {
            var interactionZone = HitTestSelectionInteraction(point);
            if (interactionZone != SelectionInteractionZone.None)
            {
                if (interactionZone == SelectionInteractionZone.MoveTop)
                {
                    _pointerState = PointerInteractionState.MovingSelection;
                    _moveStartPoint = point;
                    _originalRect = _viewModel.SelectionRect;
                    e.Pointer.Capture(this);
                    e.Handled = true;
                    return;
                }

                _pointerState = PointerInteractionState.ResizingSelection;
                _resizeDirection = interactionZone switch
                {
                    SelectionInteractionZone.ResizeTopLeft => ResizeDirection.TopLeft,
                    SelectionInteractionZone.ResizeTopRight => ResizeDirection.TopRight,
                    SelectionInteractionZone.ResizeBottomLeft => ResizeDirection.BottomLeft,
                    SelectionInteractionZone.ResizeBottomRight => ResizeDirection.BottomRight,
                    SelectionInteractionZone.ResizeLeft => ResizeDirection.Left,
                    SelectionInteractionZone.ResizeRight => ResizeDirection.Right,
                    SelectionInteractionZone.ResizeBottom => ResizeDirection.Bottom,
                    _ => ResizeDirection.None
                };

                if (_resizeDirection != ResizeDirection.None)
                {
                    _resizeStartPoint = point;
                    _originalRect = _viewModel.SelectionRect;
                    e.Pointer.Capture(this);
                    e.Handled = true;
                    return;
                }
            }
        }

        // Check for handle interaction (Move or Resize)
        // prioritized before drawing logic to allow adjustment while tool is active
        var sourceControl = e.Source as Control;
        bool isResizeHandle = sourceControl != null && sourceControl.Classes.Contains("Handle");
        bool isMoveHandle = sourceControl != null &&
            (sourceControl.Classes.Contains("MoveHandle")
             || sourceControl.Name?.Contains("InnerCorner") == true
             || HasClassInAncestors(sourceControl, "TranslationItem"));
        
        System.Diagnostics.Debug.WriteLine($"[PointerPressed] isResize: {isResizeHandle}, isMove: {isMoveHandle}");

        if (sourceControl != null && props.IsLeftButtonPressed && !isResizeHandle && (isMoveHandle || ResolveTranslationSelectionFromVisualTree(sourceControl) != null))
        {
            System.Diagnostics.Debug.WriteLine($"[PointerPressed] Entered MoveHandle block");
            var sel = ResolveTranslationSelectionFromVisualTree(sourceControl);

            if (sel != null)
            {
                if (props.IsRightButtonPressed) return;

                _pointerState = PointerInteractionState.MovingTranslationBox;
                _movingTranslationSelection = sel;
                _translationMoveStart = point;
                _originalTranslationBounds = sel.Bounds;
                e.Pointer.Capture(this);
                e.Handled = true;
                return;
            }
            else if (_viewModel.RecState == RecordingState.Idle && _viewModel.CurrentState == SnipState.Selected)
            {
                _pointerState = PointerInteractionState.MovingSelection;
                _moveStartPoint = point;
                _originalRect = _viewModel.SelectionRect;
                e.Handled = true;
                return;
            }
        }

        if (sourceControl != null && props.IsLeftButtonPressed && isResizeHandle)
        {
            System.Diagnostics.Debug.WriteLine($"[PointerPressed] Entered ResizeHandle block");
            var sel = ResolveTranslationSelectionFromVisualTree(sourceControl);

            if (sel != null)
            {
                _pointerState = PointerInteractionState.ResizingTranslationBox;
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
            _pointerState = PointerInteractionState.ResizingSelection;
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
                // All modes now use a fixed top-center toolbar that can be dragged by its grip handle
                // (translation's strip or the screenshot/recording handle). Buttons stay clickable.
                if (IsInteractiveToolbarControl(sourceControl))
                {
                    return;
                }

                if (!IsToolbarDragHandleHit(sourceControl))
                {
                    return;
                }

                // Otherwise, start dragging the toolbar
                _pointerState = PointerInteractionState.DraggingToolbar;
                _toolbarDragOffset = e.GetPosition(toolbar); // Position relative to toolbar
                e.Pointer.Capture(this);
                e.Handled = true;
                return;
            }
        }

        if (_viewModel.IsTranslationMode)
        {
            TryHandleTranslationPointerPressed(point, e);
            return;
        }

        if (TryHandleDrawingStartPressed(point, e))
            return;


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
        else if (_pointerState == PointerInteractionState.DraggingToolbar || _pointerState == PointerInteractionState.MovingTranslationBox || _pointerState == PointerInteractionState.MovingSelection)
        {
            SetCursorShape(StandardCursorType.SizeAll);
            specialCursorSet = true;
        }
        else if (_pointerState == PointerInteractionState.DraggingAnnotation)
        {
            SetCursorForAnnotationZone(_annotationHitZone);
            specialCursorSet = true;
        }
        else if (_pointerState == PointerInteractionState.ResizingSelection || _pointerState == PointerInteractionState.ResizingTranslationBox)
        {
            var dir = _pointerState == PointerInteractionState.ResizingSelection ? _resizeDirection : _translationResizeDirection;
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
                if (!_viewModel.IsTranslationMode
                    && _viewModel.IsDrawingMode
                    && _viewModel.CurrentAnnotationTool != AnnotationType.None
                    && _viewModel.SelectionRect.Inflate(AnnotationInteractionService.HandleRadius + 2).Contains(currentPoint))
                {
                    var relativePoint = new Point(
                        currentPoint.X - _viewModel.SelectionRect.X,
                        currentPoint.Y - _viewModel.SelectionRect.Y);
                    var annotationHit = AnnotationInteractionService.HitTest(
                        _viewModel.Annotations,
                        _viewModel.SelectedAnnotation,
                        relativePoint);
                    if (annotationHit.IsHit)
                    {
                        SetCursorForAnnotationZone(annotationHit.Zone);
                        actionCursorSet = true;
                    }
                }

                if (!_viewModel.IsTranslationMode && !_viewModel.IsDrawingMode && _viewModel.CurrentState == SnipState.Selected)
                {
                    var interactionZone = HitTestSelectionInteraction(currentPoint);
                    switch (interactionZone)
                    {
                        case SelectionInteractionZone.MoveTop:
                            SetCursorShape(StandardCursorType.SizeAll);
                            actionCursorSet = true;
                            break;
                        case SelectionInteractionZone.ResizeTopLeft:
                            SetCursorShape(StandardCursorType.TopLeftCorner);
                            actionCursorSet = true;
                            break;
                        case SelectionInteractionZone.ResizeTopRight:
                            SetCursorShape(StandardCursorType.TopRightCorner);
                            actionCursorSet = true;
                            break;
                        case SelectionInteractionZone.ResizeBottomLeft:
                            SetCursorShape(StandardCursorType.BottomLeftCorner);
                            actionCursorSet = true;
                            break;
                        case SelectionInteractionZone.ResizeBottomRight:
                            SetCursorShape(StandardCursorType.BottomRightCorner);
                            actionCursorSet = true;
                            break;
                        case SelectionInteractionZone.ResizeLeft:
                        case SelectionInteractionZone.ResizeRight:
                            SetCursorShape(StandardCursorType.SizeWestEast);
                            actionCursorSet = true;
                            break;
                        case SelectionInteractionZone.ResizeBottom:
                            SetCursorShape(StandardCursorType.SizeNorthSouth);
                            actionCursorSet = true;
                            break;
                    }
                }

                // 1. Text Annotation Hover (Hand Cursor)
                if (!actionCursorSet && _viewModel.CurrentState == SnipState.Selected && _viewModel.IsDrawingMode && _viewModel.CurrentAnnotationTool == AnnotationType.Text)
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
                    bool isMoveHandle = hit != null && (hit.Classes.Contains("MoveHandle") || HasClassInAncestors(hit, "TranslationItem") || ResolveTranslationSelectionFromVisualTree(hit) != null);

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
                    else if (isMoveHandle)
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
                bool trSelMod = IsTranslationSelectionModifierDownForRegion();
                // Translation: cross while selection modifier active (or None) or actively drawing a rect.
                if (_viewModel.IsTranslationMode && (_pointerState == PointerInteractionState.TranslationSelecting || trSelMod))
                {
                    SetCursorShape(StandardCursorType.Cross);
                }
                else if (!_viewModel.IsTranslationMode
                    && (_viewModel.CurrentState == SnipState.Idle
                        || _viewModel.CurrentState == SnipState.Selecting
                        || _viewModel.CurrentState == SnipState.Detecting))
                {
                    // Show the crosshair immediately on entry (Idle), not only once a drag begins, so it's clear
                    // you can draw a selection right away — matching the toolbar now being visible on entry.
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
        if (_pointerState == PointerInteractionState.DraggingToolbar)
        {
            double tw = _viewModel.ToolbarWidth > 0 ? _viewModel.ToolbarWidth : 600;
            double th = _viewModel.ToolbarHeight > 0 ? _viewModel.ToolbarHeight : 45;
            Rect clampBounds = _viewModel.ActiveScreenBounds.Width > 0
                ? _viewModel.ActiveScreenBounds
                : new Rect(0, 0,
                    _viewModel.ViewportSize.Width > 0 ? _viewModel.ViewportSize.Width : 1920,
                    _viewModel.ViewportSize.Height > 0 ? _viewModel.ViewportSize.Height : 1080);

            double left = Math.Clamp(
                currentPoint.X - _toolbarDragOffset.X,
                clampBounds.X,
                Math.Max(clampBounds.X, clampBounds.Right - tw));
            double top = Math.Clamp(
                currentPoint.Y - _toolbarDragOffset.Y,
                clampBounds.Y,
                Math.Max(clampBounds.Y, clampBounds.Bottom - th));

            _viewModel.ToolbarLeft = left;
            _viewModel.ToolbarTop = top;
            if (_viewModel.IsTranslationMode)
            {
                _viewModel.TranslationToolbarLeft = left;
                _viewModel.TranslationToolbarTop = top;
            }
            _viewModel.IsToolbarManuallyPositioned = true;
            return;
        }

        if (TryHandleTranslationPointerMoved(currentPoint, e))
            return;

        // 2. Selection Resizing
        if (_pointerState == PointerInteractionState.ResizingSelection)
        {
             var deltaX = currentPoint.X - _resizeStartPoint.X;
             var deltaY = currentPoint.Y - _resizeStartPoint.Y;

             var (x, y, w, h) = SelectionResizeMath.ApplyResizeDelta(_originalRect, _resizeDirection, deltaX, deltaY);
             _viewModel.SelectionRect = SelectionResizeMath.NormalizeRect(x, y, w, h);
             return;
        }
        
        // 3. Selection Moving
        if (_pointerState == PointerInteractionState.MovingSelection)
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
        
        if (TryHandleAnnotationPointerMoved(currentPoint))
            return;

        if (_viewModel.CurrentState == SnipState.Selecting)
        {
            var x = Math.Min(_startPoint.X, currentPoint.X);
            var y = Math.Min(_startPoint.Y, currentPoint.Y);
            var width = Math.Abs(currentPoint.X - _startPoint.X);
            var height = Math.Abs(currentPoint.Y - _startPoint.Y);
            _viewModel.SelectionRect = new Rect(x, y, width, height);
        }
        else if (_viewModel.CurrentState == SnipState.Detecting && !_viewModel.IsTranslationMode)
        {
            _viewModel.UpdateWindowHover(currentPoint);
        }
    }

    private void SetCursorForAnnotationZone(AnnotationHitZone zone)
    {
        switch (zone)
        {
            case AnnotationHitZone.TopLeft:
            case AnnotationHitZone.BottomRight:
                SetCursorShape(StandardCursorType.TopLeftCorner);
                break;
            case AnnotationHitZone.TopRight:
            case AnnotationHitZone.BottomLeft:
                SetCursorShape(StandardCursorType.TopRightCorner);
                break;
            case AnnotationHitZone.Top:
            case AnnotationHitZone.Bottom:
                SetCursorShape(StandardCursorType.SizeNorthSouth);
                break;
            case AnnotationHitZone.Left:
            case AnnotationHitZone.Right:
                SetCursorShape(StandardCursorType.SizeWestEast);
                break;
            case AnnotationHitZone.StartPoint:
            case AnnotationHitZone.EndPoint:
                SetCursorShape(StandardCursorType.Hand);
                break;
            default:
                SetCursorShape(StandardCursorType.SizeAll);
                break;
        }
    }

    private static bool IsToolbarDragHandleHit(Control? sourceControl)
    {
        for (Control? current = sourceControl; current != null; current = current.GetVisualParent() as Control)
        {
            if (string.Equals(current.Name, "TranslationToolbarDragStrip", StringComparison.Ordinal)
                || string.Equals(current.Name, "ToolbarDragHandle", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsInteractiveToolbarControl(Control? sourceControl)
    {
        for (Control? current = sourceControl; current != null; current = current.GetVisualParent() as Control)
        {
            if (current is Button
                or TextBox
                or ComboBox
                or Slider
                or Avalonia.Controls.Primitives.Thumb
                or SelectableTextBlock
                or ICommandSource)
            {
                return true;
            }

            if (current is SnipToolbar)
            {
                break;
            }
        }

        return false;
    }

    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_viewModel == null) return;
        
        if (_pointerState == PointerInteractionState.DraggingToolbar)
        {
            _pointerState = PointerInteractionState.None;
            e.Pointer.Capture(null);
            e.Handled = true;
            return;
        }

        if (TryHandleTranslationPointerReleased(e))
            return;

        if (_pointerState == PointerInteractionState.ResizingSelection)
        {
             _pointerState = PointerInteractionState.None;
             _resizeDirection = ResizeDirection.None;
             _viewModel.CurrentState = SnipState.Selected;
             _viewModel.RefreshSelectedSnapshotLock(forceCapture: true);
             e.Pointer.Capture(null);
             return;
        }

        if (_pointerState == PointerInteractionState.MovingSelection)
        {
            _pointerState = PointerInteractionState.None;
            _viewModel.RefreshSelectedSnapshotLock(forceCapture: true);
            e.Pointer.Capture(null);
            return;
        }
        
        if (_viewModel.CurrentState == SnipState.Selecting)
        {
             var currentPoint = e.GetPosition(this);
             var dist = Math.Sqrt(Math.Pow(currentPoint.X - _startPoint.X, 2) + Math.Pow(currentPoint.Y - _startPoint.Y, 2));
             
             if (dist < 5 && _viewModel.HoverTargetRect.Width > 0)
             {
                 _viewModel.SelectionRect = _viewModel.HoverTargetRect;
             }
             
             _viewModel.CurrentState = SnipState.Selected;
        }

        TryHandleAnnotationPointerReleased(e);
    }
}
