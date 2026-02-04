using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.VisualTree;
using GimmeCapture.ViewModels;
using GimmeCapture.Models;
using System;
using System.Linq;
using Avalonia.Platform;
using Avalonia.Input.Raw;
using GimmeCapture.Services;
using GimmeCapture.Services.Interop;
using ReactiveUI;

namespace GimmeCapture.Views;

public partial class SnipWindow : Window
{
    private Point _startPoint;
    private SnipWindowViewModel? _viewModel;
    private Annotation? _currentAnnotation;
    
    // Resize State
    private bool _isResizing;
    private ResizeDirection _resizeDirection;
    private Point _resizeStartPoint;
    
    // Window region for transparent hole (mouse pass-through)
    private IDisposable? _selectionRectSubscription;
    private IDisposable? _drawingModeSubscription;
    private Rect _originalRect;
    
    // Floating draw canvas for see-through drawing
    private FloatingDrawCanvas? _floatingDrawCanvas;

    private enum ResizeDirection
    {
        None, TopLeft, TopRight, BottomLeft, BottomRight, Top, Bottom, Left, Right
    }

    public SnipWindow()
    {
        InitializeComponent();
        
        // Listen to pointer events on the window or canvas
        PointerPressed += OnPointerPressed;
        PointerMoved += OnPointerMoved;
        PointerReleased += OnPointerReleased;
        
        // Text Input Events
        var textBox = this.FindControl<TextBox>("TextInputOverlay");
        if (textBox != null)
        {
            textBox.KeyDown += (s, e) =>
            {
                if (e.Key == Key.Enter && e.KeyModifiers.HasFlag(KeyModifiers.Control))
                {
                    FinishTextEntry();
                    e.Handled = true;
                }
                // Allow normal Enter for new lines
                else if (e.Key == Key.Escape)
                {
                    CancelTextEntry();
                    e.Handled = true;
                }
            };
        }
        
        // Close on Escape
        KeyDown += OnKeyDown;
    }
    
