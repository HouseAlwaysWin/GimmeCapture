using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using Avalonia.Controls.Primitives;
using GimmeCapture.ViewModels.Floating;
using GimmeCapture.Models;
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
        
        // Use Tunneling for PointerPressed to catch events before children can swallow them
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
                    if (ev.PropertyName == nameof(FloatingImageViewModel.ShowToolbar))
                    {
                        SizeToContent = SizeToContent.WidthAndHeight;
                    }
                    
                    SyncWindowSizeToImage();
                }
            };

            if (vm.OpenPinWindowAction == null)
            {
                vm.OpenPinWindowAction = (bitmap, rect, color, thickness, runAI) =>
                {
                    var newVm = new FloatingImageViewModel(bitmap, rect.Width, rect.Height, color, thickness, vm.HidePinDecoration, vm.HidePinBorder, 
                        vm.ClipboardService, vm.AIResourceService, vm.AppSettingsService);
                    
                    newVm.WingScale = vm.WingScale;
                    newVm.CornerIconScale = vm.CornerIconScale;
                    
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

            vm.FocusWindowAction = () =>
            {
                this.Focus();
            };

            vm.RequestSetWindowRect = (pos, w, h) =>
            {
                Position = pos;
                Width = w;
                Height = h;
            };
        }
    }

    private void OnTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is FloatingImageViewModel vm)
        {
            var visualSource = e.Source as Avalonia.Visual;
            while (visualSource != null)
            {
                if (visualSource is Button || visualSource is ToggleButton || visualSource is ContextMenu)
                    return;
                visualSource = visualSource.GetVisualParent();
            }

            if (vm.IsProcessing || vm.DiagnosticText.Contains("AI Trigger"))
            {
                System.Diagnostics.Debug.WriteLine("FloatingWindow: Skipping Tap Diagnostic (AI Active)");
            }
            else
            {
                vm.DiagnosticText = $"Tap: Tool={vm.CurrentTool}, AnnotTool={vm.CurrentAnnotationTool}, AI={vm.IsInteractiveSelectionMode}";
            }

            if (vm.ShowToolbar) return; // Don't toggle off via tap (as per user request: "打開後不關閉")
            
            if (!_isResizing && !_isSelecting && !_isMaybeMoving && !_isAIPointing && !_isDrawing &&
                vm.CurrentTool == FloatingTool.None && vm.CurrentAnnotationTool == AnnotationType.None && !vm.IsInteractiveSelectionMode)
            {
                vm.ShowToolbar = true;
            }
            else
            {
                e.Handled = true;
            }
        }
    }

    private void SyncWindowSizeToImage()
    {
        if (DataContext is FloatingImageViewModel vm) 
        {
             // Fix for "Position Offset" issue:
             // Do NOT use SizeToContent.WidthAndHeight as it can unpredictable shift the window origin 
             // or render size on some platforms/setups, causing the "Pin ran off" visual glich.
             // Instead, we manually calculate and set the size.
             
             SizeToContent = SizeToContent.Manual;

             var padding = vm.WindowPadding;
             // Calculate target content size (Image + Padding)
             double contentW = vm.DisplayWidth + padding.Left + padding.Right;
             double contentH = vm.DisplayHeight + padding.Top + padding.Bottom;
             
             // Add Toolbar allowance if visible
             // Grid 'Auto' row will handle exact rendering, but we need to provide enough Window space
             // so the '*' row (Image) gets its desired height.
             if (vm.ShowToolbar)
             {
                 contentH += 42; // Estimated Toolbar Height (Standard)
             }
             
             Width = contentW;
             Height = contentH;
             
             InvalidateMeasure();
             InvalidateArrange();
        }
    }

    // Resize Fields
    private bool _isResizing;
    private ResizeDirection _resizeDirection;
    private Point _resizeStartPoint; 
    private PixelPoint _startPosition;
    private Size _startSize;

    private bool _isMaybeMoving;
    private PointerPressedEventArgs? _pendingMoveEvent;

    // Selection State
    private bool _isSelecting;
    private Point _selectionStartPoint;
    private bool _isAIPointing;

    // Drawing State
    private Annotation? _currentAnnotation;
    private bool _isDrawing;
    private Point _startPoint;
    private DateTime _lastTextFinishTime = DateTime.MinValue;
    private bool _isDraggingAnnotation;
    private Annotation? _draggingAnnotation;
    private Point _dragOffset;
    private Point _mouseDownPoint;

    private enum ResizeDirection
    {
        None, TopLeft, TopRight, BottomLeft, BottomRight, Top, Bottom, Left, Right
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is not FloatingImageViewModel vm) return;
        var source = e.Source as Control;
        var pCurrentPoint = e.GetCurrentPoint(this);
        var pointerPos = pCurrentPoint.Position;
        var pProperties = pCurrentPoint.Properties;

        // 1. Resize handles check
        if (pProperties.IsLeftButtonPressed && 
            vm.CurrentTool == FloatingTool.None && 
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

        // 3. Drawing / Text Interaction
        if (pProperties.IsLeftButtonPressed && vm.CurrentAnnotationTool != AnnotationType.None && !vm.IsProcessing)
        {
            var imageControl = this.FindControl<Image>("PinnedImage");
            if (imageControl == null) 
            {
               System.Diagnostics.Debug.WriteLine("[Drawing Debug] PinnedImage control not found!");
               return;
            }
            var pointerPosOnImage = e.GetPosition(imageControl);

            // Restrict drawing interaction to the image area to allow toolbar clicks
            if (pointerPosOnImage.X < 0 || pointerPosOnImage.Y < 0 || 
                pointerPosOnImage.X > imageControl.Bounds.Width || 
                pointerPosOnImage.Y > imageControl.Bounds.Height)
            {
                System.Diagnostics.Debug.WriteLine($"[Drawing Debug] Pointer outside image area: {pointerPosOnImage}");
                return;
            }

            System.Diagnostics.Debug.WriteLine($"[Drawing Debug] PointerPressed on image at {pointerPosOnImage}, Tool={vm.CurrentAnnotationTool}");

            if ((DateTime.Now - _lastTextFinishTime).TotalMilliseconds < 300) return;

            if (vm.IsEnteringText)
            {
                var src = e.Source as Control;
                if (src != null && (src.Name == "TextInputOverlay" || src.FindAncestorOfType<TextBox>() != null)) return;
                // If clicking outside, confirm text
                vm.ConfirmTextEntryCommand.Execute(System.Reactive.Unit.Default).Subscribe();
                e.Handled = true;
                return;
            }

            if (vm.CurrentAnnotationTool == AnnotationType.Text)
            {
                for (int i = vm.Annotations.Count - 1; i >= 0; i--)
                {
                    var ann = vm.Annotations[i];
                    if (ann.Type == AnnotationType.Text)
                    {
                        double estimatedWidth = ann.Text.Length * ann.FontSize * 0.6;
                        double estimatedHeight = ann.FontSize * 1.5;
                        var rect = new Rect(ann.StartPoint.X, ann.StartPoint.Y, estimatedWidth, estimatedHeight);
                        if (rect.Contains(pointerPosOnImage))
                        {
                            if (e.ClickCount == 2)
                            {
                                vm.Annotations.Remove(ann);
                                vm.IsEnteringText = true;
                                vm.TextInputPosition = ann.StartPoint;
                                vm.PendingText = ann.Text;
                                vm.CurrentFontSize = ann.FontSize;
                                vm.IsBold = ann.IsBold;
                                vm.IsItalic = ann.IsItalic;
                                vm.SelectedColor = ann.Color;

                                var textBox = this.FindControl<TextBox>("TextInputOverlay");
                                Avalonia.Threading.Dispatcher.UIThread.Post(() => textBox?.Focus());
                                e.Handled = true;
                                return;
                            }
                            else
                            {
                                _isDraggingAnnotation = true;
                                _draggingAnnotation = ann;
                                _dragOffset = new Point(pointerPosOnImage.X - ann.StartPoint.X, pointerPosOnImage.Y - ann.StartPoint.Y);
                                e.Pointer.Capture(this);
                                e.Handled = true;
                                return;
                            }
                        }
                    }
                }
                
                // Start NEW Text Entry
                vm.IsEnteringText = true;
                vm.TextInputPosition = pointerPosOnImage;
                vm.PendingText = string.Empty;
                var textBoxNew = this.FindControl<TextBox>("TextInputOverlay");
                textBoxNew?.Focus();
                e.Handled = true;
                return;
            }

            // Start Drawing Shape/Pen
            _isDrawing = true;
            _startPoint = pointerPosOnImage;
            _currentAnnotation = new Annotation
            {
                Type = vm.CurrentAnnotationTool,
                StartPoint = pointerPosOnImage,
                EndPoint = pointerPosOnImage,
                Color = vm.SelectedColor,
                Thickness = vm.CurrentThickness,
                FontSize = vm.CurrentFontSize,
                IsBold = vm.IsBold,
                IsItalic = vm.IsItalic
            };

            System.Diagnostics.Debug.WriteLine($"[Drawing Debug] Starting drawing: {_currentAnnotation.Type} at {_startPoint}");

            if (_currentAnnotation.Type == AnnotationType.Pen)
                _currentAnnotation.AddPoint(pointerPosOnImage);

            vm.AddAnnotation(_currentAnnotation);
            e.Pointer.Capture(this);
            e.Handled = true;
            return;
        }

        // 4. Selection Tool 
        if (pProperties.IsLeftButtonPressed && vm.CurrentTool == FloatingTool.Selection && !vm.IsProcessing)
        {
            var imageControl = this.FindControl<Image>("PinnedImage");
            if (imageControl != null)
            {
                var pos = e.GetPosition(imageControl);
                if (new Rect(0, 0, imageControl.Bounds.Width, imageControl.Bounds.Height).Contains(pos))
                {
                    _isSelecting = true;
                    _selectionStartPoint = pos;
                    e.Pointer.Capture(this);
                    e.Handled = true;
                    return;
                }
            }
        }

        // 5. AI Interaction
        if (pProperties.IsLeftButtonPressed && vm.IsPointRemovalMode && !vm.IsProcessing)
        {
            _isAIPointing = true;
            e.Pointer.Capture(this);
            e.Handled = true;
            return;
        }

        // 6. Default: Window Move preparation
        if (pProperties.IsLeftButtonPressed)
        {
            _isMaybeMoving = true;
            _startPosition = Position;
            _mouseDownPoint = e.GetPosition(this); // Using window coordinates
            // Don't capture yet, waiting for drag threshold
            _pendingMoveEvent = e; 
        }
        else if (pProperties.IsRightButtonPressed)
        {
            if (vm.IsPointRemovalMode)
            {
                vm.UndoLastInteractivePoint();
                e.Handled = true;
            }
            else if (vm.IsSelectionMode)
            {
                vm.SelectionRect = new Rect();
                e.Handled = true;
            }
        }
    }

    private void BeginMoveDrag(PointerEventArgs e)
    {
        // Workaround: initiate drag from saved event if available
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

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (DataContext is not FloatingImageViewModel vm) return;

        var currentPoint = e.GetCurrentPoint(this);
        var pointerPos = currentPoint.Position;

        if (_isResizing)
        {
            try
            {
                var screenPos = this.PointToScreen(pointerPos).ToPoint(1.0);
                var deltaX = screenPos.X - _resizeStartPoint.X;
                var deltaY = screenPos.Y - _resizeStartPoint.Y;

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
                        x = _startPosition.X + deltaX; 
                        y = _startPosition.Y + deltaY; 
                        w = _startSize.Width - deltaWidth; 
                        h = _startSize.Height - deltaHeight; 
                        break;
                    case ResizeDirection.TopRight:
                        y = _startPosition.Y + deltaY; 
                        w = _startSize.Width + deltaWidth; 
                        h = _startSize.Height - deltaHeight; 
                        break;
                    case ResizeDirection.BottomLeft:
                        x = _startPosition.X + deltaX; 
                        w = _startSize.Width - deltaWidth; 
                        h = _startSize.Height + deltaHeight; 
                        break;
                    case ResizeDirection.BottomRight:
                        w = _startSize.Width + deltaWidth; 
                        h = _startSize.Height + deltaHeight; 
                        break;
                    case ResizeDirection.Top:
                        y = _startPosition.Y + deltaY; 
                        h = _startSize.Height - deltaHeight; 
                        break;
                    case ResizeDirection.Bottom:
                        h = _startSize.Height + deltaHeight; 
                        break;
                    case ResizeDirection.Left:
                        x = _startPosition.X + deltaX; 
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
                
                // Content size will be updated automatically by the Grid layout
                // We don't need to manually set DisplayWidth/Height here anymore

                
                e.Handled = true;
                InvalidateMeasure();
                InvalidateArrange();
            }
            catch (Exception) { }
        }
        else if (_isSelecting)
        {
            var imageControl = this.FindControl<Image>("PinnedImage");
            if (imageControl != null)
            {
                var pos = e.GetPosition(imageControl);
                // Clamp to image bounds
                double x = Math.Max(0, Math.Min(pos.X, imageControl.Bounds.Width));
                double y = Math.Max(0, Math.Min(pos.Y, imageControl.Bounds.Height));
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
            var imageControl = this.FindControl<Image>("PinnedImage");
            if (imageControl != null)
            {
                var pointerPosOnImage = e.GetPosition(imageControl);
                // System.Diagnostics.Debug.WriteLine($"[Drawing Debug] PointerMoved drawing at {pointerPosOnImage}");
                if (_currentAnnotation.Type == AnnotationType.Pen)
                {
                    _currentAnnotation.AddPoint(pointerPosOnImage);
                }
                else
                {
                    _currentAnnotation.EndPoint = pointerPosOnImage;
                }
                e.Handled = true;
            }
        }
        else if (_isDraggingAnnotation && _draggingAnnotation != null)
        {
             var imageControl = this.FindControl<Image>("PinnedImage");
            if (imageControl != null)
            {
                var pointerPosOnImage = e.GetPosition(imageControl);
                var newStart = new Point(pointerPosOnImage.X - _dragOffset.X, pointerPosOnImage.Y - _dragOffset.Y);
                
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

    protected override async void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_isResizing)
        {
            e.Pointer.Capture(null); 
            _isResizing = false;

            if (DataContext is FloatingImageViewModel imageVm)
            {
                imageVm.PushResizeAction(_startPosition, _startSize.Width, _startSize.Height, Position, Width, Height);
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
        else if (_isAIPointing)
        {
            var imageControl = this.FindControl<Image>("PinnedImage");
            if (imageControl != null && DataContext is FloatingImageViewModel vm && vm.Image != null)
            {
                var pos = e.GetPosition(imageControl);
                var renderedRect = GetImageRenderedRect(imageControl);
                
                if (renderedRect.Contains(pos))
                {
                    var relativeX = pos.X - renderedRect.X;
                    var relativeY = pos.Y - renderedRect.Y;
                    var sourceSize = vm.Image.PixelSize;
                    var pixelX = relativeX * (sourceSize.Width / renderedRect.Width);
                    var pixelY = relativeY * (sourceSize.Height / renderedRect.Height);
                    bool isPositive = !e.KeyModifiers.HasFlag(KeyModifiers.Shift);

                    await vm.HandlePointClickAsync(pixelX, pixelY, isPositive);
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
            if (vm.IsPointRemovalMode || vm.IsSelectionMode)
            {
                e.Handled = true;
            }
        }
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not FloatingImageViewModel vm) return;

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
            vm.CopyCommand.Execute().Subscribe();
            e.Handled = true;
        }
        else if (e.Key == Key.X && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            vm.CutCommand.Execute().Subscribe();
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
        
        if (change.Property == BoundsProperty)
        {
             var imageControl = this.FindControl<Image>("PinnedImage");
             if (imageControl != null && DataContext is FloatingImageViewModel vm)
             {
                 vm.DisplayWidth = imageControl.Bounds.Width;
                 vm.DisplayHeight = imageControl.Bounds.Height;
             }
        }
    }

    private Rect GetImageRenderedRect(Image img)
    {
        if (img.Source == null || img.Bounds.Width <= 0 || img.Bounds.Height <= 0)
            return new Rect();

        var viewSize = img.Bounds.Size;
        var sourceSize = img.Source.Size;

        double scale = Math.Min(viewSize.Width / sourceSize.Width, viewSize.Height / sourceSize.Height);
        double w = sourceSize.Width * scale;
        double h = sourceSize.Height * scale;
        double x = (viewSize.Width - w) / 2;
        double y = (viewSize.Height - h) / 2;

        return new Rect(x, y, w, h);
    }

    private void OnAIToolSelected(object sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() => {
            var aiToolsButton = this.FindControl<Button>("AIToolsButton");
            aiToolsButton?.Flyout?.Hide();
        });
    }
}
