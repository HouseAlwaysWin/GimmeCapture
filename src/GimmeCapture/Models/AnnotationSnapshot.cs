using Avalonia;
using Avalonia.Media;

namespace GimmeCapture.Models;

public sealed class AnnotationSnapshot
{
    public Point StartPoint { get; init; }
    public Point EndPoint { get; init; }
    public Color Color { get; init; }
    public double Thickness { get; init; }
    public string Text { get; init; } = string.Empty;
    public double FontSize { get; init; }
    public FontFamily FontFamily { get; init; } = new("Arial");
    public bool IsBold { get; init; }
    public bool IsItalic { get; init; }
    public AnnotationEffectSettings EffectSettings { get; init; } = new();

    public static AnnotationSnapshot Capture(Annotation annotation)
    {
        return new AnnotationSnapshot
        {
            StartPoint = annotation.StartPoint,
            EndPoint = annotation.EndPoint,
            Color = annotation.Color,
            Thickness = annotation.Thickness,
            Text = annotation.Text,
            FontSize = annotation.FontSize,
            FontFamily = annotation.FontFamily,
            IsBold = annotation.IsBold,
            IsItalic = annotation.IsItalic,
            EffectSettings = annotation.EffectSettings.Clone()
        };
    }

    public void ApplyTo(Annotation annotation)
    {
        annotation.StartPoint = StartPoint;
        annotation.EndPoint = EndPoint;
        annotation.Color = Color;
        annotation.Thickness = Thickness;
        annotation.Text = Text;
        annotation.FontSize = FontSize;
        annotation.FontFamily = FontFamily;
        annotation.IsBold = IsBold;
        annotation.IsItalic = IsItalic;
        annotation.EffectSettings = EffectSettings.Clone();
    }

    public bool HasSameValues(AnnotationSnapshot other)
    {
        return StartPoint == other.StartPoint
            && EndPoint == other.EndPoint
            && Color == other.Color
            && Thickness.Equals(other.Thickness)
            && Text == other.Text
            && FontSize.Equals(other.FontSize)
            && FontFamily == other.FontFamily
            && IsBold == other.IsBold
            && IsItalic == other.IsItalic
            && EffectSettings.MosaicCellSize == other.EffectSettings.MosaicCellSize
            && EffectSettings.BlurRadius.Equals(other.EffectSettings.BlurRadius)
            && EffectSettings.Feather.Equals(other.EffectSettings.Feather);
    }
}
