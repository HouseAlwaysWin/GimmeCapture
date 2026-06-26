using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using Avalonia.Controls.Primitives;
using GimmeCapture.ViewModels.Floating;
using GimmeCapture.Models;
using GimmeCapture.Services.Core;
using System;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.Media.Imaging;
using System.ComponentModel;

namespace GimmeCapture.Views.Floating;

/// <summary>
/// Base class for Floating Windows (Image and Video) containing shared logic for:
/// - Resizing (Edges/Corners)
/// - Moving (Drag)
/// - Drawing (Annotations)
/// - Text Entry
/// - Selection
/// - Hotkeys
/// </summary>
public abstract class FloatingWindowBase : Window
{
    private FloatingWindowViewModelBase? _boundViewModel;
    private PropertyChangedEventHandler? _boundViewModelPropertyChangedHandler;
    private bool _suppressWindowSizeSync;
    private IDisposable? _toolbarPlacementBoundsSubscription;

    // Resize State
    protected bool _isResizing;
    protected ResizeDirection _resizeDirection;
    protected Point _resizeStartPoint;
    protected PixelPoint _startPosition;
    protected Size _startSize;
    protected double _startContentWidth;
    protected double _startContentHeight;

    // Move State
    protected bool _isMaybeMoving;
    protected Point _mouseDownPoint;
    protected PointerPressedEventArgs? _pendingMoveEvent;

    // Drawing State
    protected Annotation? _currentAnnotation;
    protected bool _isDrawing;
    protected Point _startPoint;
    protected DateTime _lastTextFinishTime = DateTime.MinValue;
    
    // Drag Annotation State
    protected bool _isDraggingAnnotation;
    protected Annotation? _draggingAnnotation;
    protected Point _dragOffset;
    protected AnnotationSnapshot? _annotationEditBefore;
    protected AnnotationHitZone _annotationHitZone;
    protected Point _annotationDragStart;

    // Selection State
    protected bool _isSelecting;
    protected Point _selectionStartPoint;

    protected enum ResizeDirection
    {
        None, TopLeft, TopRight, BottomLeft, BottomRight, Top, Bottom, Left, Right
    }

    public FloatingWindowBase()
    {
        // Shared Event Handlers via AddHandler to handle Tunneling/Bubbling correctly
        AddHandler(PointerPressedEvent, OnPointerPressed, RoutingStrategies.Tunnel);
        // Note: OnPointerMoved is handled via override, so we strictly follow original logic which didn't verify Tunnel for Move.
        
        AddHandler(TappedEvent, OnTapped, RoutingStrategies.Bubble);
        AddHandler(ContextRequestedEvent, OnContextRequested, RoutingStrategies.Tunnel);
        
        KeyDown += OnKeyDown;
        PositionChanged += (_, _) => QueueToolbarEdgePlacement();
        _toolbarPlacementBoundsSubscription = this.GetObservable(BoundsProperty)
            .Subscribe(_ => QueueToolbarEdgePlacement());
    }

    /// <summary>
    /// Abstract method to get the specific content control (Image or Video) 
    /// used for coordinate mapping.
    /// </summary>
    protected abstract Control? GetContentControl();

