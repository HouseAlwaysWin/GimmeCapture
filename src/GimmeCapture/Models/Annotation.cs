using Avalonia;
using Avalonia.Media;
using ReactiveUI;
using System.Collections.ObjectModel;

namespace GimmeCapture.Models;

public enum AnnotationType
{
    None,       // No tool selected
    Rectangle,
    Ellipse,
    Arrow,
    Line,
    Text,
    Pen,
    Mosaic,
    Blur
}

public class Annotation : ReactiveObject
{
    private AnnotationType _type;
    public AnnotationType Type
    {
        get => _type;
        set => this.RaiseAndSetIfChanged(ref _type, value);
    }

    private Point _startPoint;
    public Point StartPoint
    {
        get => _startPoint;
        set => this.RaiseAndSetIfChanged(ref _startPoint, value);
    }

    private Point _endPoint;
    public Point EndPoint
    {
        get => _endPoint;
        set => this.RaiseAndSetIfChanged(ref _endPoint, value);
    }

    private Color _color;
    public Color Color
    {
        get => _color;
        set => this.RaiseAndSetIfChanged(ref _color, value);
    }

    private double _thickness;
    public double Thickness
    {
        get => _thickness;
        set => this.RaiseAndSetIfChanged(ref _thickness, value);
    }

    private string _text = string.Empty;
    public string Text
    {
        get => _text;
        set => this.RaiseAndSetIfChanged(ref _text, value);
    }

    private double _fontSize;
    public double FontSize
    {
        get => _fontSize;
        set => this.RaiseAndSetIfChanged(ref _fontSize, value);
    }

    private FontFamily _fontFamily = new FontFamily("Arial");
    public FontFamily FontFamily
    {
        get => _fontFamily;
        set => this.RaiseAndSetIfChanged(ref _fontFamily, value);
    }

    private bool _isBold;
    public bool IsBold
    {
        get => _isBold;
        set => this.RaiseAndSetIfChanged(ref _isBold, value);
    }

    private bool _isItalic;
    public bool IsItalic
    {
        get => _isItalic;
        set => this.RaiseAndSetIfChanged(ref _isItalic, value);
    }

    private Avalonia.Media.Imaging.Bitmap? _drawingModeSnapshot;
    public Avalonia.Media.Imaging.Bitmap? DrawingModeSnapshot
    {
        get => _drawingModeSnapshot;
        set => this.RaiseAndSetIfChanged(ref _drawingModeSnapshot, value);
    }

    public Avalonia.Points Points { get; } = new();

    public void AddPoint(Point p)
    {
        Points.Add(p);
        this.RaisePropertyChanged(nameof(Points));
    }
}
