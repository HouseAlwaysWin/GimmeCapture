using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using SkiaSharp;

namespace GimmeCapture.Views.Controls;

/// <summary>
/// Draws the glow around a frame, the box shadow <c>ColorToBoxShadowConverter</c> makes (the colour at half opacity,
/// blurred 10 and spread 1), as <see cref="Border"/> draws its <c>BoxShadow</c>, but with Skia's mask blur. A border's
/// box shadow blurs a layer as large as the border with an image filter every time it is drawn: drawn on the CPU ("Use
/// less memory"), that and the checkerboard under the image (<see cref="TransparencyCheckerboard"/>) held a 1280x720 pin
/// being resized to 19-20 frames a second, 29-46 once both were drawn this way. A mask blur works a blurred rectangle
/// out along its edges, and comes within a few levels of the box shadow (<c>GlowDecoratorRenderTests</c>). Border seals
/// its Render, so this decorator draws the glow around the border it holds, which lays itself out to fill it. The rest
/// falls back to Avalonia's own box shadow: rounded corners, inset shadows, and drawing without Skia.
/// </summary>
public class GlowDecorator : Decorator
{
    public static readonly StyledProperty<BoxShadows> GlowProperty =
        AvaloniaProperty.Register<GlowDecorator, BoxShadows>(nameof(Glow));

    static GlowDecorator()
    {
        AffectsRender<GlowDecorator>(GlowProperty);
    }

    /// <summary>The glow, a <c>BoxShadow</c> value, drawn around the child.</summary>
    public BoxShadows Glow
    {
        get => GetValue(GlowProperty);
        set => SetValue(GlowProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        var glow = Glow;
        if (glow.Count == 0 || Child is not { Bounds: { Width: > 0, Height: > 0 } } child)
        {
            return;
        }

        var rect = ShadowRect(child, out var cornerRadius);
        foreach (var shadow in glow)
        {
            // "0 0 0 0 Transparent", which the styles set to hide it, and any other that would leave nothing to see.
            if (shadow.Color.A != 0 && (shadow.Blur > 0 || shadow.Spread != 0 || shadow.OffsetX != 0 || shadow.OffsetY != 0))
            {
                context.Custom(new GlowOperation(rect, cornerRadius, shadow));
            }
        }
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ChildProperty)
        {
            if (change.OldValue is Border oldBorder)
            {
                oldBorder.PropertyChanged -= OnBorderPropertyChanged;
            }

            if (change.NewValue is Border newBorder)
            {
                newBorder.PropertyChanged += OnBorderPropertyChanged;
            }
        }
    }

    // The shadow follows the border's thickness and corners, which change nothing else of this decorator.
    private void OnBorderPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == Border.BorderThicknessProperty || e.Property == Border.CornerRadiusProperty)
        {
            InvalidateVisual();
        }
    }

    // The rect a border casts its shadow from: its own, less half a uniform border, where the border's pen runs.
    private static Rect ShadowRect(Control child, out CornerRadius cornerRadius)
    {
        var rect = child.Bounds;
        cornerRadius = default;
        if (child is not Border border)
        {
            return rect;
        }

        cornerRadius = border.CornerRadius;
        var thickness = border.BorderThickness;
        if (border.UseLayoutRounding)
        {
            thickness = LayoutHelper.RoundLayoutThickness(thickness, LayoutHelper.GetLayoutScale(border));
        }

        return thickness.IsUniform && thickness.Top > 0 ? rect.Deflate(thickness.Top * 0.5) : rect;
    }

    private sealed class GlowOperation : ICustomDrawOperation
    {
        private readonly Rect _rect;
        private readonly CornerRadius _cornerRadius;
        private readonly BoxShadow _shadow;

        public GlowOperation(Rect rect, CornerRadius cornerRadius, BoxShadow shadow)
        {
            _rect = rect;
            _cornerRadius = cornerRadius;
            _shadow = shadow;
        }

        // A Gaussian's tail ends within three sigmas; one pixel more for the blur's rounding.
        public Rect Bounds =>
            _rect.Inflate(_shadow.Spread + (3 * BlurSigma(_shadow.Blur)) + 1)
                .Translate(new Vector(_shadow.OffsetX, _shadow.OffsetY))
                .Union(_rect);

        public bool HitTest(Point p) => false;

        public bool Equals(ICustomDrawOperation? other) =>
            other is GlowOperation glow && glow._rect == _rect && glow._cornerRadius == _cornerRadius && glow._shadow == _shadow;

        public void Dispose()
        {
        }

        public void Render(ImmediateDrawingContext context)
        {
            if (_shadow.IsInset
                || _cornerRadius != default
                || context.TryGetFeature<ISkiaSharpApiLeaseFeature>() is not { } skia)
            {
                context.DrawRectangle(
                    null, null, _rect, _cornerRadius.TopLeft, _cornerRadius.TopLeft, new BoxShadows(_shadow));
                return;
            }

            using var lease = skia.Lease();
            var canvas = lease.SkCanvas;
            var color = _shadow.Color;
            float sigma = BlurSigma(_shadow.Blur);
            using var blur = sigma > 0 ? SKMaskFilter.CreateBlur(SKBlurStyle.Normal, sigma) : null;
            using var paint = new SKPaint
            {
                IsAntialias = true,
                Color = new SKColor(color.R, color.G, color.B, (byte)(color.A * lease.CurrentOpacity)),
                MaskFilter = blur,
            };

            // As Avalonia draws an outer box shadow: clipped out of the rect, then spread and moved by its offset.
            var rect = new SKRect((float)_rect.X, (float)_rect.Y, (float)_rect.Right, (float)_rect.Bottom);
            var shadowRect = rect;
            shadowRect.Inflate((float)_shadow.Spread, (float)_shadow.Spread);
            int saved = canvas.Save();
            canvas.ClipRect(rect, SKClipOperation.Difference);
            canvas.Translate((float)_shadow.OffsetX, (float)_shadow.OffsetY);
            canvas.DrawRect(shadowRect, paint);
            canvas.RestoreToCount(saved);
        }

        // Avalonia's own radius-to-sigma for box shadows (SkBlurRadiusToSigma in Avalonia.Skia).
        private static float BlurSigma(double blur) => blur > 0 ? (0.288675f * (float)blur) + 0.5f : 0f;
    }
}
