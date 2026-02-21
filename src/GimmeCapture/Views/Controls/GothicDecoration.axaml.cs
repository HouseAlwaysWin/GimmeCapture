using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using System;

namespace GimmeCapture.Views.Controls
{
    public partial class GothicDecoration : UserControl
    {
        public static readonly StyledProperty<IBrush> DecorationBrushProperty =
            AvaloniaProperty.Register<GothicDecoration, IBrush>(nameof(DecorationBrush), Brushes.Red);

        public static readonly StyledProperty<double> SelectionIconSizeProperty =
            AvaloniaProperty.Register<GothicDecoration, double>(nameof(SelectionIconSize), 22.0);

        public static readonly StyledProperty<double> WingWidthProperty =
            AvaloniaProperty.Register<GothicDecoration, double>(nameof(WingWidth), 100.0);

        public static readonly StyledProperty<double> WingHeightProperty =
            AvaloniaProperty.Register<GothicDecoration, double>(nameof(WingHeight), 60.0);

        public static readonly StyledProperty<Thickness> LeftWingMarginProperty =
            AvaloniaProperty.Register<GothicDecoration, Thickness>(nameof(LeftWingMargin), new Thickness(-100, 0, 0, 0));

        public static readonly StyledProperty<Thickness> RightWingMarginProperty =
            AvaloniaProperty.Register<GothicDecoration, Thickness>(nameof(RightWingMargin), new Thickness(0, 0, -100, 0));

        public static readonly StyledProperty<bool> ShowWingsProperty =
            AvaloniaProperty.Register<GothicDecoration, bool>(nameof(ShowWings), true);

        public IBrush DecorationBrush
        {
            get => GetValue(DecorationBrushProperty);
            set => SetValue(DecorationBrushProperty, value);
        }

        public static readonly StyledProperty<bool> ShowCornersProperty =
            AvaloniaProperty.Register<GothicDecoration, bool>(nameof(ShowCorners), true);

        public static readonly StyledProperty<bool> ShowBorderProperty =
            AvaloniaProperty.Register<GothicDecoration, bool>(nameof(ShowBorder), true);

        public static readonly StyledProperty<double> SelectionThicknessProperty =
            AvaloniaProperty.Register<GothicDecoration, double>(nameof(SelectionThickness), 1.0);

        public double SelectionThickness
        {
            get => GetValue(SelectionThicknessProperty);
            set => SetValue(SelectionThicknessProperty, value);
        }

        public bool ShowWings
        {
            get => GetValue(ShowWingsProperty);
            set => SetValue(ShowWingsProperty, value);
        }

        public bool ShowCorners
        {
            get => GetValue(ShowCornersProperty);
            set => SetValue(ShowCornersProperty, value);
        }

        public bool ShowBorder
        {
            get => GetValue(ShowBorderProperty);
            set => SetValue(ShowBorderProperty, value);
        }

        public double SelectionIconSize
        {
            get => GetValue(SelectionIconSizeProperty);
            set => SetValue(SelectionIconSizeProperty, value);
        }

        public double WingWidth
        {
            get => GetValue(WingWidthProperty);
            set => SetValue(WingWidthProperty, value);
        }

        public double WingHeight
        {
            get => GetValue(WingHeightProperty);
            set => SetValue(WingHeightProperty, value);
        }

        public Thickness LeftWingMargin
        {
            get => GetValue(LeftWingMarginProperty);
            set => SetValue(LeftWingMarginProperty, value);
        }

        public Thickness RightWingMargin
        {
            get => GetValue(RightWingMarginProperty);
            set => SetValue(RightWingMarginProperty, value);
        }

        public GothicDecoration()
        {
            InitializeComponent();
        }
    }
}
