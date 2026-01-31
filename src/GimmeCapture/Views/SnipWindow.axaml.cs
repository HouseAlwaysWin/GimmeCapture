using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using GimmeCapture.ViewModels;
using System;

namespace GimmeCapture.Views;

public partial class SnipWindow : Window
{
    private Point _startPoint;
    private SnipWindowViewModel? _viewModel;

    public SnipWindow()
    {
        InitializeComponent();
        
        // Listen to pointer events on the window or canvas
        // Since the window covers the screen, window events are fine.
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
                // Must run on UI thread
                Close();
            };
            
            _viewModel.HideAction = () =>
            {
                // To remove border but keep window active logically, Hide() might close it?
                // Window.Hide() hides it.
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
        }
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_viewModel == null) return;

        var point = e.GetPosition(this);
        var props = e.GetCurrentPoint(this).Properties;

        if (props.IsLeftButtonPressed)
        {
            if (_viewModel.CurrentState == SnipWindowViewModel.SnipState.Idle || 
                _viewModel.CurrentState == SnipWindowViewModel.SnipState.Detecting)
            {
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
        
        if (_viewModel.CurrentState == SnipWindowViewModel.SnipState.Selecting)
        {
             _viewModel.CurrentState = SnipWindowViewModel.SnipState.Selected;
             // Don't close yet, wait for Toolbar action
        }
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
        }
    }
}
