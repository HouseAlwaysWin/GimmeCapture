using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using GimmeCapture.ViewModels;
using System;

namespace GimmeCapture.Views;

public partial class SnipWindow : Window
{
    private Point _startPoint;
    private SnipWindowViewModel? _viewModel;
    
    // Resize State
    private bool _isResizing;
    private ResizeDirection _resizeDirection;
    private Point _resizeStartPoint;
    private Rect _originalRect;

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
        
        // Close on Escape
        KeyDown += OnKeyDown;
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        _viewModel = DataContext as SnipWindowViewModel;
        if (_viewModel != null)
        {
            _viewModel.CloseAction = () => 
            {
                Close();
            };
            
            _viewModel.HideAction = () =>
            {
                Hide();
            };
            

            _viewModel.PickSaveFileAction = async () =>
            {
                 var topLevel = TopLevel.GetTopLevel(this);
                 if (topLevel == null) return null;
                 
                 var file = await topLevel.StorageProvider.SaveFilePickerAsync(new Avalonia.Platform.Storage.FilePickerSaveOptions
                 {
                     Title = "Save Screenshot",
                     DefaultExtension = "png",
                     ShowOverwritePrompt = true,
                     SuggestedFileName = $"Capture_{DateTime.Now:yyyyMMdd_HHmmss}",
                     FileTypeChoices = new[]
                     {
                         new Avalonia.Platform.Storage.FilePickerFileType("PNG Image") { Patterns = new[] { "*.png" } }
                     }
                 });
                 
                 return file?.Path.LocalPath;
            };

            _viewModel.OpenPinWindowAction = (bitmap, rect) =>
            {
                // Create ViewModel
                var vm = new FloatingImageViewModel(bitmap);
                
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

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_viewModel == null) return;

        var point = e.GetPosition(this);
        var props = e.GetCurrentPoint(this).Properties;
        var source = e.Source as Control;

        // Check if we clicked on a handle
        // Using Control instead of Ellipse because we changed XAML to use Grid (Panel)
        if (props.IsLeftButtonPressed && source is Control handle && handle.Classes.Contains("Handle"))
        {
            _isResizing = true;
            _resizeDirection = GetDirectionFromName(handle.Name);
            _resizeStartPoint = point;
            _originalRect = _viewModel.SelectionRect;
            e.Handled = true;
            return;
        }

        if (props.IsLeftButtonPressed)
        {
            if (_viewModel.CurrentState == SnipWindowViewModel.SnipState.Idle || 
                _viewModel.CurrentState == SnipWindowViewModel.SnipState.Detecting ||
                _viewModel.CurrentState == SnipWindowViewModel.SnipState.Selected) // Allow re-selection if clicking outside? Or just start new?
            {
                // If we are already selected but clicked outside handle, start new selection?
                // Or maybe just clear? Let's allow new selection.
                
                _startPoint = point;
                _viewModel.CurrentState = SnipWindowViewModel.SnipState.Selecting;
                _viewModel.SelectionRect = new Rect(_startPoint, new Size(0, 0));
            }
        }
        else if (props.IsRightButtonPressed)
        {
            if (_viewModel.CurrentState == SnipWindowViewModel.SnipState.Selecting || 
                _viewModel.CurrentState == SnipWindowViewModel.SnipState.Selected)
            {
                // Reset to Idle
                _viewModel.CurrentState = SnipWindowViewModel.SnipState.Idle;
                _viewModel.SelectionRect = new Rect(0,0,0,0);
            }
            else
            {
                 Close();
            }
        }
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_viewModel == null) return;

        var currentPoint = e.GetPosition(this);

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

        if (_viewModel.CurrentState == SnipWindowViewModel.SnipState.Selecting)
        {
            var x = Math.Min(_startPoint.X, currentPoint.X);
            var y = Math.Min(_startPoint.Y, currentPoint.Y);
            var width = Math.Abs(currentPoint.X - _startPoint.X);
            var height = Math.Abs(currentPoint.Y - _startPoint.Y);

            _viewModel.SelectionRect = new Rect(x, y, width, height);
        }
        else if (_viewModel.CurrentState == SnipWindowViewModel.SnipState.Idle)
        {
            // TODO: Window Auto-detection logic here later
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
             _viewModel.CurrentState = SnipWindowViewModel.SnipState.Selected;
             return;
        }
        
        if (_viewModel.CurrentState == SnipWindowViewModel.SnipState.Selecting)
        {
             _viewModel.CurrentState = SnipWindowViewModel.SnipState.Selected;
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
