using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace GimmeCapture.Views.Controls;

/// <summary>
/// The snip overlay's candidate boxes: the text the OCR scan found, the windows it can snap to, the lines Translate mode
/// searches. Each is outlined inside its bounds in one colour at one opacity. Each box was a Border in an ItemsControl,
/// with a content presenter, four bindings, one of them through a converter that made a brush for it, an opacity layer,
/// and a pointer-over style of its own that nothing could trigger. Adding a scan's 100 text boxes and 20 windows took
/// 26-33 ms on the UI thread and their layout 46-57 more, 78-92 ms to the first frame, just as the scan came in. This
/// draws them all in one Render with one pen, where layout rounding put each Border: its corner at the nearest device
/// pixel, its size rounded up, its stroke as thick as the Border's rounded thickness. CandidateBoxesLayerRenderTests
/// holds the two against each other.
/// </summary>
public sealed class CandidateBoxesLayer : Control
{
    public static readonly StyledProperty<IReadOnlyList<Rect>?> BoxesProperty =
        AvaloniaProperty.Register<CandidateBoxesLayer, IReadOnlyList<Rect>?>(nameof(Boxes));

    public static readonly StyledProperty<Color> BoxColorProperty =
        AvaloniaProperty.Register<CandidateBoxesLayer, Color>(nameof(BoxColor));

    public static readonly StyledProperty<double> BoxOpacityProperty =
        AvaloniaProperty.Register<CandidateBoxesLayer, double>(nameof(BoxOpacity), 1.0);

    public static readonly StyledProperty<double> BoxThicknessProperty =
        AvaloniaProperty.Register<CandidateBoxesLayer, double>(nameof(BoxThickness), 1.0);

    static CandidateBoxesLayer()
    {
        AffectsRender<CandidateBoxesLayer>(BoxesProperty, BoxColorProperty, BoxOpacityProperty, BoxThicknessProperty);
    }

    /// <summary>The boxes, in this layer's coordinates. Replaced whole, never changed in place.</summary>
    public IReadOnlyList<Rect>? Boxes
    {
        get => GetValue(BoxesProperty);
        set => SetValue(BoxesProperty, value);
    }

    public Color BoxColor
    {
        get => GetValue(BoxColorProperty);
        set => SetValue(BoxColorProperty, value);
    }

    /// <summary>The outlines' opacity, as a Border's <c>Opacity</c> had it.</summary>
    public double BoxOpacity
    {
        get => GetValue(BoxOpacityProperty);
        set => SetValue(BoxOpacityProperty, value);
    }

    /// <summary>The outline's width, as a Border's <c>BorderThickness</c>.</summary>
    public double BoxThickness
    {
        get => GetValue(BoxThicknessProperty);
        set => SetValue(BoxThicknessProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        if (Boxes is not { Count: > 0 } boxes)
        {
            return;
        }

        double scale = LayoutHelper.GetLayoutScale(this);
        double thickness = StrokeThickness(BoxThickness, scale, UseLayoutRounding);
        var pen = new ImmutablePen(new ImmutableSolidColorBrush(BoxColor, BoxOpacity), thickness);
        foreach (Rect box in boxes)
        {
            context.DrawRectangle(null, pen, StrokeRect(box, scale, thickness, UseLayoutRounding));
        }
    }

    /// <summary>The stroke's width a Border with <paramref name="borderThickness"/> draws at <paramref name="scale"/>.</summary>
    internal static double StrokeThickness(double borderThickness, double scale, bool useLayoutRounding) =>
        useLayoutRounding
            ? LayoutHelper.RoundLayoutThickness(new Thickness(borderThickness), scale).Top
            : borderThickness;

    /// <summary>
    /// Where a Border at <paramref name="box"/>, set on a Canvas by its corner and given its size, runs its stroke of
    /// <paramref name="thickness"/>: the bounds layout gives it, less half the stroke.
    /// </summary>
    internal static Rect StrokeRect(Rect box, double scale, double thickness, bool useLayoutRounding)
    {
        Rect bounds = useLayoutRounding
            ? new Rect(
                new Point(LayoutHelper.RoundLayoutValue(box.X, scale), LayoutHelper.RoundLayoutValue(box.Y, scale)),
                LayoutHelper.RoundLayoutSizeUp(box.Size, scale))
            : box;
        return bounds.Deflate(thickness / 2);
    }
}
