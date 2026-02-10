using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using Avalonia.Controls.Primitives;
using GimmeCapture.ViewModels.Floating;
using GimmeCapture.Models;
using System;
using System.Threading.Tasks;
using System.IO;
using Avalonia.Interactivity;
using Avalonia.Threading;
using System.Reactive.Linq;

namespace GimmeCapture.Views.Floating;

public partial class FloatingVideoWindow : Window
{
    private double _startContentWidth;
    private double _startContentHeight;

    public FloatingVideoWindow()
    {
        InitializeComponent();
        
        // Use Tunneling for PointerPressed
        AddHandler(PointerPressedEvent, OnPointerPressed, RoutingStrategies.Tunnel);
        AddHandler(TappedEvent, OnTapped, RoutingStrategies.Bubble);
        
        KeyDown += OnKeyDown;
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is FloatingVideoViewModel vm)
        {
            vm.CloseAction = Close;
            vm.RequestRedraw = () => 
            {
                var image = this.FindControl<Image>("PinnedVideo");
                image?.InvalidateVisual();
            };

            vm.CopyAction = async () => 
            {
                if (string.IsNullOrEmpty(vm.VideoPath)) return;
                
                await Task.Run(() => 
                {
                    var escapedPath = vm.VideoPath.Replace("'", "''");
                    var command = $"Set-Clipboard -Path '{escapedPath}'";
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "powershell",
                        Arguments = $"-Command \"{command}\"",
                        CreateNoWindow = true,
                        UseShellExecute = false
                    });
                });
            };

            vm.SaveAction = async () => 
            {
                if (string.IsNullOrEmpty(vm.VideoPath)) return;

                var storage = this.StorageProvider;
                if (storage == null) return;

                var extension = Path.GetExtension(vm.VideoPath).TrimStart('.');
                var file = await storage.SaveFilePickerAsync(new Avalonia.Platform.Storage.FilePickerSaveOptions
                {
                    Title = "Save Video As",
                    DefaultExtension = extension,
                    FileTypeChoices = new[] { new Avalonia.Platform.Storage.FilePickerFileType(extension.ToUpper()) { Patterns = new[] { "*." + extension } } }
                });

                if (file != null)
                {
                    var targetPath = file.Path.LocalPath;
                    File.Copy(vm.VideoPath, targetPath, true);
                }
            };

            vm.FocusWindowAction = () =>
            {
                this.Focus();
            };

            vm.RequestSetWindowRect = (pos, w, h, cw, ch) =>
            {
                Position = pos;
                Width = w;
                Height = h;
            };

            // Force initial sync
            SyncWindowSizeToVideo();
            
            vm.PropertyChanged += (s, ev) =>
            {
                if (ev.PropertyName == nameof(FloatingVideoViewModel.ShowToolbar) || 
                    ev.PropertyName == nameof(FloatingVideoViewModel.WindowPadding) ||
                    ev.PropertyName == nameof(FloatingVideoViewModel.DisplayWidth) ||
                    ev.PropertyName == nameof(FloatingVideoViewModel.DisplayHeight))
                {
                    // Ensure the window resizes to accommodate the toolbar/padding changes
                    SyncWindowSizeToVideo();
                }
            };
        }
    }

    private void OnTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is FloatingVideoViewModel vm)
        {
            var visualSource = e.Source as Avalonia.Visual;
            while (visualSource != null)
            {
                if (visualSource is Button || visualSource is ToggleButton || visualSource is ContextMenu)
                    return;
                visualSource = visualSource.GetVisualParent();
            }

            if (vm.ShowToolbar) return; // Don't toggle off via tap
            
            if (!_isResizing && !_isDrawing && !_isMaybeMoving &&
                vm.CurrentTool == FloatingTool.None && vm.CurrentAnnotationTool == AnnotationType.None)
            {
                vm.ShowToolbar = true;
            }
            else
            {
                e.Handled = true;
            }
        }
    }

    private void SyncWindowSizeToVideo()
    {
        if (DataContext is FloatingVideoViewModel vm)
        {
            // Mirroring FloatingImageWindow.axaml.cs precisely:
            // Manual sizing is more predictable for preventing origin shift and clipping 
            // when decorations or toolbar visibility change.
            SizeToContent = SizeToContent.Manual; 
            
            var padding = vm.WindowPadding;
            // Calculate target content size (MainBorder + Padding)
            double border = 0;
            double contentW = vm.DisplayWidth + padding.Left + padding.Right + border;
            double contentH = vm.DisplayHeight + padding.Top + padding.Bottom + border;

            // Dynamic MinWidth to protect toolbar without breaking tiny snips
            MinWidth = vm.ShowToolbar ? (380 + padding.Left + padding.Right) : 50;
            MinHeight = vm.ShowToolbar ? (150 + padding.Top + padding.Bottom) : 50;

            Width = System.Math.Max(contentW, MinWidth);
            Height = System.Math.Max(contentH, MinHeight);
            
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
    private Point _mouseDownPoint;
    private bool _isMaybeMoving;
    private PointerPressedEventArgs? _pendingMoveEvent;

    // Drawing State
    private Annotation? _currentAnnotation;
    private bool _isDrawing;
    private Point _startPoint;
    private DateTime _lastTextFinishTime = DateTime.MinValue;
    private bool _isDraggingAnnotation;
    private Annotation? _draggingAnnotation;
    private Point _dragOffset;

    private enum ResizeDirection
    {
        None, TopLeft, TopRight, BottomLeft, BottomRight, Top, Bottom, Left, Right
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is not FloatingVideoViewModel vm) return;
        var source = e.Source as Control;
        var pCurrentPoint = e.GetCurrentPoint(this);
        var pointerPos = pCurrentPoint.Position;
        var pProperties = pCurrentPoint.Properties;

        // 1. Resize handles check
        if (pProperties.IsLeftButtonPressed && source != null && source.Classes.Contains("Handle"))
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
        if (pProperties.IsLeftButtonPressed && vm.CurrentAnnotationTool != AnnotationType.None)
        {
            var imageControl = this.FindControl<Image>("PinnedVideo");
            if (imageControl == null) 
            {
                System.Diagnostics.Debug.WriteLine("[Video Drawing Debug] PinnedVideo control not found!");
                return;
            }
            var pointerPosOnImage = e.GetPosition(imageControl);

            // Restrict drawing interaction to the image area to allow toolbar clicks
            if (pointerPosOnImage.X < 0 || pointerPosOnImage.Y < 0 || 
                pointerPosOnImage.X > imageControl.Bounds.Width || 
                pointerPosOnImage.Y > imageControl.Bounds.Height)
            {
                System.Diagnostics.Debug.WriteLine($"[Video Drawing Debug] Pointer outside video area: {pointerPosOnImage}");
                return;
            }

            System.Diagnostics.Debug.WriteLine($"[Video Drawing Debug] PointerPressed on video at {pointerPosOnImage}, Tool={vm.CurrentAnnotationTool}");

            if ((DateTime.Now - _lastTextFinishTime).TotalMilliseconds < 300) return;

            if (vm.IsEnteringText)
            {
                var src = e.Source as Control;
                if (src != null && (src.Name == "TextInputOverlay" || src.FindAncestorOfType<TextBox>() != null)) return;
                // Confirm text if clicking elsewhere
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

            System.Diagnostics.Debug.WriteLine($"[Video Drawing Debug] Starting drawing: {_currentAnnotation.Type} at {_startPoint}");

            if (_currentAnnotation.Type == AnnotationType.Pen)
                _currentAnnotation.AddPoint(pointerPosOnImage);

            vm.AddAnnotation(_currentAnnotation);
            e.Pointer.Capture(this);
            e.Handled = true;
            return;
        }

        // 4. Default: Window Move preparation
        if (pProperties.IsLeftButtonPressed)
        {
            _isMaybeMoving = true;
            _startPosition = Position;
            _mouseDownPoint = e.GetPosition(this); // Using window coordinates
            // Don't capture yet, waiting for drag threshold
            _pendingMoveEvent = e; 
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (DataContext is not FloatingVideoViewModel vm) return;
        var currentPoint = e.GetCurrentPoint(this);
        var pointerPos = currentPoint.Position;

        if (_isResizing)
        {
            PerformResizing(e);
        }
        else if (_isDrawing && _currentAnnotation != null)
        {
            var imageControl = this.FindControl<Image>("PinnedVideo");
            if (imageControl != null)
            {
                var pointerPosOnImage = e.GetPosition(imageControl);
                // System.Diagnostics.Debug.WriteLine($"[Video Drawing Debug] PointerMoved drawing at {pointerPosOnImage}");
                if (_currentAnnotation.Type == AnnotationType.Pen)
                    _currentAnnotation.AddPoint(pointerPosOnImage);
                else
                    _currentAnnotation.EndPoint = pointerPosOnImage;
                e.Handled = true;
            }
        }
        else if (_isDraggingAnnotation && _draggingAnnotation != null)
        {
            var imageControl = this.FindControl<Image>("PinnedVideo");
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
        else if (_isMaybeMoving)
        {
             var delta = pointerPos - _mouseDownPoint;
             if (Math.Abs(delta.X) > 5 || Math.Abs(delta.Y) > 5)
             {
                 _isMaybeMoving = false;
                 BeginMoveDrag(e);
             }
        }
    }

    private void BeginMoveDrag(PointerEventArgs e)
    {
        // Workaround to initiate drag if we can, or just manual move support if BeginMoveDrag(PointerPressedEventArgs) required
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

    private void PerformResizing(PointerEventArgs e)
    {
        if (DataContext is not FloatingVideoViewModel vm) return;

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
                double aspectRatio = vm.OriginalWidth / vm.OriginalHeight;
                
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

            // Re-calculate X/Y
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
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Resize error: {ex.Message}");
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        
        if (_isDrawing)
        {
             System.Diagnostics.Debug.WriteLine($"[Video Drawing Debug] PointerReleased. Finished drawing {_currentAnnotation?.Type}");
        }

        if (_isResizing)
        {
            e.Pointer.Capture(null); 
            _isResizing = false;

            if (DataContext is FloatingVideoViewModel videoVm)
            {
                videoVm.PushResizeAction(_startPosition, _startSize.Width, _startSize.Height, _startContentWidth, _startContentHeight,
                                       Position, Width, Height, videoVm.DisplayWidth, videoVm.DisplayHeight);
            }
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

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not FloatingVideoViewModel vm) return;

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

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        
        if (change.Property == BoundsProperty)
        {
             // ACTIVE SYNC: We no longer update the VM from bounds.
             // Everything flows from VM -> XAML and Resize -> VM -> XAML.
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
}
