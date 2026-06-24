using Avalonia;
using Avalonia.Media;
using ReactiveUI;
using System.Collections.ObjectModel;
using System.Text.Json.Serialization;

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
    Blur,
    Highlighter,
    Step,
    Callout     // Text label with a leader line pointing at the annotated target
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

    private Size _drawingModeReferenceSize;
    public Size DrawingModeReferenceSize
    {
        get => _drawingModeReferenceSize;
        set => this.RaiseAndSetIfChanged(ref _drawingModeReferenceSize, value);
    }

    private AnnotationEffectSettings _effectSettings = new();
    public AnnotationEffectSettings EffectSettings
    {
        get => _effectSettings;
        set => this.RaiseAndSetIfChanged(ref _effectSettings, value);
    }

    private bool _isFilled;
    public bool IsFilled
    {
        get => _isFilled;
        set => this.RaiseAndSetIfChanged(ref _isFilled, value);
    }

    private int _stepNumber;
    public int StepNumber
    {
        get => _stepNumber;
        set => this.RaiseAndSetIfChanged(ref _stepNumber, value);
    }

    private bool _isSelected;

    [JsonIgnore]
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value) return;
            this.RaiseAndSetIfChanged(ref _isSelected, value);
            this.RaisePropertyChanged(nameof(ShowsAreaSelection));
            this.RaisePropertyChanged(nameof(ShowsLineSelection));
        }
    }

    [JsonIgnore]
    public bool ShowsAreaSelection =>
        IsSelected && Type is AnnotationType.Rectangle or AnnotationType.Ellipse or AnnotationType.Mosaic or AnnotationType.Blur or AnnotationType.Highlighter or AnnotationType.Step;

    [JsonIgnore]
    public bool ShowsLineSelection =>
        IsSelected && Type is AnnotationType.Line or AnnotationType.Arrow or AnnotationType.Callout;

    public Avalonia.Points Points { get; } = new();

    public void AddPoint(Point p)
    {
        Points.Add(p);
        this.RaisePropertyChanged(nameof(Points));
    }

    public Annotation Clone()
    {
        var clone = new Annotation
        {
            Type = this.Type,
            StartPoint = this.StartPoint,
            EndPoint = this.EndPoint,
            Color = this.Color,
            Thickness = this.Thickness,
            Text = this.Text,
            FontSize = this.FontSize,
            FontFamily = this.FontFamily,
            IsBold = this.IsBold,
            IsItalic = this.IsItalic,
            DrawingModeSnapshot = this.DrawingModeSnapshot,
            DrawingModeReferenceSize = this.DrawingModeReferenceSize,
            EffectSettings = this.EffectSettings.Clone(),
            IsFilled = this.IsFilled,
            StepNumber = this.StepNumber
        };
        foreach (var p in this.Points)
        {
            clone.Points.Add(p);
        }
        return clone;
    }
}
