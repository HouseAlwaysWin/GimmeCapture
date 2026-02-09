using Avalonia.Controls;
using Avalonia.Markup.Xaml;

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

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
