using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using Avalonia.Controls.Primitives;
using GimmeCapture.ViewModels.Floating;
using System;
using GimmeCapture.Services.Abstractions;
using GimmeCapture.Services.Core;
using Avalonia.Media.Imaging;
using Avalonia.Media;
using System.Reactive.Linq;
using Avalonia.Interactivity;

namespace GimmeCapture.Views.Floating;

public partial class FloatingImageWindow : Window
{
    public FloatingImageWindow()
    {
        InitializeComponent();
        
        // Use Tunneling for PointerPressed to catch events before children can swallow them,
        // although here we mostly want to catch them BEFORE they bubble up to Tapped.
        AddHandler(PointerPressedEvent, OnPointerPressed, RoutingStrategies.Tunnel);
        AddHandler(TappedEvent, OnTapped, RoutingStrategies.Bubble);
        
        // Use Tunneling for ContextRequested to catch it before the RootGrid opens the menu
        AddHandler(ContextRequestedEvent, OnContextRequested, RoutingStrategies.Tunnel);
        KeyDown += OnKeyDown;
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is FloatingImageViewModel vm)
        {
            vm.CloseAction = Close;
            
            // CopyAction handled by IClipboardService in ViewModel

            
            vm.SaveAction = async () =>
            {
                 var topLevel = TopLevel.GetTopLevel(this);
                 if (topLevel == null) return;
                 
                 var file = await topLevel.StorageProvider.SaveFilePickerAsync(new Avalonia.Platform.Storage.FilePickerSaveOptions
                 {
                     Title = "Save Floating Image",
                     DefaultExtension = "png",
                     ShowOverwritePrompt = true,
                     SuggestedFileName = $"Pinned_{DateTime.Now:yyyyMMdd_HHmmss}",
                     FileTypeChoices = new[]
                     {
                         new Avalonia.Platform.Storage.FilePickerFileType("PNG Image") { Patterns = new[] { "*.png" } }
                     }
                 });
                 
                 if (file != null)
                 {
                     using var stream = await file.OpenWriteAsync();
                     vm.Image?.Save(stream);
                 }
            };

            // Force initial sync
            SyncWindowSizeToImage();
            
            vm.PropertyChanged += (s, ev) =>
            {
                if (ev.PropertyName == nameof(FloatingImageViewModel.Image) || 
                    ev.PropertyName == nameof(FloatingImageViewModel.WindowPadding) ||
                    ev.PropertyName == nameof(FloatingImageViewModel.ShowToolbar))
                {
                    // If toolbar visibility changed, we MUST restore SizeToContent 
                    // to ensure the window grows to show it correctly.
                    if (ev.PropertyName == nameof(FloatingImageViewModel.ShowToolbar))
                    {
                        SizeToContent = SizeToContent.WidthAndHeight;
                    }
                    
                    SyncWindowSizeToImage();
                }
            };

            // Implementation for spawning a NEW pinned window from selection
            vm.OpenPinWindowAction = (bitmap, rect, color, thickness, runAI) =>
            {
                // Reuse the same logic as SnipWindow to spawn new windows
                var newVm = new FloatingImageViewModel(bitmap, rect.Width, rect.Height, color, thickness, vm.HidePinDecoration, vm.HidePinBorder, 
                    vm.ClipboardService, vm.AIResourceService);
                
                newVm.WingScale = vm.WingScale;
                newVm.CornerIconScale = vm.CornerIconScale;
                
                var padding = newVm.WindowPadding;
                
                // Position the new window near the current one for feedback, 
                // but offset it so it's clearly a new window.
                var newWin = new FloatingImageWindow
                {
                    DataContext = newVm,
                    Position = new PixelPoint(Position.X + 40, Position.Y + 40)
                };
                
                newWin.Show();
                
                if (runAI)
                {
                    Avalonia.Threading.Dispatcher.UIThread.Post(() => {
                        newVm.RemoveBackgroundCommand.Execute().Subscribe();
                    });
                }
            };
        }
    }

    private void OnTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is FloatingImageViewModel vm)
        {
            // 1. Filter out interactive elements (Buttons, ToggleButtons, etc.)
            var visualSource = e.Source as Avalonia.Visual;
            while (visualSource != null)
            {
                if (visualSource is Button || visualSource is ToggleButton || visualSource is ContextMenu)
                    return;
                visualSource = visualSource.GetVisualParent();
            }

            // Prevent diagnostic overwrite if AI just triggered or is processing
            if (vm.IsProcessing || vm.DiagnosticText.Contains("AI Trigger"))
            {
                System.Diagnostics.Debug.WriteLine("FloatingWindow: Skipping Tap Diagnostic (AI Active)");
            }
            else
            {
                vm.DiagnosticText = $"Tap: Tool={vm.CurrentTool}, AI={vm.IsInteractiveSelectionMode}";
            }
            System.Diagnostics.Debug.WriteLine($"FloatingWindow: OnTapped. Source: {e.Source}. Tool: {vm.CurrentTool}. AI: {vm.IsInteractiveSelectionMode}");
            
            // 2. Toggle toolbar on single click ONLY if no tool is active
            if (!_isResizing && !_isSelecting && !_isMaybeMoving && !_isAIPointing &&
                vm.CurrentTool == FloatingTool.None && !vm.IsInteractiveSelectionMode)
            {
                System.Diagnostics.Debug.WriteLine("FloatingWindow: Toggling toolbar");
                vm.ToggleToolbarCommand.Execute().Subscribe();
            }
            else
            {
                System.Diagnostics.Debug.WriteLine("FloatingWindow: Blocking toolbar toggle (Tool Active)");
                e.Handled = true;
            }
        }
    }

    private void SyncWindowSizeToImage()
    {
        // Now using SizeToContent="WidthAndHeight", so we just need to ensure layout is refreshed
        InvalidateMeasure();
        InvalidateArrange();
    }

    // Resize Fields
    private bool _isResizing;
    private ResizeDirection _resizeDirection;
    private Point _resizeStartPoint; // Screen Coordinates
    
    // Start State
    private PixelPoint _startPosition;
    private Size _startSize; // Logical
    private Point _pointerPressedPoint; // Screen Coordinates for drag threshold
    private bool _isMaybeMoving;
    private PointerPressedEventArgs? _pendingMoveEvent;

    // Selection State
    private bool _isSelecting;
    private Point _selectionStartPoint;
    private bool _isAIPointing;

    private enum ResizeDirection
    {
        None, TopLeft, TopRight, BottomLeft, BottomRight, Top, Bottom, Left, Right
    }

    private async void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is not FloatingImageViewModel vm) return;
        var source = e.Source as Control;
        var pointerPos = e.GetCurrentPoint(this).Position;

        // 1. Resize handles priority
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed && 
            vm.CurrentTool == FloatingTool.None && 
            source != null && source.Classes.Contains("Handle"))
        {
            _isResizing = true;
            _resizeDirection = GetDirectionFromName(source.Name);
            try
            {
                // 關閉 SizeToContent 以允許手動 resize
                SizeToContent = SizeToContent.Manual;
                
                var p = e.GetCurrentPoint(this);
                _resizeStartPoint = this.PointToScreen(p.Position).ToPoint(1.0);
                _startPosition = Position;
                _startSize = Bounds.Size;
                e.Pointer.Capture(this);
                e.Handled = true;
            }
            catch (Exception) { _isResizing = false; }
            return;
        }

        // 2. Interactive elements (Buttons etc)
        var visualSource = e.Source as Avalonia.Visual;
        var vFallback = visualSource;
        while (vFallback != null)
        {
            if (vFallback is Button || vFallback is ICommandSource || vFallback is ContextMenu)
                return;
            vFallback = vFallback.GetVisualParent();
        }

        // 3. Selection Tool vs Movement Preparation
        var pProperties = e.GetCurrentPoint(this).Properties;
        if (pProperties.IsLeftButtonPressed)
        {
            var imageControl = this.FindControl<Image>("PinnedImage");
            
            if (vm.CurrentTool == FloatingTool.Selection && imageControl != null)
            {
                var pos = e.GetPosition(imageControl);
                // Only start selecting if click is within image bounds
                if (new Rect(0, 0, imageControl.Bounds.Width, imageControl.Bounds.Height).Contains(pos))
                {
                    _isSelecting = true;
                    _selectionStartPoint = new Point(
                        Math.Max(0, Math.Min(imageControl.Bounds.Width, pos.X)),
                        Math.Max(0, Math.Min(imageControl.Bounds.Height, pos.Y))
                    );
                    e.Pointer.Capture(this);
                    e.Handled = true;
                    return;
                }
            }
            
            if (vm.IsPointRemovalMode)
            {
                vm.DiagnosticText = "AI Pressed: Capturing...";
                System.Diagnostics.Debug.WriteLine("FloatingWindow: AI Mode Pressed - Capturing Pointer");
                _isAIPointing = true;
                e.Pointer.Capture(this);
                e.Handled = true;
                return;
            }

            // Default: Drag preparation (Only if no AI mode/Selection mode)
            if (vm.CurrentTool == FloatingTool.None)
            {
                _isMaybeMoving = true;
                _pointerPressedPoint = this.PointToScreen(pointerPos).ToPoint(1.0);
                _pendingMoveEvent = e;
                e.Pointer.Capture(this);
            }
        }
        else if (pProperties.IsRightButtonPressed)
        {
            // Block ContextMenu and perform tool-specific cancel if a tool is active
            if (vm.IsPointRemovalMode)
            {
                await vm.UndoLastPointAsync();
                e.Handled = true;
                System.Diagnostics.Debug.WriteLine("FloatingWindow: AI Undo Last Point via Right-Click");
            }
            else if (vm.IsSelectionMode)
            {
                // Reset selection
                vm.SelectionRect = new Rect();
                e.Handled = true;
                System.Diagnostics.Debug.WriteLine("FloatingWindow: Selection Reset via Right-Click");
            }
        }
    }
    
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (DataContext is not FloatingImageViewModel vm) return;
        var pointerPos = e.GetCurrentPoint(this).Position;

        if (_isResizing)
        {
            PerformResizing(e);
        }
        else if (_isSelecting)
        {
            // Update Selection Rect (Coordinates should be relative to PinnedImage or its container)
            var imageControl = this.FindControl<Image>("PinnedImage");
            if (imageControl == null) return;

            var relativePos = e.GetPosition(imageControl);
            var startPos = _selectionStartPoint;
            
            // Clamp current point to image bounds
            double clampedX = Math.Max(0, Math.Min(imageControl.Bounds.Width, relativePos.X));
            double clampedY = Math.Max(0, Math.Min(imageControl.Bounds.Height, relativePos.Y));
            
            double x = Math.Min(startPos.X, clampedX);
            double y = Math.Min(startPos.Y, clampedY);
            double w = Math.Abs(startPos.X - clampedX);
            double h = Math.Abs(startPos.Y - clampedY);
            
            vm.SelectionRect = new Rect(x, y, w, h);
            e.Handled = true;
        }
        else if (_isMaybeMoving)
        {
            var currentScreenPoint = this.PointToScreen(e.GetPosition(this)).ToPoint(1.0);
            var distance = Math.Sqrt(Math.Pow(currentScreenPoint.X - _pointerPressedPoint.X, 2) + 
                                     Math.Pow(currentScreenPoint.Y - _pointerPressedPoint.Y, 2));
            
            // Movement threshold (3 pixels) to distinguish between Click and Drag
            if (distance > 3 && _pendingMoveEvent != null)
            {
                _isMaybeMoving = false;
                var ev = _pendingMoveEvent;
                _pendingMoveEvent = null;
                e.Pointer.Capture(null); // Release capture so BeginMoveDrag works
                BeginMoveDrag(ev);
            }
        }
    }

    private void PerformResizing(PointerEventArgs e)
    {
        try
        {
            var p = e.GetCurrentPoint(this);
            var currentScreenPoint = this.PointToScreen(p.Position).ToPoint(1.0);
            
            var deltaX = currentScreenPoint.X - _resizeStartPoint.X;
            var deltaY = currentScreenPoint.Y - _resizeStartPoint.Y;

            var scaling = RenderScaling;
            var deltaWidth = deltaX / scaling;
            var deltaHeight = deltaY / scaling;

            double x = _startPosition.X;
            double y = _startPosition.Y;
            double w = _startSize.Width;
            double h = _startSize.Height;

            switch (_resizeDirection)
            {
                case ResizeDirection.TopLeft:
                    x = _startPosition.X + (int)deltaX; 
                    y = _startPosition.Y + (int)deltaY; 
                    w = _startSize.Width - deltaWidth; 
                    h = _startSize.Height - deltaHeight; 
                    break;
                case ResizeDirection.TopRight:
                    y = _startPosition.Y + (int)deltaY; 
                    w = _startSize.Width + deltaWidth; 
                    h = _startSize.Height - deltaHeight; 
                    break;
                case ResizeDirection.BottomLeft:
                    x = _startPosition.X + (int)deltaX; 
                    w = _startSize.Width - deltaWidth; 
                    h = _startSize.Height + deltaHeight; 
                    break;
                case ResizeDirection.BottomRight:
                    w = _startSize.Width + deltaWidth; 
                    h = _startSize.Height + deltaHeight; 
                    break;
                case ResizeDirection.Top:
                    y = _startPosition.Y + (int)deltaY; 
                    h = _startSize.Height - deltaHeight; 
                    break;
                case ResizeDirection.Bottom:
                    h = _startSize.Height + deltaHeight; 
                    break;
                case ResizeDirection.Left:
                    x = _startPosition.X + (int)deltaX; 
                    w = _startSize.Width - deltaWidth; 
                    break;
                case ResizeDirection.Right:
                    w = _startSize.Width + deltaWidth; 
                    break;
            }

            w = Math.Max(50, w);
            h = Math.Max(50, h);

            Position = new PixelPoint((int)x, (int)y);
            Width = w;
            Height = h;

            // 更新 ViewModel 中的顯示尺寸
            if (DataContext is FloatingImageViewModel vm)
            {
                var padding = vm.WindowPadding;
                vm.DisplayWidth = Math.Max(1, w - padding.Left - padding.Right);
                
                double toolbarHeight = vm.ShowToolbar ? 42 : 0; // 預估工具列高度
                vm.DisplayHeight = Math.Max(1, h - padding.Top - padding.Bottom - toolbarHeight);
            }
            
            e.Handled = true;
            InvalidateMeasure();
            InvalidateArrange();
        }
        catch (Exception) { }
    }
    
    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_isResizing)
        {
            e.Pointer.Capture(null); 
            _isResizing = false;
        }
        else if (_isSelecting)
        {
            e.Pointer.Capture(null);
            _isSelecting = false;
        }
        else if (_isAIPointing)
        {
            var imageControl = this.FindControl<Image>("PinnedImage");
            if (imageControl != null && DataContext is FloatingImageViewModel vm)
            {
                var pos = e.GetPosition(imageControl);
                var bounds = new Rect(0, 0, imageControl.Bounds.Width, imageControl.Bounds.Height);
                System.Diagnostics.Debug.WriteLine($"FloatingWindow: AI Released. Pos: {pos}, Bounds: {bounds}");
                
                if (bounds.Contains(pos))
                {
                    // CRITICAL: Sync exact UI bounds to ViewModel to eliminate mapping drift
                    vm.DisplayWidth = imageControl.Bounds.Width;
                    vm.DisplayHeight = imageControl.Bounds.Height;
                    
                    vm.DiagnosticText = $"AI Trigger: {pos.X:F0},{pos.Y:F0}";
                    System.Diagnostics.Debug.WriteLine($"FloatingWindow: Triggering AI recognition. UI Bounds Sync: {vm.DisplayWidth}x{vm.DisplayHeight}");
                    _ = vm.HandlePointClickAsync(pos.X, pos.Y);
                }
                else
                {
                    vm.DiagnosticText = "AI Release: Out of Bounds";
                    System.Diagnostics.Debug.WriteLine("FloatingWindow: AI click outside image bounds - ignored");
                }
            }
            e.Pointer.Capture(null);
            _isAIPointing = false;
            e.Handled = true;
        }
        else if (_isMaybeMoving)
        {
            e.Pointer.Capture(null);
            _isMaybeMoving = false;
            _pendingMoveEvent = null;
        }
    }
    
    private void OnContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (DataContext is FloatingImageViewModel vm)
        {
            // Block context menu if any interactive tool is active
            if (vm.IsPointRemovalMode || vm.IsSelectionMode)
            {
                e.Handled = true;
                System.Diagnostics.Debug.WriteLine("FloatingWindow: Blocking ContextMenu because a tool is active.");
            }
        }
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not FloatingImageViewModel vm) return;

        if (e.Key == Key.Escape)
        {
            Close();
        }
        else if (e.Key == Key.C && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            vm.CopyCommand.Execute().Subscribe();
            e.Handled = true;
        }
        else if (e.Key == Key.X && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            vm.CutCommand.Execute().Subscribe();
            e.Handled = true;
        }
    }

    private ResizeDirection GetDirectionFromName(string? name)
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

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        
        if (change.Property == BoundsProperty && DataContext is FloatingImageViewModel vm)
        {
            var imageControl = this.FindControl<Image>("PinnedImage");
            if (imageControl != null)
            {
                vm.DisplayWidth = imageControl.Bounds.Width;
                vm.DisplayHeight = imageControl.Bounds.Height;
            }
        }
    }
}
