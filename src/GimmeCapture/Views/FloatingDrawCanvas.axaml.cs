using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Media;
using GimmeCapture.Models;
using GimmeCapture.ViewModels;
using System;
using System.Collections.Generic;

namespace GimmeCapture.Views;

/// <summary>
/// A floating, nearly transparent canvas window that overlays the selection area.
/// Allows drawing annotations while keeping the underlying content (YouTube) visible.
/// </summary>
public partial class FloatingDrawCanvas : Window
{
    private SnipWindowViewModel? _viewModel;
    private Point _lastPoint;
    private bool _isDrawing;
    private Polyline? _currentStroke;
    private Canvas? _drawingSurface;
    
    // Keep track of the current annotation being drawn
    private Annotation? _currentAnnotation;

    public FloatingDrawCanvas()
    {
        InitializeComponent();
        
        _drawingSurface = this.FindControl<Canvas>("DrawingSurface");
        
        PointerPressed += OnPointerPressed;
        PointerMoved += OnPointerMoved;
        PointerReleased += OnPointerReleased;
    }

    private void InitializeComponent()
    {
        Avalonia.Markup.Xaml.AvaloniaXamlLoader.Load(this);
    }

    /// <summary>
    /// Initialize with the SnipWindow's ViewModel for annotation sync
    /// </summary>
    public void Initialize(SnipWindowViewModel viewModel)
    {
        _viewModel = viewModel;
    }

    /// <summary>
    /// Update the position and size to match the selection rect
    /// </summary>
    public void UpdateBounds(Rect selectionRect, PixelPoint screenOffset, double scaling)
    {
        // Position the window at the selection rect location (in screen coordinates)
        var x = (int)(selectionRect.X) + screenOffset.X;
        var y = (int)(selectionRect.Y) + screenOffset.Y;
        
        Position = new PixelPoint(x, y);
        Width = selectionRect.Width;
        Height = selectionRect.Height;
    }

    /// <summary>
    /// Clear all drawn shapes from the canvas (not from ViewModel)
    /// </summary>
    public void ClearCanvas()
    {
        _drawingSurface?.Children.Clear();
    }

    /// <summary>
    /// Render annotations from the ViewModel to the canvas
    /// </summary>
    public void RenderAnnotations()
    {
        if (_viewModel == null || _drawingSurface == null) return;
        
        _drawingSurface.Children.Clear();
        
        foreach (var annotation in _viewModel.Annotations)
        {
            if (annotation.Type == AnnotationType.Pen && annotation.Points.Count > 1)
            {
                var polyline = new Polyline
                {
                    Stroke = new SolidColorBrush(annotation.Color),
                    StrokeThickness = annotation.Thickness,
                    StrokeLineCap = PenLineCap.Round,
                    StrokeJoin = PenLineJoin.Round,
                    Points = new Points(annotation.Points)
                };
                _drawingSurface.Children.Add(polyline);
            }
        }
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_viewModel == null || !_viewModel.IsDrawingMode) return;
        
        // Only handle Pen tool for now in floating canvas
        if (_viewModel.CurrentTool != AnnotationType.Pen) return;
        
        var point = e.GetCurrentPoint(this);
        if (point.Properties.IsLeftButtonPressed)
        {
            _isDrawing = true;
            _lastPoint = point.Position;
            
            // Create a new annotation
            _currentAnnotation = new Annotation
            {
                Type = AnnotationType.Pen,
                StartPoint = _lastPoint,
                Color = _viewModel.SelectedColor,
                Thickness = _viewModel.CurrentThickness
            };
            _currentAnnotation.AddPoint(_lastPoint);
            
            // Start a new visual stroke
            _currentStroke = new Polyline
            {
                Stroke = new SolidColorBrush(_viewModel.SelectedColor),
                StrokeThickness = _viewModel.CurrentThickness,
                StrokeLineCap = PenLineCap.Round,
                StrokeJoin = PenLineJoin.Round,
                Points = new Points { _lastPoint }
            };
            
            _drawingSurface?.Children.Add(_currentStroke);
            
            e.Handled = true;
        }
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_isDrawing || _currentStroke == null || _currentAnnotation == null) return;
        
        var point = e.GetCurrentPoint(this);
        var currentPoint = point.Position;
        
        // Add point to visual stroke
        if (_currentStroke.Points is Points points)
        {
            points.Add(currentPoint);
        }
        
        // Add point to annotation model
        _currentAnnotation.AddPoint(currentPoint);
        
        _lastPoint = currentPoint;
        e.Handled = true;
    }

    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_isDrawing || _viewModel == null || _currentAnnotation == null) return;
        
        _isDrawing = false;
        
        // Finalize the annotation
        if (_currentAnnotation.Points.Count > 1)
        {
            _currentAnnotation.EndPoint = _lastPoint;
            _viewModel.Annotations.Add(_currentAnnotation);
        }
        
        _currentAnnotation = null;
        _currentStroke = null;
        e.Handled = true;
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        
        PointerPressed -= OnPointerPressed;
        PointerMoved -= OnPointerMoved;
        PointerReleased -= OnPointerReleased;
    }
}
