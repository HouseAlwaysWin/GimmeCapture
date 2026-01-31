using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using GimmeCapture.ViewModels;
using GimmeCapture.Models;
using System;

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
        
        // Text Input Events
        var textBox = this.FindControl<TextBox>("TextInputOverlay");
        if (textBox != null)
        {
            textBox.KeyDown += (s, e) =>
            {
                if (e.Key == Key.Enter && !e.KeyModifiers.HasFlag(KeyModifiers.Shift))
                {
                    FinishTextEntry();
                    e.Handled = true;
                }
                else if (e.Key == Key.Escape)
                {
                    CancelTextEntry();
                    e.Handled = true;
                }
            };
            textBox.LostFocus += (s, e) => FinishTextEntry();
        }
        
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
            if (_viewModel.IsDrawingMode && _viewModel.CurrentState == SnipWindowViewModel.SnipState.Selected)
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
                    
                    _viewModel.Annotations.Add(_currentAnnotation);
                    e.Handled = true;
                    return;
                }
            }

            // If clicking OUTSIDE or in Idle/Detecting, start NEW selection
            if (_viewModel.CurrentState == SnipWindowViewModel.SnipState.Idle || 
                _viewModel.CurrentState == SnipWindowViewModel.SnipState.Detecting ||
                !_viewModel.SelectionRect.Contains(point))
            {
                _startPoint = point;
                _viewModel.CurrentState = SnipWindowViewModel.SnipState.Selecting;
                _viewModel.SelectionRect = new Rect(_startPoint, new Size(0, 0));
                _viewModel.IsDrawingMode = false; // Exit drawing mode when starting new selection
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
        else if (_viewModel.CurrentState == SnipWindowViewModel.SnipState.Selected && _currentAnnotation != null)
        {
            // Update Drawing
            var relPoint = new Point(currentPoint.X - _viewModel.SelectionRect.X, currentPoint.Y - _viewModel.SelectionRect.Y);
            _currentAnnotation.EndPoint = relPoint;
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

        if (_currentAnnotation != null)
        {
            _currentAnnotation = null; // Drawing finished
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
                FontSize = _viewModel.CurrentFontSize
            });
        }
        
        CancelTextEntry();
    }

    private void CancelTextEntry()
    {
        if (_viewModel == null) return;
        _viewModel.IsEnteringText = false;
        _viewModel.PendingText = string.Empty;
        // Shift focus back to window to allow hotkeys etc.
        this.Focus();
    }
}
