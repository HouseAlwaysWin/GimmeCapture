using Avalonia;
using Avalonia.Media;
using ReactiveUI;

namespace GimmeCapture.Models;

public enum AnnotationType
{
    Rectangle,
    Ellipse,
    Arrow,
    Line,
    Text
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
}
