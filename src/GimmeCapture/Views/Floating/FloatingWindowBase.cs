using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using Avalonia.Controls.Primitives;
using GimmeCapture.ViewModels.Floating;
using GimmeCapture.Models;
using GimmeCapture.Services.Core;
using GimmeCapture.Views.Shared;
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

    // Annotation drawing / drag / text-entry pointer state machine (shared with the compress editor).
    private AnnotationInputController _annotationInput = null!;

    // Selection State
    protected bool _isSelecting;
    protected Point _selectionStartPoint;

    protected enum ResizeDirection
    {
        None, TopLeft, TopRight, BottomLeft, BottomRight, Top, Bottom, Left, Right
    }

    public FloatingWindowBase()
    {
        _annotationInput = new AnnotationInputController(
            getState: () => (DataContext as FloatingWindowViewModelBase)?.EditorState,
            getContentControl: GetContentControl,
            getContentSnapshot: GetContentSnapshot,
            isInteractionBlocked: () => (DataContext as FloatingWindowViewModelBase)?.IsProcessing ?? true,
            confirmTextEntry: () =>
            {
                if (DataContext is FloatingWindowViewModelBase vm)
                {
                    vm.ConfirmTextEntryCommand.Execute(System.Reactive.Unit.Default).Subscribe();
                }
            },
            focusTextInput: () => this.FindControl<TextBox>("TextInputOverlay")?.Focus(),
            captureTarget: this,
            setCursor: c => Cursor = c);

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
            if (!_isResizing && !_annotationInput.IsDrawing && !_isMaybeMoving && !_isSelecting &&
                !vm.IsAnyToolActive)
            {
                vm.ShowToolbar = true;
            }
        }
    }

    protected virtual void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is not FloatingWindowViewModelBase vm) return;

        // Take keyboard focus on any interactive click so the window's KeyBindings (the drawing-tool
        // shortcuts R/E/A/L/P/T/M/B/W) fire without first having to open a focusable toolbar control —
        // the pin is click-through and never self-focuses on open. A focusable child that was clicked
        // (toolbar button / text box) re-takes focus during its own click handling, so this only
        // "sticks" when the press landed on non-focusable content (the image/canvas).
        if (!IsKeyboardFocusWithin)
        {
            Focus();
        }

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

        // 3. Drawing / Text Interaction — the shared annotation pointer state machine (also used by the
        // compress 進階影片編輯 editor). Returns true when the annotation branch applied (handled OR vetoed),
        // matching the original early-return semantics.
        if (_annotationInput.HandlePointerPressed(e))
        {
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

        _annotationInput.UpdateCursor(e, hostBusy: _isSelecting || _isResizing);

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
        else
        {
            // Shared drawing / annotation-drag move branches (no-op when neither is active).
            _annotationInput.HandlePointerMoved(e, suppressCursor: true);
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
        else if (_annotationInput.HandlePointerReleased(e))
        {
            // Shared drawing / annotation-drag release branches (commit pending shape / finalize edit).
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