    // Add Click Handler for OK Button
    private void OnTextConfirmClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        FinishTextEntry();
    }

    private System.Collections.Generic.List<Window> _hiddenTopmostWindows = new();

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);

        // Position window on the monitor where the cursor is
        var pointerPoint = Screens.ScreenFromPoint(this.Screens.All.FirstOrDefault()?.Bounds.TopLeft ?? new PixelPoint(0,0)); // Fallback
        
        // Get current pointer position
        var visual = this.GetVisualRoot();
        if (visual is IInputRoot inputRoot)
        {
             // This is tricky in Avalonia without a PointerDevice. 
             // Let's use a simpler approach: get it from the Screens API if possible or just let it be.
             // Actually, the best way in Avalonia is to check the mouse position via interaction or just use ScreenFromVisual
        }
        
        // Final working approach for multi-monitor:
        // Try to find screen from mouse position (OS specific usually, but Screens API has it)
        try 
        {
            // Note: In Avalonia 11, we can use window.Screens.ScreenFromPoint (we did above)
            // But how to get mouse pixel point? 
            // We'll use a heuristic for now: screen from MainWindow or mouse if we had it.
            // Let's stick with the most reliable: ScreenFromVisual if mouse is over it,
            // or ScreenFromPoint(MousePosition) using platform interop if needed.
            // For now, let's just make sure it stays on THE screen it was opened on.
        }
        catch {}

        // Defer Z-Order logic to ensure window is fully initialized
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (_viewModel != null)
            {
                _viewModel.VisualScaling = this.RenderScaling;
                _viewModel.ScreenOffset = this.Position;
                _viewModel.RefreshWindowRects(this.TryGetPlatformHandle()?.Handle);
            }

            // Ensure SnipWindow is absolutely on top of everything
            this.Topmost = true;
            this.Activate(); 
            this.Focus();

            // Temporarily lower existing Pin windows
            if (Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
            {
                foreach (var win in desktop.Windows)
                {
                    if (win is FloatingImageWindow floating && floating.Topmost && floating.IsVisible)
                    {
                        floating.Topmost = false;
                        _hiddenTopmostWindows.Add(floating);
                    }
                }
            }
            
            // Re-assert Topmost for self just in case
            this.Topmost = false;
            this.Topmost = true;
        }, Avalonia.Threading.DispatcherPriority.Input);
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        
        // Cleanup subscriptions
        _selectionRectSubscription?.Dispose();
        _drawingModeSubscription?.Dispose();
        
        // Close floating draw canvas
        _floatingDrawCanvas?.Close();
        _floatingDrawCanvas = null;
        
        // Clear window region before closing
        var hwnd = this.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (hwnd != IntPtr.Zero && OperatingSystem.IsWindows())
        {
            Win32Helpers.ClearWindowRegion(hwnd);
        }
        
        // NEW: Ensure recording is stopped if window is closed (e.g., via ESC or system close)
        if (_viewModel != null && _viewModel.RecState != RecordingState.Idle)
        {
            // Use Fire and Forget for the command, it handles internal state
            _viewModel.StopRecordingCommand.Execute().Subscribe();
        }

        // Restore Pin windows to Topmost
        foreach (var win in _hiddenTopmostWindows)
        {
            try 
            {
                if (win.IsVisible) // Ensure not closed
                    win.Topmost = true;
            }
            catch { /* Ignore if window closed */ }
        }
        _hiddenTopmostWindows.Clear();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        _viewModel = DataContext as SnipWindowViewModel;
        if (_viewModel != null)
        {
            this.GetObservable(Visual.BoundsProperty).Subscribe(b => _viewModel.ViewportSize = b.Size);
            _viewModel.IsMagnifierEnabled = true;
            _viewModel.CloseAction = () => 
            {
                Close();
            };
            
            _viewModel.HideAction = () =>
            {
                Hide();
            };
            
            // Subscribe to SelectionRect, CurrentState, and IsDrawingMode changes to update window region
            _selectionRectSubscription = _viewModel.WhenAnyValue(
                x => x.SelectionRect, 
                x => x.CurrentState, 
                x => x.IsDrawingMode)
                .Subscribe(tuple => UpdateWindowRegion(tuple.Item1, tuple.Item2, tuple.Item3));
            
            // Set up the snapshot capture action for drawing mode
            _viewModel.CaptureDrawingModeSnapshotAction = () => CaptureDrawingModeSnapshot();

            _viewModel.PickSaveFileAction = async () =>
            {
                 var topLevel = TopLevel.GetTopLevel(this);
                 if (topLevel == null) return null;
                 
                 bool isRecording = _viewModel.IsRecordingMode;
                 string defaultExt = isRecording ? _viewModel.RecordFormat : "png";
                 string fileTypeName = isRecording ? $"{defaultExt.ToUpper()} Video" : "PNG Image";
                 string pattern = $"*.{defaultExt}";
                 
                 var file = await topLevel.StorageProvider.SaveFilePickerAsync(new Avalonia.Platform.Storage.FilePickerSaveOptions
                 {
                     Title = isRecording ? "Save Recording" : "Save Screenshot",
                     DefaultExtension = defaultExt,
                     ShowOverwritePrompt = true,
                     SuggestedFileName = $"Capture_{DateTime.Now:yyyyMMdd_HHmmss}",
                     FileTypeChoices = new[]
                     {
                         new Avalonia.Platform.Storage.FilePickerFileType(fileTypeName) { Patterns = new[] { pattern } }
                     }
                 });
                 
                 return file?.Path.LocalPath;
            };

            _viewModel.OpenPinWindowAction = (bitmap, rect, color, thickness) =>
            {
                // Use settings directly from MainVm to ensure consistency
                bool hideDecoration = _viewModel.MainVm?.HideSnipPinDecoration ?? false;
                bool hideBorder = _viewModel.MainVm?.HideSnipPinBorder ?? false;
                
                var vm = new FloatingImageViewModel(bitmap, color, thickness, hideDecoration, hideBorder);
                
                try
                {
                    // Create Window
                    var win = new FloatingImageWindow
                    {
                        DataContext = vm,
                        Position = new PixelPoint((int)rect.X, (int)rect.Y),
                        Width = rect.Width,
                        Height = rect.Height
                    };
                    
                    win.Show();
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Error showing Floating Window: {ex}");
                }
            };
        }
    }

    // Text Dragging State
    private bool _isDraggingAnnotation;
    private Annotation? _draggingAnnotation;
    private Point _dragOffset;
    
    // Selection Moving State
    private bool _isMovingSelection;
    private Point _moveStartPoint;

    // Flag to Debounce Text Entry Finish
    private DateTime _lastTextFinishTime = DateTime.MinValue;

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

             FinishTextEntry();
             e.Handled = true;
             return;
        }

        var point = e.GetPosition(this);
        var props = e.GetCurrentPoint(this).Properties;
        var source = e.Source as Control;

        // 1. Text Interaction (Edit / Move) - High Priority
        if (props.IsLeftButtonPressed && _viewModel.IsDrawingMode && _viewModel.CurrentTool == AnnotationType.Text)
        {
             // Convert Window Point to Selection Space for Hit Testing
             var selectionSpacePoint = new Point(point.X - _viewModel.SelectionRect.X, point.Y - _viewModel.SelectionRect.Y);
             
             // Check for hit on existing text annotations (Top-most first)
             for (int i = _viewModel.Annotations.Count - 1; i >= 0; i--)
             {
                 var ann = _viewModel.Annotations[i];
                 if (ann.Type == AnnotationType.Text)
                 {
                     // Simple bounds estimation for hit testing
                     // Assume approx height/width based on font size and length
                     // A better way would be actual measure, but this is sufficient for now
                     double estimatedWidth = ann.Text.Length * ann.FontSize * 0.6; 
                     double estimatedHeight = ann.FontSize * 1.5;
                     
                     var rect = new Rect(ann.StartPoint.X, ann.StartPoint.Y, estimatedWidth, estimatedHeight);
                     if (rect.Contains(selectionSpacePoint))
                     {
                         if (e.ClickCount == 2)
                         {
                             // Double Click -> Edit Mode
                             _viewModel.Annotations.Remove(ann);
                             
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

        // Check if we clicked on a handle
        // Using Control instead of Ellipse because we changed XAML to use Grid (Panel)
        // Disable resize while recording to prevent mismatch between UI and actual recording region
        if (props.IsLeftButtonPressed && source is Control handle && handle.Classes.Contains("Handle"))
        {
            if (_viewModel.RecState != RecordingState.Idle)
            {
                e.Handled = true;
                return; // Block resize during recording
            }
            _isResizing = true;
            _resizeDirection = GetDirectionFromName(handle.Name);
            _resizeStartPoint = point;
            _originalRect = _viewModel.SelectionRect;
            e.Handled = true;
            return;
        }
        if (props.IsLeftButtonPressed)
        {
            if (_viewModel.IsDrawingMode && _viewModel.CurrentState == SnipState.Selected)
            {
                // Logic: If in drawing mode and clicked INSIDE the selection area, draw.
                if (_viewModel.SelectionRect.Contains(point))
                {
                    if (_viewModel.CurrentTool == AnnotationType.Text)
                    {
                        // Start Text Entry
                        _viewModel.IsEnteringText = true;
                        _viewModel.TextInputPosition = point;
                        _viewModel.PendingText = string.Empty;
                        
                        // Focus Textbox
                        var textBox = this.FindControl<TextBox>("TextInputOverlay");
                        textBox?.Focus();
                        e.Handled = true;
                        return;
                    }

                    // Start Drawing
                    _startPoint = point;
                    var relPoint = new Point(point.X - _viewModel.SelectionRect.X, point.Y - _viewModel.SelectionRect.Y);
                    
                    _currentAnnotation = new Annotation
                    {
                        Type = _viewModel.CurrentTool,
                        StartPoint = relPoint,
                        EndPoint = relPoint,
                        Color = _viewModel.SelectedColor,
                        Thickness = _viewModel.CurrentThickness,
                        FontSize = _viewModel.CurrentFontSize
                    };

                    if (_viewModel.CurrentTool == AnnotationType.Pen)
                    {
                        _currentAnnotation.AddPoint(relPoint);
                    }
                    
                    _viewModel.Annotations.Add(_currentAnnotation);
                    e.Handled = true;
                    return;
                }
            }

            // If clicking OUTSIDE or in Idle/Detecting, start NEW selection
            // Check if the click is within the toolbar bounds (coordinate-based check)
            // This works even when flyouts have closed before the event reaches us
            var toolbar = this.FindControl<Views.Controls.SnipToolbar>("Toolbar");
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
            
            // Also check visual tree for popups (they have their own visual tree)
            var sourceControl = e.Source as Control;
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
            
            if (_viewModel.CurrentState == SnipState.Idle || 
                _viewModel.CurrentState == SnipState.Detecting)
            {
                _startPoint = point;
                _viewModel.CurrentState = SnipState.Selecting;
                _viewModel.SelectionRect = new Rect(_startPoint, new Size(0, 0));
                _viewModel.IsDrawingMode = false;
                _viewModel.Annotations.Clear();
            }
            else if (_viewModel.CurrentState == SnipState.Selected && !_viewModel.IsDrawingMode)
            {
                // Check if we are in the "Wing Dead Zone" (outside selection but within 120px of border)
                // If so, we ignore the click instead of starting a new selection 
                // to prevent accidental resets when clicking near wings.
                var expandedBounds = _viewModel.SelectionRect.Inflate(120);
                if (expandedBounds.Contains(point) && !_viewModel.SelectionRect.Contains(point))
                {
                    e.Handled = true;
                    return;
                }

                if (_viewModel.SelectionRect.Contains(point))
                {
                    // Move Selection - ONLY if clicking on handles or specific move regions
                    // Since we removed Background="Transparent", we only get here if e.Source is a visible element.
                    // We allow move if source is a Handle or one of our decorative icons.
                    bool isHandle = sourceControl != null && (sourceControl.Classes.Contains("Handle") || sourceControl.Name?.Contains("InnerCorner") == true || sourceControl.Parent?.GetType() == typeof(Grid) && ((Grid)sourceControl.Parent).Name == "AccentCornersGrid");
                    
                    if (isHandle && _viewModel.RecState == RecordingState.Idle)
                    {
                        _isMovingSelection = true;
                        _moveStartPoint = point;
                        _originalRect = _viewModel.SelectionRect;
                        e.Handled = true;
                    }
                    else if (!isHandle)
                    {
                        // Clicked in the middle (click-through) or on non-handle border part.
                        // If it's the middle, Avalonia shouldn't even fire this on 'this' because it's transparent.
                        // But as a fallback, we do nothing.
                    }
                }
                else
                {
                    // Clicked far away from selection -> Start NEW selection
                     _startPoint = point;
                     _viewModel.CurrentState = SnipState.Selecting;
                     _viewModel.SelectionRect = new Rect(_startPoint, new Size(0, 0));
                     _viewModel.IsDrawingMode = false;
                     _viewModel.Annotations.Clear();
                }
            }
        }
        else if (props.IsRightButtonPressed)
        {
            if (_viewModel == null) return;

            // NEW: Prevent resetting or closing if we are actively recording
            if (_viewModel.RecState != RecordingState.Idle)
            {
                e.Handled = true;
                return;
            }

            if (_viewModel.CurrentState == SnipState.Selecting || 
                _viewModel.CurrentState == SnipState.Selected)
            {
                // Reset to Detecting to re-enable auto-detection (red box)
                _viewModel.CurrentState = SnipState.Detecting;
                _viewModel.SelectionRect = new Rect(0,0,0,0);
            }
            else
            {
                 Close();
            }
        }
    }

    private StandardCursorType _currentCursorType = StandardCursorType.Arrow;
    
    private void UpdateCursor(StandardCursorType type)
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
        var sourceControl = e.Source as Control;

        // --- Cursor Logic ---
        if (_isMovingSelection || _isDraggingAnnotation)
        {
            UpdateCursor(StandardCursorType.SizeAll);
        }
        else if (_isResizing)
        {
             // Handled by XAML/Capture
        }
        else if (_viewModel.CurrentState == SnipState.Selected)
        {
            bool cursorSet = false;

            // 1. Text Annotation Hover (Hand Cursor)
            if (_viewModel.IsDrawingMode && _viewModel.CurrentTool == AnnotationType.Text)
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
                             UpdateCursor(StandardCursorType.Hand);
                             cursorSet = true;
                             break;
                         }
                     }
                 }
            }

            // 2. Selection Loop Move Hover (SizeAll Cursor)
            if (!cursorSet && !_viewModel.IsDrawingMode && _viewModel.SelectionRect.Contains(currentPoint))
            {
                // Verify we are not over a handle
                bool isOverHandle = sourceControl != null && sourceControl.Classes.Contains("Handle");
                
                if (!isOverHandle)
                {
                    UpdateCursor(StandardCursorType.SizeAll);
                    cursorSet = true;
                }
            }

            if (!cursorSet)
            {
                UpdateCursor(StandardCursorType.Arrow);
            }
        }
        else
        {
            // For Idle, Detecting, Selecting -> Use Crosshair
            UpdateCursor(StandardCursorType.Cross);
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

             // Normalize Rect (prevent negative width/height)
             if (w < 0) { x += w; w = Math.Abs(w); } // Crude flip prevention or just abs
             if (h < 0) { y += h; h = Math.Abs(h); }
             
             _viewModel.SelectionRect = new Rect(x, y, w, h);
             return;
        }
        
        if (_isMovingSelection)
        {
             // Move Selection
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
             // Move Annotation
             var selectionSpacePoint = new Point(currentPoint.X - _viewModel.SelectionRect.X, currentPoint.Y - _viewModel.SelectionRect.Y);
             _draggingAnnotation.StartPoint = new Point(selectionSpacePoint.X - _dragOffset.X, selectionSpacePoint.Y - _dragOffset.Y);
             _draggingAnnotation.EndPoint = _draggingAnnotation.StartPoint; // Update EndPoint for consistency (Text usually ignores it but good practice)
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
            // Update Drawing
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
            // Ensure state is Selected
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
             // Check if we should adopt the DetectedRect (if move distance is small)
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
            _currentAnnotation = null; // Drawing finished
        }
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            if (_viewModel == null) return;

            // NEW: Prevent resetting or closing if we are actively recording
            if (_viewModel.RecState != RecordingState.Idle)
            {
                e.Handled = true;
                return;
            }

            if (_viewModel.IsDrawingMode)
            {
                _viewModel.IsDrawingMode = false;
                e.Handled = true;
            }
            else if (_viewModel.CurrentState == SnipState.Selecting || 
                     _viewModel.CurrentState == SnipState.Selected)
            {
                // Reset to Detecting to re-enable auto-detection (red box)
                _viewModel.CurrentState = SnipState.Detecting;
                _viewModel.SelectionRect = new Rect(0,0,0,0);
                e.Handled = true;
            }
            else
            {
                 Close();
            }
        }
        else if (e.Key == Key.S && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            if (_viewModel != null)
            {
                if (_viewModel.IsRecordingMode)
                {
                    _viewModel.StopRecordingCommand.Execute().Subscribe();
                }
                else
                {
                    _viewModel.SaveCommand.Execute().Subscribe();
                }
            }
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

    private void FinishTextEntry()
    {
        if (_viewModel == null || !_viewModel.IsEnteringText) return;
        
        if (!string.IsNullOrWhiteSpace(_viewModel.PendingText))
        {
            var relPoint = new Point(_viewModel.TextInputPosition.X - _viewModel.SelectionRect.X, _viewModel.TextInputPosition.Y - _viewModel.SelectionRect.Y);
            
            _viewModel.Annotations.Add(new Annotation
            {
                Type = AnnotationType.Text,
                StartPoint = relPoint,
                EndPoint = relPoint,
                Text = _viewModel.PendingText,
                Color = _viewModel.SelectedColor,
                FontSize = _viewModel.CurrentFontSize,
                FontFamily = _viewModel.CurrentFontFamily,
                IsBold = _viewModel.IsBold,
                IsItalic = _viewModel.IsItalic
            });
        }
        
        CancelTextEntry();
    }

    private void CancelTextEntry()
    {
        if (_viewModel == null) return;
        _viewModel.IsEnteringText = false;
        _viewModel.PendingText = string.Empty;
        _lastTextFinishTime = DateTime.Now; // Set debounce timestamp
        // Shift focus back to window to allow hotkeys etc.
        this.Focus();
    }

    /// <summary>
    /// Updates the window region to create a "hole" in the selection area for mouse pass-through.
    /// This allows clicking on underlying windows (like YouTube) while keeping the border UI interactive.
    /// The hole is disabled when in drawing mode to allow annotations.
    /// </summary>
    private void UpdateWindowRegion(Rect selectionRect, SnipState state, bool isDrawingMode)
    {
        if (!OperatingSystem.IsWindows()) return;
        
        var hwnd = this.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (hwnd == IntPtr.Zero) return;

        // Only apply region when:
        // 1. In Selected state with valid selection
        // 2. NOT in drawing mode (SetWindowRgn hole prevents ANY window from receiving mouse in that area)
        if (state == SnipState.Selected && selectionRect.Width > 10 && selectionRect.Height > 10 && !isDrawingMode)
        {
            // Get physical pixel dimensions (account for DPI scaling)
            double scaling = this.RenderScaling;
            int windowWidth = (int)(this.Bounds.Width * scaling);
            int windowHeight = (int)(this.Bounds.Height * scaling);
            
            // Convert selection rect to physical pixels
            var scaledRect = new Rect(
                selectionRect.X * scaling,
                selectionRect.Y * scaling,
                selectionRect.Width * scaling,
                selectionRect.Height * scaling
            );
            
            // Calculate toolbar rect in physical pixels (prevents toolbar from being clipped)
            Rect? toolbarRect = null;
            if (_viewModel != null)
            {
                // Toolbar position is stored in ViewModel, size is approximately 400x40
                const double toolbarWidth = 500;  // Slightly larger to account for flyouts
                const double toolbarHeight = 50;
                toolbarRect = new Rect(
                    _viewModel.ToolbarLeft * scaling,
                    _viewModel.ToolbarTop * scaling,
                    toolbarWidth * scaling,
                    toolbarHeight * scaling
                );
            }

            // EXTRA OPAQUE REGIONS: Wings
            // Wings are centered vertically on the selection edges, 100x60 logical
            var extraRegions = new System.Collections.Generic.List<Rect>();
            double wingsY = selectionRect.Center.Y - 30; // 60/2
            
            // Left Wing (outside, flush)
            extraRegions.Add(new Rect(
                (selectionRect.X - 100) * scaling,
                wingsY * scaling,
                100 * scaling,
                60 * scaling
            ));
            
            // Right Wing (outside, flush)
            extraRegions.Add(new Rect(
                selectionRect.Right * scaling,
                wingsY * scaling,
                100 * scaling,
                60 * scaling
            ));
            
            // Apply window region with hole.
            // Use 30px logical border (matching handles) instead of 120px to reduce dead zone.
            int borderWidth = (int)(30 * scaling);
            Win32Helpers.SetWindowHoleRegion(hwnd, windowWidth, windowHeight, scaledRect, borderWidth, toolbarRect, extraRegions);
        }
        else
        {
            // Clear region when not in Selected state OR in drawing mode
            // Drawing mode requires full window for mouse capture
            Win32Helpers.ClearWindowRegion(hwnd);
        }
    }

    /// <summary>
    /// Captures a snapshot of the selection area before closing the hole.
    /// This allows the user to see what they're annotating while in drawing mode.
    /// </summary>
    private void CaptureDrawingModeSnapshot()
    {
        if (_viewModel == null || !OperatingSystem.IsWindows()) return;
        
        var selectionRect = _viewModel.SelectionRect;
        if (selectionRect.Width < 10 || selectionRect.Height < 10) return;

        try
        {
            // Calculate physical pixels for the selection area
            double scaling = this.RenderScaling;
            var screenPos = this.Position; // physical pixels
            
            // Convert selection logical coordinates to physical and add window physical position
            int xPhysical = (int)(selectionRect.X * scaling) + screenPos.X;
            int yPhysical = (int)(selectionRect.Y * scaling) + screenPos.Y;
            int widthPhysical = (int)(selectionRect.Width * scaling);
            int heightPhysical = (int)(selectionRect.Height * scaling);

            if (widthPhysical <= 0 || heightPhysical <= 0) return;

            // Capture using GDI+
            using var bitmap = new System.Drawing.Bitmap(widthPhysical, heightPhysical);
            using (var g = System.Drawing.Graphics.FromImage(bitmap))
            {
                g.CopyFromScreen(
                    xPhysical, 
                    yPhysical, 
                    0, 0, 
                    new System.Drawing.Size(widthPhysical, heightPhysical));
            }

            // Convert to Avalonia Bitmap
            using var stream = new System.IO.MemoryStream();
            bitmap.Save(stream, System.Drawing.Imaging.ImageFormat.Png);
            stream.Seek(0, System.IO.SeekOrigin.Begin);
            
            _viewModel.DrawingModeSnapshot = new Avalonia.Media.Imaging.Bitmap(stream);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to capture drawing mode snapshot: {ex.Message}");
        }
    }
}
