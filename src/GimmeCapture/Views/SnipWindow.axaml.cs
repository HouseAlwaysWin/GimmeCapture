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
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_viewModel == null) return;

        _startPoint = e.GetPosition(this);
        _viewModel.IsSelecting = true;
        _viewModel.SelectionRect = new Rect(_startPoint, new Size(0, 0));
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_viewModel == null || !_viewModel.IsSelecting) return;

        var currentPoint = e.GetPosition(this);
        
        // Calculate rect regardless of drag direction
        var x = Math.Min(_startPoint.X, currentPoint.X);
        var y = Math.Min(_startPoint.Y, currentPoint.Y);
        var width = Math.Abs(currentPoint.X - _startPoint.X);
        var height = Math.Abs(currentPoint.Y - _startPoint.Y);

        _viewModel.SelectionRect = new Rect(x, y, width, height);
    }

    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_viewModel == null) return;
        
        // Stop updating, but keep the rect visible (or close and capture)
        // For now, let's just stop selecting.
        _viewModel.IsSelecting = false; // Or keep it true to show border? 
        // If IsSelecting is false, the border hides (based on binding).
        // Let's change IsSelecting to IsDragActive or use another property "HasSelection".
        
        // The plan says "PointerReleased -> 完成選區，擷取螢幕"
        Close();
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
        }
    }
}
