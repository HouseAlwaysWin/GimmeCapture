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

            // Force initial sync
            SyncWindowSizeToVideo();
            
            vm.PropertyChanged += (s, ev) =>
            {
                if (ev.PropertyName == nameof(FloatingVideoViewModel.ShowToolbar) || 
                    ev.PropertyName == nameof(FloatingVideoViewModel.WindowPadding))
                {
                    if (ev.PropertyName == nameof(FloatingVideoViewModel.ShowToolbar))
                    {
                        SizeToContent = SizeToContent.Manual;
                        var padding = vm.WindowPadding;
                        double toolbarHeight = vm.ShowToolbar ? 60 : 0;
                        Height = vm.DisplayHeight + padding.Top + padding.Bottom + toolbarHeight;
                    }
                    InvalidateMeasure();
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
            SizeToContent = SizeToContent.Manual; 
            
            var padding = vm.WindowPadding;
            double toolbarHeight = vm.ShowToolbar ? 60 : 0;
            
            Width = vm.DisplayWidth + padding.Left + padding.Right;
            Height = vm.DisplayHeight + padding.Top + padding.Bottom + toolbarHeight;
            
            InvalidateMeasure();
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
             var imageControl = this.FindControl<Image>("PinnedVideo");
             if (imageControl != null && DataContext is FloatingVideoViewModel vm)
             {
                 vm.DisplayWidth = imageControl.Bounds.Width;
                 vm.DisplayHeight = imageControl.Bounds.Height;
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
}
