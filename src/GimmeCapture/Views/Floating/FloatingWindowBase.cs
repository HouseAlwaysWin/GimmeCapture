using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using Avalonia.Controls.Primitives;
using GimmeCapture.ViewModels.Floating;
using GimmeCapture.Models;
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
                    SyncWindowSizeToContent();
                }
            };
            vm.PropertyChanged += _boundViewModelPropertyChangedHandler;
            _boundViewModel = vm;
            
            // Force initial sync
            SyncWindowSizeToContent();
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
            vFallback = vFallback.GetVisualParent();
        }

        // 3. Drawing / Text Interaction
        if (pProperties.IsLeftButtonPressed && vm.CurrentAnnotationTool != AnnotationType.None && !vm.IsProcessing)
        {
            var contentControl = GetContentControl();
            if (contentControl == null) return;

            var pointerPosOnContent = e.GetPosition(contentControl);

            // Restrict drawing interaction to the content area
            if (pointerPosOnContent.X < 0 || pointerPosOnContent.Y < 0 || 
                pointerPosOnContent.X > contentControl.Bounds.Width || 
                pointerPosOnContent.Y > contentControl.Bounds.Height)
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
                                vm.Annotations.Remove(ann);
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

            _currentAnnotation = new Annotation
            {
                Type = vm.CurrentAnnotationTool,
                StartPoint = pointerPosOnContent,
                EndPoint = pointerPosOnContent,
                Color = vm.SelectedColor,
                Thickness = vm.CurrentThickness,
                FontSize = vm.CurrentFontSize,
                IsBold = vm.IsBold,
                IsItalic = vm.IsItalic,
                DrawingModeSnapshot = frameSnapshot
            };

            if (_currentAnnotation.Type == AnnotationType.Pen)
                _currentAnnotation.AddPoint(pointerPosOnContent);

            vm.AddAnnotation(_currentAnnotation);
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
                var newStart = new Point(pointerPosOnContent.X - _dragOffset.X, pointerPosOnContent.Y - _dragOffset.Y);
                
                var deltaX = newStart.X - _draggingAnnotation.StartPoint.X;
                var deltaY = newStart.Y - _draggingAnnotation.StartPoint.Y;

                _draggingAnnotation.StartPoint = newStart;
                _draggingAnnotation.EndPoint = new Point(_draggingAnnotation.EndPoint.X + deltaX, _draggingAnnotation.EndPoint.Y + deltaY);
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
            _currentAnnotation = null;
        }
        else if (_isDraggingAnnotation)
        {
            e.Pointer.Capture(null);
            _isDraggingAnnotation = false;
            _draggingAnnotation = null;
        }
        else if (_isMaybeMoving)
        {
            e.Pointer.Capture(null);
            _isMaybeMoving = false;
            _pendingMoveEvent = null;
        }
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

            // Update ViewModel
            vm.DisplayWidth = Math.Max(1, contentW);
            vm.DisplayHeight = Math.Max(1, contentH);

            // Update Window Size
            double hPad = padding.Left + padding.Right;
            double vPad = padding.Top + padding.Bottom;
            
            double targetWindowW = vm.DisplayWidth + hPad;
            double targetWindowH = vm.DisplayHeight + vPad;

            MinWidth = vm.ShowToolbar ? (380 + hPad) : 50;
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
            if (vm.IsEnteringText)
            {
                vm.CancelTextEntryCommand.Execute(System.Reactive.Unit.Default).Subscribe();
                e.Handled = true;
            }
            else
            {
                Close();
            }
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