    /// <summary>
    /// Abstract method to get the snapshot of the current content 
    /// (for Mosaic/Blur effect initialization).
    /// </summary>
    protected abstract Bitmap? GetContentSnapshot();

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_boundViewModel != null && _boundViewModelPropertyChangedHandler != null)
        {
            _boundViewModel.PropertyChanged -= _boundViewModelPropertyChangedHandler;
            _boundViewModel = null;
            _boundViewModelPropertyChangedHandler = null;
        }

        if (DataContext is FloatingWindowViewModelBase vm)
        {
            // Shared VM Setup
            vm.CloseAction = Close;
            vm.FocusWindowAction = () => this.Focus();

            vm.RequestSetWindowRect = (pos, w, h, cw, ch) =>
            {
                Position = pos;
                // We don't set Width/Height directly here usually in ImageWindow, 
                // but we might need to trigger sync.
                SyncWindowSizeToContent(); 
            };
            
            // Re-bind property changed
            _boundViewModelPropertyChangedHandler = (s, ev) =>
            {
                if (ev.PropertyName == nameof(FloatingWindowViewModelBase.ShowToolbar) || 
                    ev.PropertyName == nameof(FloatingWindowViewModelBase.WindowPadding) ||
                    ev.PropertyName == nameof(FloatingWindowViewModelBase.DisplayWidth) ||
                    ev.PropertyName == nameof(FloatingWindowViewModelBase.DisplayHeight))
                {
                    if (_suppressWindowSizeSync || _isResizing)
                    {
                        return;
                    }

                    SyncWindowSizeToContent();
                }

                if (ev.PropertyName is nameof(FloatingWindowViewModelBase.ShowToolbar)
                    or nameof(FloatingWindowViewModelBase.ToolbarHeight)
                    or nameof(FloatingWindowViewModelBase.WindowPadding))
                {
                    QueueToolbarEdgePlacement();
                }
            };
            vm.PropertyChanged += _boundViewModelPropertyChangedHandler;
            _boundViewModel = vm;
            
            // Force initial sync
            SyncWindowSizeToContent();
            QueueToolbarEdgePlacement();
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        if (_boundViewModel != null && _boundViewModelPropertyChangedHandler != null)
        {
            _boundViewModel.PropertyChanged -= _boundViewModelPropertyChangedHandler;
            _boundViewModel = null;
            _boundViewModelPropertyChangedHandler = null;
        }

        _toolbarPlacementBoundsSubscription?.Dispose();
        _toolbarPlacementBoundsSubscription = null;

        base.OnClosed(e);
    }

    protected virtual void SyncWindowSizeToContent()
    {
         if (DataContext is FloatingWindowViewModelBase vm) 
        {
             SizeToContent = SizeToContent.Manual;

             var padding = vm.WindowPadding;
             double border = 0; // If any extra border logic needed
             double contentW = vm.DisplayWidth + padding.Left + padding.Right + border;
             double contentH = vm.DisplayHeight + padding.Top + padding.Bottom + border;
             
             // Dynamic MinWidth to protect toolbar
             MinWidth = vm.ShowToolbar ? (480 + padding.Left + padding.Right) : 50;
             MinHeight = vm.ShowToolbar ? (150 + padding.Top + padding.Bottom) : 50;

             Width = System.Math.Max(contentW, MinWidth);
             Height = System.Math.Max(contentH, MinHeight);
             
             InvalidateMeasure();
             InvalidateArrange();
             QueueToolbarEdgePlacement();
        }
    }

    private void QueueToolbarEdgePlacement()
    {
        Dispatcher.UIThread.Post(UpdateToolbarEdgePlacement, DispatcherPriority.Loaded);
    }

    private void UpdateToolbarEdgePlacement()
    {
        if (DataContext is not FloatingWindowViewModelBase vm || !vm.ShowToolbar)
        {
            return;
        }

        var screen = Screens.ScreenFromWindow(this);
        if (screen == null || Bounds.Height <= 0)
        {
            return;
        }

        const double defaultBottomMargin = 10;
        var scaling = screen.Scaling > 0 ? screen.Scaling : RenderScaling;
        var windowBottomPhysical = Position.Y + Bounds.Height * scaling;
        var overlapPhysical = Math.Max(0, windowBottomPhysical - screen.WorkingArea.Bottom);
        var overlapLogical = overlapPhysical / Math.Max(0.1, scaling);
        var toolbarHeight = vm.ToolbarHeight > 0 ? vm.ToolbarHeight : 32;
        var maximumBottomMargin = Math.Max(
            defaultBottomMargin,
            Bounds.Height - toolbarHeight - defaultBottomMargin);
        var targetBottomMargin = Math.Clamp(
            defaultBottomMargin + overlapLogical,
            defaultBottomMargin,
            maximumBottomMargin);

        vm.IsToolbarFlipped = overlapLogical > 0.5;
        if (Math.Abs(vm.ToolbarMargin.Bottom - targetBottomMargin) > 0.5)
        {
            vm.ToolbarMargin = new Thickness(0, 0, 0, targetBottomMargin);
        }

        // Top contextual sub-toolbar: mirror the bottom logic against the screen's TOP edge. It normally
        // rests snug above the content border (SubToolbarRestingTop); when the pin sits above the work-area
        // top, push the floating row DOWN by the overlap so it stays fully on-screen instead of being clipped.
        if (vm.IsSubToolbarVisible)
        {
            const double minTopMargin = 8;
            const double subToolbarHeight = 40;
            var topOverlapPhysical = Math.Max(0, screen.WorkingArea.Y - Position.Y);
            var topOverlapLogical = topOverlapPhysical / Math.Max(0.1, scaling);
            var restingTop = Math.Max(minTopMargin, vm.SubToolbarRestingTop);
            var maximumTopMargin = Math.Max(restingTop, Bounds.Height - subToolbarHeight - minTopMargin);
            var targetTopMargin = Math.Clamp(restingTop + topOverlapLogical, restingTop, maximumTopMargin);
            if (Math.Abs(vm.SubToolbarMargin.Top - targetTopMargin) > 0.5)
            {
                vm.SubToolbarMargin = new Thickness(0, targetTopMargin, 0, 0);
            }
        }
    }

    protected virtual void OnTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is FloatingWindowViewModelBase vm)
        {
            var visualSource = e.Source as Avalonia.Visual;
            while (visualSource != null)
            {
                if (visualSource is Button || visualSource is ToggleButton || visualSource is ContextMenu)
                    return;
                visualSource = visualSource.GetVisualParent();
            }

            if (vm.ShowToolbar) return; 
            
            // Logic to prevent showing toolbar if we just finished an interaction
            if (!_isResizing && !_isDrawing && !_isMaybeMoving && !_isSelecting &&
                !vm.IsAnyToolActive)
            {
                vm.ShowToolbar = true;
            }
        }
    }

    protected virtual void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is not FloatingWindowViewModelBase vm) return;
        var source = e.Source as Control;
        var pCurrentPoint = e.GetCurrentPoint(this);
        var pointerPos = pCurrentPoint.Position;
        var pProperties = pCurrentPoint.Properties;

        // 1. Resize handles check
        if (pProperties.IsLeftButtonPressed && 
            !vm.IsAnyToolActive && 
            source != null && source.Classes.Contains("Handle"))
        {
            _isResizing = true;
            _resizeDirection = GetDirectionFromName(source.Name);
            try
            {
                SizeToContent = SizeToContent.Manual;
                _resizeStartPoint = this.PointToScreen(pointerPos).ToPoint(1.0);
                _startPosition = Position;
                _startSize = Bounds.Size;
                _startContentWidth = vm.DisplayWidth;
                _startContentHeight = vm.DisplayHeight;
                e.Pointer.Capture(this);
                e.Handled = true;
            }
            catch (Exception) { _isResizing = false; }
            return;
        }

        // 2. Interactive elements (Buttons, Sliders etc)
        var visualSource = e.Source as Avalonia.Visual;
        var vFallback = visualSource;
        while (vFallback != null)
        {
            if (vFallback is Button || vFallback is ToggleButton || vFallback is ICommandSource || vFallback is ContextMenu || vFallback is TextBox || vFallback is Slider || vFallback is Thumb || vFallback is SelectableTextBlock)
                return;
            // Regions opted out of window-move (e.g. the toolbar / timeline strip): don't drag the pin.
            if (vFallback is Control vc && vc.Classes.Contains("no-window-drag"))
                return;
            vFallback = vFallback.GetVisualParent();
        }

        // 3. Drawing / Text Interaction
        if (pProperties.IsLeftButtonPressed && vm.CurrentAnnotationTool != AnnotationType.None && !vm.IsProcessing)
        {
            var contentControl = GetContentControl();
            if (contentControl == null) return;

            var pointerPosOnContent = e.GetPosition(contentControl);
            var contentBounds = new Rect(0, 0, contentControl.Bounds.Width, contentControl.Bounds.Height);
            var interactionBounds = vm.SelectedAnnotation != null
                ? contentBounds.Inflate(AnnotationInteractionService.HandleRadius + 2)
                : contentBounds;

            // Restrict drawing interaction to the content area
            if (!interactionBounds.Contains(pointerPosOnContent))
            {
                return;
            }

            if ((DateTime.Now - _lastTextFinishTime).TotalMilliseconds < 300) return;

            if (vm.IsEnteringText)
            {
                var src = e.Source as Control;
                if (src != null && (src.Name == "TextInputOverlay" || src.FindAncestorOfType<TextBox>() != null)) return;
                
                // Clicking outside text box confirms text
                vm.ConfirmTextEntryCommand.Execute(System.Reactive.Unit.Default).Subscribe();
                e.Handled = true;
                return;
            }

            var annotationHit = AnnotationInteractionService.HitTest(
                vm.Annotations,
                vm.SelectedAnnotation,
                pointerPosOnContent);
            if (annotationHit.IsHit && annotationHit.Annotation != null)
            {
                _isDraggingAnnotation = true;
                _draggingAnnotation = annotationHit.Annotation;
                _annotationHitZone = annotationHit.Zone;
                _annotationDragStart = pointerPosOnContent;
                _annotationEditBefore = vm.BeginAnnotationEdit(annotationHit.Annotation);
                if (annotationHit.Annotation.Type == AnnotationType.Text)
                {
                    _dragOffset = new Point(
                        pointerPosOnContent.X - annotationHit.Annotation.StartPoint.X,
                        pointerPosOnContent.Y - annotationHit.Annotation.StartPoint.Y);
                }
                e.Pointer.Capture(this);
                e.Handled = true;
                return;
            }

            if (!contentBounds.Contains(pointerPosOnContent))
            {
                return;
            }

            vm.ClearAnnotationSelection();

            if (vm.CurrentAnnotationTool == AnnotationType.Text)
            {
                // Check if clicking existing text to edit/drag
                for (int i = vm.Annotations.Count - 1; i >= 0; i--)
                {
                    var ann = vm.Annotations[i];
                    if (ann.Type == AnnotationType.Text)
                    {
                        double estimatedWidth = ann.Text.Length * ann.FontSize * 0.6;
                        double estimatedHeight = ann.FontSize * 1.5;
                        var rect = new Rect(ann.StartPoint.X, ann.StartPoint.Y, estimatedWidth, estimatedHeight);
                        if (rect.Contains(pointerPosOnContent))
                        {
                            if (e.ClickCount == 2)
                            {
                                // Edit Mode
                                vm.RemoveAnnotation(ann);
                                vm.IsEnteringText = true;
                                vm.TextInputPosition = ann.StartPoint;
                                vm.PendingText = ann.Text;
                                vm.CurrentFontSize = ann.FontSize;
                                vm.IsBold = ann.IsBold;
                                vm.IsItalic = ann.IsItalic;
                                vm.SelectedColor = ann.Color;

                                var textBox = this.FindControl<TextBox>("TextInputOverlay");
                                Dispatcher.UIThread.Post(() => textBox?.Focus());
                                e.Handled = true;
                                return;
                            }
                            else
                            {
                                // Drag Mode
                                _isDraggingAnnotation = true;
                                _draggingAnnotation = ann;
                                _dragOffset = new Point(pointerPosOnContent.X - ann.StartPoint.X, pointerPosOnContent.Y - ann.StartPoint.Y);
                                _annotationHitZone = AnnotationHitZone.Body;
                                _annotationDragStart = pointerPosOnContent;
                                _annotationEditBefore = vm.BeginAnnotationEdit(ann);
                                e.Pointer.Capture(this);
                                e.Handled = true;
                                return;
                            }
                        }
                    }
                }
                
                // Start NEW Text Entry
                vm.IsEnteringText = true;
                vm.TextInputPosition = pointerPosOnContent;
                vm.PendingText = string.Empty;
                var textBoxNew = this.FindControl<TextBox>("TextInputOverlay");
                textBoxNew?.Focus();
                e.Handled = true;
                return;
            }

            // Start Drawing Shape/Pen
            _isDrawing = true;
            _startPoint = pointerPosOnContent;
            
            // Snapshot is only needed for effects that sample underlying pixels.
            Bitmap? frameSnapshot = (vm.CurrentAnnotationTool == AnnotationType.Mosaic || vm.CurrentAnnotationTool == AnnotationType.Blur)
                ? GetContentSnapshot()
                : null;

            _currentAnnotation = vm.CreateAnnotationForCurrentTool(
                pointerPosOnContent,
                frameSnapshot,
                contentControl.Bounds.Size);

            vm.BeginPendingAnnotation(_currentAnnotation);
            e.Pointer.Capture(this);
            e.Handled = true;
            return;
        }

        // 4. Selection Tool 
        if (pProperties.IsLeftButtonPressed && vm.CurrentTool == FloatingTool.Selection && !vm.IsProcessing)
        {
            var contentControl = GetContentControl();
            if (contentControl != null)
            {
                var pos = e.GetPosition(contentControl);
                if (new Rect(0, 0, contentControl.Bounds.Width, contentControl.Bounds.Height).Contains(pos))
                {
                    _isSelecting = true;
                    _selectionStartPoint = pos;
                    e.Pointer.Capture(this);
                    e.Handled = true;
                    return;
                }
            }
        }

        // 5. Default: Window Move preparation
        if (pProperties.IsLeftButtonPressed && !vm.IsAnyToolActive)
        {
            _isMaybeMoving = true;
            _startPosition = Position;
            _mouseDownPoint = e.GetPosition(this); // Using window coordinates
            _pendingMoveEvent = e; 
        }
        else if (pProperties.IsRightButtonPressed)
        {
             // Right click cancel logic
             if (vm.IsSelectionMode)
             {
                 vm.SelectionRect = new Rect();
                 e.Handled = true;
             }
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (DataContext is not FloatingWindowViewModelBase vm) return;

        var currentPoint = e.GetCurrentPoint(this);
        var pointerPos = currentPoint.Position;

        UpdateAnnotationCursor(e, vm);

        if (_isResizing)
        {
            PerformResizing(e, vm);
        }
        else if (_isSelecting)
        {
            var contentControl = GetContentControl();
            if (contentControl != null)
            {
                var pos = e.GetPosition(contentControl);
                // Clamp to image bounds
                double x = Math.Max(0, Math.Min(pos.X, contentControl.Bounds.Width));
                double y = Math.Max(0, Math.Min(pos.Y, contentControl.Bounds.Height));
                var currentPos = new Point(x, y);

                var rect = new Rect(
                    Math.Min(_selectionStartPoint.X, currentPos.X),
                    Math.Min(_selectionStartPoint.Y, currentPos.Y),
                    Math.Abs(currentPos.X - _selectionStartPoint.X),
                    Math.Abs(currentPos.Y - _selectionStartPoint.Y));
                
                vm.SelectionRect = rect;
            }
        }
        else if (_isDrawing && _currentAnnotation != null)
        {
            var contentControl = GetContentControl();
            if (contentControl != null)
            {
                var pointerPosOnContent = e.GetPosition(contentControl);
                pointerPosOnContent = new Point(
                    Math.Clamp(pointerPosOnContent.X, 0, contentControl.Bounds.Width),
                    Math.Clamp(pointerPosOnContent.Y, 0, contentControl.Bounds.Height));
                if (_currentAnnotation.Type == AnnotationType.Pen)
                {
                    _currentAnnotation.AddPoint(pointerPosOnContent);
                }
                else
                {
                    _currentAnnotation.EndPoint = pointerPosOnContent;
                }
                e.Handled = true;
            }
        }
        else if (_isDraggingAnnotation && _draggingAnnotation != null)
        {
            var contentControl = GetContentControl();
            if (contentControl != null)
            {
                var pointerPosOnContent = e.GetPosition(contentControl);
                if (_draggingAnnotation.Type == AnnotationType.Text)
                {
                    var estimatedWidth = _draggingAnnotation.Text.Length * _draggingAnnotation.FontSize * 0.6;
                    var estimatedHeight = _draggingAnnotation.FontSize * 1.5;
                    var newStart = new Point(
                        Math.Clamp(pointerPosOnContent.X - _dragOffset.X, 0, Math.Max(0, contentControl.Bounds.Width - estimatedWidth)),
                        Math.Clamp(pointerPosOnContent.Y - _dragOffset.Y, 0, Math.Max(0, contentControl.Bounds.Height - estimatedHeight)));
                    var delta = newStart - _draggingAnnotation.StartPoint;
                    _draggingAnnotation.StartPoint = newStart;
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
                        pointerPosOnContent,
                        contentControl.Bounds.Size);
                }
            }
        }
        
        if (_isMaybeMoving)
        {
             var delta = pointerPos - _mouseDownPoint;
             if (Math.Abs(delta.X) > 5 || Math.Abs(delta.Y) > 5)
             {
                 _isMaybeMoving = false;
                 BeginMoveDrag(e);
             }
        }
    }

    // After the Callout leader is dragged, open the label text-entry overlay at the leader's end
    // (label anchor). A click with no real drag gets a default offset so the leader stays visible
    // and valid. The pending leader is finalized by the VM's ConfirmTextEntryCommand.
    private void BeginCalloutLabelEntry(FloatingWindowViewModelBase vm, Annotation leader)
    {
        var dx = leader.EndPoint.X - leader.StartPoint.X;
        var dy = leader.EndPoint.Y - leader.StartPoint.Y;
        if (Math.Sqrt((dx * dx) + (dy * dy)) < AnnotationInteractionService.MinimumLineLength)
        {
            leader.EndPoint = new Point(leader.StartPoint.X + 48, leader.StartPoint.Y + 48);
        }

        vm.BeginCalloutTextEntry(leader);
        vm.IsEnteringText = true;
        vm.TextInputPosition = leader.EndPoint;
        vm.PendingText = leader.Text ?? string.Empty;

        var textBox = this.FindControl<TextBox>("TextInputOverlay");
        Dispatcher.UIThread.Post(() => textBox?.Focus());
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        
        if (_isResizing)
        {
            e.Pointer.Capture(null); 
            _isResizing = false;

            if (DataContext is FloatingWindowViewModelBase vm)
            {
                vm.PushResizeAction(_startPosition, _startSize.Width, _startSize.Height, _startContentWidth, _startContentHeight,
                                       Position, Width, Height, vm.DisplayWidth, vm.DisplayHeight);
            }
        }
        else if (_isSelecting)
        {
            e.Pointer.Capture(null);
            _isSelecting = false;
        }
        else if (_isDrawing)
        {
            e.Pointer.Capture(null);
            _isDrawing = false;
            if (DataContext is FloatingWindowViewModelBase vm && _currentAnnotation != null)
            {
                if (_currentAnnotation.Type == AnnotationType.Callout)
                {
                    BeginCalloutLabelEntry(vm, _currentAnnotation);
                }
                else
                {
                    vm.CommitPendingAnnotation(_currentAnnotation);
                }
            }
            _currentAnnotation = null;
        }
        else if (_isDraggingAnnotation)
        {
            e.Pointer.Capture(null);
            if (DataContext is FloatingWindowViewModelBase vm
                && _draggingAnnotation != null
                && _annotationEditBefore != null)
            {
                vm.CommitAnnotationEdit(_draggingAnnotation, _annotationEditBefore);
            }
            _isDraggingAnnotation = false;
            _draggingAnnotation = null;
            _annotationEditBefore = null;
            _annotationHitZone = AnnotationHitZone.None;
        }
        else if (_isMaybeMoving)
        {
            e.Pointer.Capture(null);
            _isMaybeMoving = false;
            _pendingMoveEvent = null;
        }
    }

    private void UpdateAnnotationCursor(PointerEventArgs e, FloatingWindowViewModelBase vm)
    {
        if (vm.CurrentAnnotationTool == AnnotationType.None || vm.IsProcessing)
        {
            return;
        }

        if (_isDraggingAnnotation)
        {
            Cursor = CreateAnnotationCursor(_annotationHitZone);
            return;
        }

        if (_isDrawing || _isSelecting || _isResizing)
        {
            return;
        }

        var contentControl = GetContentControl();
        if (contentControl == null)
        {
            return;
        }

        var point = e.GetPosition(contentControl);
        var contentBounds = new Rect(0, 0, contentControl.Bounds.Width, contentControl.Bounds.Height);
        var interactionBounds = vm.SelectedAnnotation != null
            ? contentBounds.Inflate(AnnotationInteractionService.HandleRadius + 2)
            : contentBounds;
        if (!interactionBounds.Contains(point))
        {
            Cursor = new Cursor(StandardCursorType.Arrow);
            return;
        }

        var hit = AnnotationInteractionService.HitTest(vm.Annotations, vm.SelectedAnnotation, point);
        Cursor = hit.IsHit
            ? CreateAnnotationCursor(hit.Zone)
            : new Cursor(contentBounds.Contains(point)
                ? (vm.CurrentAnnotationTool == AnnotationType.Text
                    ? StandardCursorType.Ibeam
                    : StandardCursorType.Cross)
                : StandardCursorType.Arrow);
    }

    private static Cursor CreateAnnotationCursor(AnnotationHitZone zone)
    {
        var cursorType = zone switch
        {
            AnnotationHitZone.TopLeft or AnnotationHitZone.BottomRight => StandardCursorType.TopLeftCorner,
            AnnotationHitZone.TopRight or AnnotationHitZone.BottomLeft => StandardCursorType.TopRightCorner,
            AnnotationHitZone.Top or AnnotationHitZone.Bottom => StandardCursorType.SizeNorthSouth,
            AnnotationHitZone.Left or AnnotationHitZone.Right => StandardCursorType.SizeWestEast,
            AnnotationHitZone.StartPoint or AnnotationHitZone.EndPoint => StandardCursorType.Hand,
            _ => StandardCursorType.SizeAll
        };
        return new Cursor(cursorType);
    }

    private void BeginMoveDrag(PointerEventArgs e)
    {
        if (_pendingMoveEvent != null)
        {
             BeginMoveDrag(_pendingMoveEvent);
             _pendingMoveEvent = null;
        }
    }

    private new void BeginMoveDrag(PointerPressedEventArgs e)
    {
        e.Pointer.Capture(null);
        base.BeginMoveDrag(e);
    }
    
    private void PerformResizing(PointerEventArgs e, FloatingWindowViewModelBase vm)
    {
        try
        {
            var padding = vm.WindowPadding;
            var p = e.GetCurrentPoint(this);
            var currentScreenPoint = this.PointToScreen(p.Position).ToPoint(1.0);
            
            var deltaX = currentScreenPoint.X - _resizeStartPoint.X;
            var deltaY = currentScreenPoint.Y - _resizeStartPoint.Y;

            var scaling = RenderScaling;
            var deltaWidth = deltaX / scaling;
            var deltaHeight = deltaY / scaling;

            double contentW = _startContentWidth;
            double contentH = _startContentHeight;

            if (e.KeyModifiers.HasFlag(KeyModifiers.Shift))
            {
                // Uniform Aspect Ratio Logic
                double aspectRatio = vm.OriginalWidth / vm.OriginalHeight;
                if (double.IsNaN(aspectRatio) || double.IsInfinity(aspectRatio)) aspectRatio = 1;
                
                bool useWidthAsBasis;
                if (_resizeDirection == ResizeDirection.Top || _resizeDirection == ResizeDirection.Bottom)
                    useWidthAsBasis = false;
                else if (_resizeDirection == ResizeDirection.Left || _resizeDirection == ResizeDirection.Right)
                    useWidthAsBasis = true;
                else 
                {
                    double dW = Math.Abs(deltaWidth);
                    double dH = Math.Abs(deltaHeight);
                    useWidthAsBasis = dW >= dH;
                }

                if (useWidthAsBasis)
                {
                    double dragDir = (_resizeDirection == ResizeDirection.Left || _resizeDirection == ResizeDirection.TopLeft || _resizeDirection == ResizeDirection.BottomLeft) ? -1 : 1;
                    contentW = Math.Max(1, _startContentWidth + (deltaWidth * dragDir));
                    contentH = contentW / aspectRatio;
                }
                else
                {
                    double dragDir = (_resizeDirection == ResizeDirection.Top || _resizeDirection == ResizeDirection.TopLeft || _resizeDirection == ResizeDirection.TopRight) ? -1 : 1;
                    contentH = Math.Max(1, _startContentHeight + (deltaHeight * dragDir));
                    contentW = contentH * aspectRatio;
                }
            }
            else
            {
                // Free Resize
                if (_resizeDirection == ResizeDirection.Right || _resizeDirection == ResizeDirection.BottomRight || _resizeDirection == ResizeDirection.TopRight)
                    contentW += deltaWidth;
                else if (_resizeDirection == ResizeDirection.Left || _resizeDirection == ResizeDirection.BottomLeft || _resizeDirection == ResizeDirection.TopLeft)
                    contentW -= deltaWidth;

                if (_resizeDirection == ResizeDirection.Bottom || _resizeDirection == ResizeDirection.BottomLeft || _resizeDirection == ResizeDirection.BottomRight)
                    contentH += deltaHeight;
                else if (_resizeDirection == ResizeDirection.Top || _resizeDirection == ResizeDirection.TopLeft || _resizeDirection == ResizeDirection.TopRight)
                    contentH -= deltaHeight;
            }

            // Update ViewModel without re-entering SyncWindowSizeToContent on every pointer move.
            _suppressWindowSizeSync = true;
            vm.DisplayWidth = Math.Max(1, contentW);
            vm.DisplayHeight = Math.Max(1, contentH);

            // Update Window Size
            double hPad = padding.Left + padding.Right;
            double vPad = padding.Top + padding.Bottom;
            
            double targetWindowW = vm.DisplayWidth + hPad;
            double targetWindowH = vm.DisplayHeight + vPad;

            MinWidth = vm.ShowToolbar ? (480 + hPad) : 50;
            MinHeight = vm.ShowToolbar ? (150 + vPad) : 50;

            Width = Math.Max(targetWindowW, MinWidth);
            Height = Math.Max(targetWindowH, MinHeight);

            // Re-calculate X/Y to keep origin pinned
            double deltaWinW = Width - _startSize.Width;
            double deltaWinH = Height - _startSize.Height;

            double newX = _startPosition.X;
            double newY = _startPosition.Y;

            if (_resizeDirection == ResizeDirection.TopLeft || _resizeDirection == ResizeDirection.Top || _resizeDirection == ResizeDirection.TopRight)
                newY = _startPosition.Y - deltaWinH * scaling;
            
            if (_resizeDirection == ResizeDirection.TopLeft || _resizeDirection == ResizeDirection.Left || _resizeDirection == ResizeDirection.BottomLeft)
                newX = _startPosition.X - deltaWinW * scaling;

            Position = new PixelPoint((int)newX, (int)newY);
            
            InvalidateMeasure();
            InvalidateArrange();
        }
        catch (Exception)
        {
            // Suppress invalid resize calcs
        }
        finally
        {
            _suppressWindowSizeSync = false;
        }
    }

    protected void OnContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (DataContext is FloatingWindowViewModelBase vm)
        {
             // Disable Context Menu in certain modes
            if ((vm is FloatingImageViewModel imgVm && imgVm.IsPointRemovalMode) || vm.IsSelectionMode)
            {
                e.Handled = true;
            }
        }
    }

    protected virtual void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not FloatingWindowViewModelBase vm) return;

        if (e.Key == Key.Escape)
        {
            // Esc cancels the in-progress action (text entry / selection / tool / trim / point-removal);
            // it no longer closes the window. Closing is Ctrl+W, the close button, or the context menu.
            vm.TryCancelCurrentAction();
            e.Handled = true;
        }
        else if (e.Key == Key.C && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            // vm.CopyCommand ??
            if (vm is FloatingImageViewModel imgVm) imgVm.CopyCommand.Execute().Subscribe();
             else if (vm is FloatingVideoViewModel vidVm) vidVm.CopyCommand.Execute().Subscribe();
            e.Handled = true;
        }
        else if (e.Key == Key.Z && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            vm.UndoCommand.Execute().Subscribe();
            e.Handled = true;
        }
        else if (e.Key == Key.Y && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            vm.RedoCommand.Execute().Subscribe();
            e.Handled = true;
        }
    }
    
    protected ResizeDirection GetDirectionFromName(string? name)
    {
        return name switch
        {
            "HandleTopLeft" => ResizeDirection.TopLeft,
            "HandleTopRight" => ResizeDirection.TopRight,
            "HandleBottomLeft" => ResizeDirection.BottomLeft,
            "HandleBottomRight" => ResizeDirection.BottomRight,
            "HandleTop" => ResizeDirection.Top,
            "HandleBottom" => ResizeDirection.Bottom,
            "HandleLeft" => ResizeDirection.Left,
            "HandleRight" => ResizeDirection.Right,
            _ => ResizeDirection.None
        };
    }
}
