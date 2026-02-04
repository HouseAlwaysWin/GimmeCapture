using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using GimmeCapture.ViewModels.Floating;
using System;
using System.Threading.Tasks;
using System.IO;

namespace GimmeCapture.Views.Floating;

public partial class FloatingVideoWindow : Window
{
    public FloatingVideoWindow()
    {
        InitializeComponent();
        
        PointerPressed += OnPointerPressed;
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
        }
    }

    // Resize Fields
    private bool _isResizing;
    private ResizeDirection _resizeDirection;
    private Point _resizeStartPoint; // Screen Coordinates
    
    // Start State
    private PixelPoint _startPosition;
    private Size _startSize; // Logical

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
            BeginMoveDrag(e);
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
                
                e.Handled = true;
                
                InvalidateMeasure();
                InvalidateArrange();
            }
            catch (Exception)
            {
                // Suppress runtime resize errors
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
