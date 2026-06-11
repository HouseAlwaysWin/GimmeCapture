using System;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using GimmeCapture.Models;
using GimmeCapture.Services.Core;

namespace GimmeCapture.Views.Controls;

public sealed class AnnotationSelectionAdorner : Control
{
    public static readonly StyledProperty<Annotation?> AnnotationProperty =
        AvaloniaProperty.Register<AnnotationSelectionAdorner, Annotation?>(nameof(Annotation));

    private static readonly IBrush AccentBrush = new SolidColorBrush(Color.FromRgb(0, 210, 255));
    private static readonly IBrush HandleFillBrush = Brushes.White;
    private readonly Pen _outlinePen = new(AccentBrush, 1, new DashStyle([4, 3], 0));
    private readonly Pen _handlePen = new(AccentBrush, 1.5);
    private Annotation? _subscribedAnnotation;

    static AnnotationSelectionAdorner()
    {
        AnnotationProperty.Changed.AddClassHandler<AnnotationSelectionAdorner>(
            static (control, args) => control.OnAnnotationChanged(args));
    }

    public Annotation? Annotation
    {
        get => GetValue(AnnotationProperty);
        set => SetValue(AnnotationProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var annotation = Annotation;
        if (annotation == null || !annotation.IsSelected)
        {
            return;
        }

        if (AnnotationInteractionService.IsArea(annotation.Type))
        {
            DrawAreaSelection(context, AnnotationInteractionService.GetNormalizedBounds(annotation));
        }
        else if (AnnotationInteractionService.IsLine(annotation.Type))
        {
            context.DrawLine(_outlinePen, annotation.StartPoint, annotation.EndPoint);
            DrawRoundHandle(context, annotation.StartPoint);
            DrawRoundHandle(context, annotation.EndPoint);
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        SubscribeToAnnotation(null);
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        SubscribeToAnnotation(Annotation);
        InvalidateVisual();
    }

    private void OnAnnotationChanged(AvaloniaPropertyChangedEventArgs args)
    {
        SubscribeToAnnotation(args.NewValue as Annotation);
        InvalidateVisual();
    }

    private void SubscribeToAnnotation(Annotation? annotation)
    {
        if (_subscribedAnnotation != null)
        {
            _subscribedAnnotation.PropertyChanged -= OnAnnotationPropertyChanged;
        }

        _subscribedAnnotation = annotation;
        if (_subscribedAnnotation != null)
        {
            _subscribedAnnotation.PropertyChanged += OnAnnotationPropertyChanged;
        }
    }

    private void OnAnnotationPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(Annotation.StartPoint)
            or nameof(Annotation.EndPoint)
            or nameof(Annotation.IsSelected)
            or nameof(Annotation.Type))
        {
            InvalidateVisual();
        }
    }

    private void DrawAreaSelection(DrawingContext context, Rect bounds)
    {
        context.DrawRectangle(null, _outlinePen, bounds);
        var midX = bounds.X + bounds.Width / 2;
        var midY = bounds.Y + bounds.Height / 2;
        DrawSquareHandle(context, bounds.TopLeft);
        DrawSquareHandle(context, new Point(midX, bounds.Top));
        DrawSquareHandle(context, bounds.TopRight);
        DrawSquareHandle(context, new Point(bounds.Right, midY));
        DrawSquareHandle(context, bounds.BottomRight);
        DrawSquareHandle(context, new Point(midX, bounds.Bottom));
        DrawSquareHandle(context, bounds.BottomLeft);
        DrawSquareHandle(context, new Point(bounds.Left, midY));
    }

    private void DrawSquareHandle(DrawingContext context, Point center)
    {
        const double size = AnnotationInteractionService.HandleRadius * 2;
        context.DrawRectangle(
            HandleFillBrush,
            _handlePen,
            new Rect(center.X - size / 2, center.Y - size / 2, size, size));
    }

    private void DrawRoundHandle(DrawingContext context, Point center)
    {
        context.DrawEllipse(
            HandleFillBrush,
            _handlePen,
            center,
            AnnotationInteractionService.HandleRadius,
            AnnotationInteractionService.HandleRadius);
    }
}
