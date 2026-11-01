using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using GimmeCapture.Models;
using GimmeCapture.Services.Core.Rendering;
using ReactiveUI;
using ReactiveUI.Reactive;
using System;
using System.Reactive.Disposables;
using System.Reactive.Linq;

namespace GimmeCapture.Views.Controls;

public partial class AnnotationControl : UserControl
{
    public static readonly StyledProperty<Annotation?> AnnotationProperty =
        AvaloniaProperty.Register<AnnotationControl, Annotation?>(nameof(Annotation));

    public Annotation? Annotation
    {
        get => GetValue(AnnotationProperty);
        set => SetValue(AnnotationProperty, value);
    }

    /// <summary>
    /// Unified effect preview bitmap generated from the shared annotation renderer.
    /// </summary>
    public static readonly StyledProperty<WriteableBitmap?> EffectPreviewBitmapProperty =
        AvaloniaProperty.Register<AnnotationControl, WriteableBitmap?>(nameof(EffectPreviewBitmap));

    public WriteableBitmap? EffectPreviewBitmap
    {
        get => GetValue(EffectPreviewBitmapProperty);
        set => SetValue(EffectPreviewBitmapProperty, value);
    }

    private IDisposable? _effectSubscription;

    public AnnotationControl()
    {
        InitializeComponent();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        _effectSubscription?.Dispose();

        if (DataContext is Annotation ann && (ann.Type == AnnotationType.Mosaic || ann.Type == AnnotationType.Blur))
        {
            _effectSubscription = Observable.CombineLatest(
                ann.WhenAnyValue(a => a.StartPoint),
                ann.WhenAnyValue(a => a.EndPoint),
                ann.WhenAnyValue(a => a.DrawingModeSnapshot),
                ann.WhenAnyValue(a => a.DrawingModeReferenceSize),
                // Effect settings are replaced with a new object, never changed in place, so the replacement is the
                // change to follow (the settings type raises no notifications of its own).
                ann.WhenAnyValue(a => a.EffectSettings),
                this.GetObservable(BoundsProperty),
                (start, end, snapshot, drawnIn, settings, bounds) =>
                    (start, end, snapshot, PreviewReferenceSize(drawnIn, bounds.Size, picture: default), settings))
                // Only what the preview is drawn from: the control's size counts for an annotation that has no size of
                // its own, so resizing a pin, which resizes every annotation on it, redraws none. The first is drawn
                // below, at once.
                .DistinctUntilChanged()
                .Skip(1)
                .Throttle(TimeSpan.FromMilliseconds(16))
                .ObserveOn(RxSchedulers.MainThreadScheduler)
                .Subscribe(_ => RegenerateEffectPreview(ann));

            RegenerateEffectPreview(ann);
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _effectSubscription?.Dispose();
        _effectSubscription = null;

        var old = EffectPreviewBitmap;
        EffectPreviewBitmap = null;
        old?.Dispose();
    }

    private void RegenerateEffectPreview(Annotation annotation)
    {
        var snapshot = annotation.DrawingModeSnapshot;
        if (snapshot == null)
            return;

        WriteableBitmap? result;
        try
        {
            Size reference = PreviewReferenceSize(annotation.DrawingModeReferenceSize, Bounds.Size, snapshot.PixelSize);
            result = AnnotationRenderService.Shared.RenderAnnotationPreview(snapshot, annotation, reference.Width, reference.Height);
        }
        catch (ObjectDisposedException)
        {
            // The picture went before the annotation did, as a pin's oldest image leaves its undo history: the preview
            // stays as last drawn. It used to come from a copy the control kept of the whole picture.
            return;
        }

        var old = EffectPreviewBitmap;
        EffectPreviewBitmap = result;
        old?.Dispose();
    }

    // The size the annotation's points are in: the content's when it was drawn, failing that the control's, failing
    // that the picture's.
    internal static Size PreviewReferenceSize(Size drawnIn, Size bounds, PixelSize picture)
    {
        double width = IsSize(drawnIn.Width) ? drawnIn.Width : IsSize(bounds.Width) ? bounds.Width : picture.Width;
        double height = IsSize(drawnIn.Height) ? drawnIn.Height : IsSize(bounds.Height) ? bounds.Height : picture.Height;
        return new Size(width, height);

        static bool IsSize(double value) => !double.IsNaN(value) && value > 0;
    }
}
