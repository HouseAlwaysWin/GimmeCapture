using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using GimmeCapture.Models;
using GimmeCapture.ViewModels.Shared;

namespace GimmeCapture.Views.Controls;

public partial class DrawingToolbar : UserControl
{
    public DrawingToolbar()
    {
        InitializeComponent();
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
