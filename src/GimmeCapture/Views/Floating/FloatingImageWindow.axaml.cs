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

namespace GimmeCapture.Views.Floating;

public partial class FloatingImageWindow : Window
{
    public FloatingImageWindow()
    {
        InitializeComponent();
        
        PointerPressed += OnPointerPressed;
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
                    SyncWindowSizeToImage();
                }
            };

            // Implementation for spawning a NEW pinned window from selection
            vm.OpenPinWindowAction = (bitmap, rect, color, thickness, runAI) =>
            {
                // Reuse the same logic as SnipWindow to spawn new windows
                var newVm = new FloatingImageViewModel(bitmap, color, thickness, vm.HidePinDecoration, vm.HidePinBorder, 
                    vm.ClipboardService, vm.AIResourceService);
                
                newVm.WingScale = vm.WingScale;
                newVm.CornerIconScale = vm.CornerIconScale;
                
                var padding = newVm.WindowPadding;
                
                // Position the new window near the current one for feedback, 
                // but offset it so it's clearly a new window.
                var newWin = new FloatingImageWindow
                {
                    DataContext = newVm,
                    Position = new PixelPoint(Position.X + 40, Position.Y + 40),
                    Width = bitmap.Size.Width + padding.Left + padding.Right,
                    Height = bitmap.Size.Height + padding.Top + padding.Bottom
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

    // Selection State
    private bool _isSelecting;
    private Point _selectionStartPoint;

    private enum ResizeDirection
    {
        None, TopLeft, TopRight, BottomLeft, BottomRight, Top, Bottom, Left, Right
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
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
                
                // 設定 Image Stretch 為 Fill 讓圖片填滿整個區域
                var pinnedImage = this.FindControl<Image>("PinnedImage");
                if (pinnedImage != null)
                {
                    pinnedImage.Stretch = Avalonia.Media.Stretch.Fill;
                    pinnedImage.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch;
                    pinnedImage.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Stretch;
                }
                
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

        // 3. Selection Tool vs Movement
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            if (vm.CurrentTool != FloatingTool.None)
            {
                // Start selection (Save start point relative to PinnedImage)
                var imageControl = this.FindControl<Image>("PinnedImage");
                if (imageControl != null)
                {
                    _isSelecting = true;
                    _selectionStartPoint = e.GetPosition(imageControl);
                    e.Pointer.Capture(this);
                    e.Handled = true;
                }
            }
            else
            {
                BeginMoveDrag(e);
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
            // Get position relative to PinnedImage to ensure it's in image space
            var imageControl = this.FindControl<Image>("PinnedImage");
            if (imageControl == null) return;

            var relativePos = e.GetPosition(imageControl);
            var startPos = _selectionStartPoint; // We'll store start as relative to image too
            
            double x = Math.Min(startPos.X, relativePos.X);
            double y = Math.Min(startPos.Y, relativePos.Y);
            double w = Math.Abs(startPos.X - relativePos.X);
            double h = Math.Abs(startPos.Y - relativePos.Y);
            
            vm.SelectionRect = new Rect(x, y, w, h);
            e.Handled = true;
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
}
