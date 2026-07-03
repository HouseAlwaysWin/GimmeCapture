using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GimmeCapture.Models;
using GimmeCapture.ViewModels.Shared;

namespace GimmeCapture.Views.Controls;

public partial class DrawingToolbar : UserControl
{
    /// <summary>
    /// Whether the built-in Undo/Redo buttons are shown. The floating pins surface those on their main
    /// toolbar instead, so they set this false to avoid duplication; the Snip toolbar keeps the default.
    /// </summary>
    public static readonly StyledProperty<bool> ShowHistoryProperty =
        AvaloniaProperty.Register<DrawingToolbar, bool>(nameof(ShowHistory), defaultValue: true);

    public bool ShowHistory
    {
        get => GetValue(ShowHistoryProperty);
        set => SetValue(ShowHistoryProperty, value);
    }

    /// <summary>
    /// Optional theme override for the toolbar's MAIN-ROW buttons, so a host can match its own button
    /// style (the video editor passes the gold GothicToolMetalButton; Pin/Snip keep the default dark
    /// Snip themes). Flyout interiors are separate visual trees and intentionally keep the dark look.
    /// </summary>
    public static readonly StyledProperty<ControlTheme?> ButtonThemeProperty =
        AvaloniaProperty.Register<DrawingToolbar, ControlTheme?>(nameof(ButtonTheme));

    public ControlTheme? ButtonTheme
    {
        get => GetValue(ButtonThemeProperty);
        set => SetValue(ButtonThemeProperty, value);
    }

    public DrawingToolbar()
    {
        InitializeComponent();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        ApplyButtonTheme();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ButtonThemeProperty && VisualRoot != null)
        {
            ApplyButtonTheme();
        }
    }

    // Re-theme the visible main-row buttons. Buttons inside Flyouts are not visual descendants until
    // their popup opens, so this walk naturally leaves the flyout panels' dark buttons untouched.
    private void ApplyButtonTheme()
    {
        if (ButtonTheme is not { } theme)
        {
            return;
        }

        foreach (Button button in this.GetVisualDescendants().OfType<Button>().ToList())
        {
            button.Theme = theme;
        }
    }

    private void OnShapeSelected(object sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        // Use Post to ensure the SelectToolCommand has a chance to execute 
        // before the flyout is hidden, which might trigger focus/layout changes.
        Avalonia.Threading.Dispatcher.UIThread.Post(() => {
            var shapesButton = this.FindControl<Button>("ShapesButton");
            shapesButton?.Flyout?.Hide();
        });
    }

    private void OnRedactionSelected(object sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            var redactionButton = this.FindControl<Button>("RedactionButton");
            redactionButton?.Flyout?.Hide();
        });
    }

    private void OnTextToolClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (DataContext is IDrawingToolViewModel vm
                && vm.CurrentAnnotationTool != AnnotationType.Text)
            {
                HideTextFlyout();
            }
        }, DispatcherPriority.Input);
    }

    public void HideTextFlyout()
    {
        this.FindControl<Button>("TextButton")?.Flyout?.Hide();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
