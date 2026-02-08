using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using GimmeCapture.ViewModels.Floating;
using System;
using System.Threading.Tasks;
using System.IO;
using Avalonia.VisualTree;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace GimmeCapture.Views.Floating;

public partial class FloatingVideoWindow : Window
{
    public FloatingVideoWindow()
    {
        InitializeComponent();
        
        PointerPressed += OnPointerPressed;
        KeyDown += OnKeyDown;
        AddHandler(TappedEvent, OnTapped, Avalonia.Interactivity.RoutingStrategies.Bubble);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is FloatingVideoViewModel vm)
        {
            vm.CloseAction = Close;
            vm.RequestRedraw = () => 
            {
                // Force specialized redraw of the image control
                var image = this.FindControl<Image>("PinnedVideo");
                image?.InvalidateVisual();
            };

            vm.CopyAction = async () => 
            {
                if (string.IsNullOrEmpty(vm.VideoPath)) return;
                
                await Task.Run(() => 
                {
                    // Use PowerShell to copy file to clipboard
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

                var extension = System.IO.Path.GetExtension(vm.VideoPath).TrimStart('.');
                var file = await storage.SaveFilePickerAsync(new Avalonia.Platform.Storage.FilePickerSaveOptions
                {
                    Title = "Save Video As",
                    DefaultExtension = extension,
                    FileTypeChoices = new[] { new Avalonia.Platform.Storage.FilePickerFileType(extension.ToUpper()) { Patterns = new[] { "*." + extension } } }
                });

                if (file != null)
                {
                    var targetPath = file.Path.LocalPath;
                    System.IO.File.Copy(vm.VideoPath, targetPath, true);
                }
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
                        SizeToContent = SizeToContent.Manual; // Temporarily disable to force re-measure if needed, or keep Manual
                        // Actually, for toolbar toggle we might need to adjust Height
                        var padding = vm.WindowPadding;
                        double toolbarHeight = vm.ShowToolbar ? 42 : 0;
                        Height = vm.DisplayHeight + padding.Top + padding.Bottom + toolbarHeight;
                    }
                    InvalidateMeasure();
                }
            };
        }
    }

    private void SyncWindowSizeToVideo()
    {
        if (DataContext is FloatingVideoViewModel vm)
        {
            // Initial size setup
            var padding = vm.WindowPadding;
            double toolbarHeight = vm.ShowToolbar ? 42 : 0;
            
            Width = vm.DisplayWidth + padding.Left + padding.Right;
            Height = vm.DisplayHeight + padding.Top + padding.Bottom + toolbarHeight;
            
            InvalidateMeasure();
        }
    }

    // Resize Fields
    private bool _isResizing;
    private ResizeDirection _resizeDirection;
    private Point _resizeStartPoint; // Screen Coordinates
    
    // Start State
    private PixelPoint _startPosition;
    private Size _startSize; // Logical
    
    // Manual Drag State
    private Point _pointerPressedPoint;
    private bool _isMaybeMoving;
    private PointerPressedEventArgs? _pendingMoveEvent;

    private enum ResizeDirection
    {
        None, TopLeft, TopRight, BottomLeft, BottomRight, Top, Bottom, Left, Right
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var source = e.Source as Control;
        
        // Ensure we hit a handle
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed && source != null && source.Classes.Contains("Handle"))
        {
            _isResizing = true;
            _resizeDirection = GetDirectionFromName(source.Name);
            
            try
            {
                var p = e.GetCurrentPoint(this);
                _resizeStartPoint = this.PointToScreen(p.Position).ToPoint(1.0);
                
                _startPosition = Position;
                _startSize = Bounds.Size;
                
                e.Pointer.Capture(this);
                e.Handled = true;
            }
            catch (Exception)
            {
                _isResizing = false;
            }
            return;
        }

        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            // Do NOT call BeginMoveDrag immediately, as it swallows MouseUp/Tapped events.
            // Instead, wait for a small movement threshold.
            _isMaybeMoving = true;
            _pointerPressedPoint = this.PointToScreen(e.GetPosition(this)).ToPoint(1.0);
            _pendingMoveEvent = e;
            e.Pointer.Capture(this);
        }
    }
    
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        
        if (_isResizing)
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
                
                // Update ViewModel display size
                if (DataContext is FloatingVideoViewModel vm)
                {
                    var padding = vm.WindowPadding;
                    vm.DisplayWidth = Math.Max(1, w - padding.Left - padding.Right);
                    
                    double toolbarHeight = vm.ShowToolbar ? 42 : 0;
                    vm.DisplayHeight = Math.Max(1, h - padding.Top - padding.Bottom - toolbarHeight);
                }

                e.Handled = true;
                
                InvalidateMeasure();
                InvalidateArrange();
            }
            catch (Exception)
            {
                // Suppress runtime resize errors
            }
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

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_isResizing)
        {
            e.Pointer.Capture(null); // Release tracking
            _isResizing = false;
            _resizeDirection = ResizeDirection.None;
        }
        else if (_isMaybeMoving)
        {
            // If we released without moving enough, it's a click, not a drag.
            e.Pointer.Capture(null);
            _isMaybeMoving = false;
            _pendingMoveEvent = null;
        }
    }
    
    private void OnTapped(object? sender, Avalonia.Input.TappedEventArgs e)
    {
        if (DataContext is FloatingVideoViewModel vm)
        {
            // Filter out interactive elements
            var visualSource = e.Source as Avalonia.Visual;
            while (visualSource != null)
            {
                if (visualSource is Button || visualSource is ContextMenu)
                    return;
                visualSource = visualSource.GetVisualParent();
            }

            // Toggle toolbar if not resizing/moving
            if (!_isResizing)
            {
                vm.ToggleToolbarCommand.Execute().Subscribe();
            }
        }
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
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
