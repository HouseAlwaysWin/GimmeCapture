using Avalonia;
using Avalonia.Controls;
using GimmeCapture.Models;

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

    public AnnotationControl()
    {
        InitializeComponent();
    }
}
