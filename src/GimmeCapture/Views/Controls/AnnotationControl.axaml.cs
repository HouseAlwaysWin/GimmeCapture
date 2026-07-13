using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using GimmeCapture.Models;
using GimmeCapture.Services.Core.Rendering;
using ReactiveUI;
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
    // Cached Avalonia→Skia conversion of the drag snapshot, reused across throttled ticks (see RegenerateEffectPreview).
    private SkiaSharp.SKBitmap? _cachedPreviewSource;
    private Bitmap? _cachedPreviewSourceKey;

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
                ann.WhenAnyValue(a => a.EffectSettings.MosaicCellSize),
                ann.WhenAnyValue(a => a.EffectSettings.BlurRadius),
                ann.WhenAnyValue(a => a.EffectSettings.Feather),
                this.GetObservable(BoundsProperty),
                (start, end, snapshot, referenceSize, _, _, _, bounds) => (start, end, snapshot, referenceSize, bounds))
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

        _cachedPreviewSource?.Dispose();
        _cachedPreviewSource = null;
        _cachedPreviewSourceKey = null;
    }

    private void RegenerateEffectPreview(Annotation annotation)
    {
        var snapshot = annotation.DrawingModeSnapshot;
        if (snapshot == null)
            return;

        double referenceWidth = annotation.DrawingModeReferenceSize.Width;
        double referenceHeight = annotation.DrawingModeReferenceSize.Height;
        if (double.IsNaN(referenceWidth) || referenceWidth <= 0)
            referenceWidth = Bounds.Width;
        if (double.IsNaN(referenceHeight) || referenceHeight <= 0)
            referenceHeight = Bounds.Height;
        if (double.IsNaN(referenceWidth) || referenceWidth <= 0)
            referenceWidth = snapshot.PixelSize.Width;
        if (double.IsNaN(referenceHeight) || referenceHeight <= 0)
            referenceHeight = snapshot.PixelSize.Height;

        var result = AnnotationRenderService.Shared.RenderAnnotationPreview(
            snapshot, annotation, referenceWidth, referenceHeight,
            ref _cachedPreviewSource, ref _cachedPreviewSourceKey);
        var old = EffectPreviewBitmap;
        EffectPreviewBitmap = result;
        old?.Dispose();
    }
}
